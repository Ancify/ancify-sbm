using Ancify.SBM.Server;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

[TestClass]
public class ReconnectTests
{
    [TestMethod]
    public async Task ClientReconnects_AfterServerRestart_AndCanSend()
    {
        // Verify AlwaysReconnect behavior: the client must reconnect to a server
        // that has been restarted on the same port, and post-reconnect sends must
        // reach the new server. Re-authentication after reconnect (M8) is deferred,
        // so we send to an anonymous channel.

        int port = TestUtil.GetFreePort();
        using var firstServerCts = new CancellationTokenSource();
        var firstServer = TestUtil.CreateServer(port);
        _ = firstServer.StartAsync(firstServerCts.Token);
        await Task.Delay(50);

        var statusLog = new System.Collections.Concurrent.ConcurrentQueue<ConnectionStatus>();
        var client = TestUtil.CreateClient(port, alwaysReconnect: true);
        client.On<ConnectionStatusEventArgs>(EventType.ConnectionStatusChanged, args => statusLog.Enqueue(args.Status));

        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => firstServer.ClientCount == 1, message: "First server saw no connection.");

        // Fully stop the first server: cancel the accept loop AND release the listener
        // so the second server can bind on the same port.
        firstServerCts.Cancel();
        firstServer.Stop();
        await Task.Delay(300);

        // Start a fresh server on the same port.
        using var secondServerCts = new CancellationTokenSource();
        int receivedAfterReconnect = 0;
        var secondServer = TestUtil.CreateServer(port);
        secondServer.ClientConnected += (_, e) =>
        {
            e.ClientSocket.On("post-reconnect", _ =>
            {
                Interlocked.Increment(ref receivedAfterReconnect);
                return Task.CompletedTask;
            });
        };
        _ = secondServer.StartAsync(secondServerCts.Token);

        // The client's receive loop should observe the dropped connection and the
        // AlwaysReconnect path should reconnect to the now-running second server.
        await TestUtil.WaitForAsync(() => secondServer.ClientCount == 1,
            TimeSpan.FromSeconds(8),
            $"Client did not reconnect (second server saw {secondServer.ClientCount} clients). " +
            $"Status log: [{string.Join(", ", statusLog)}]");

        // The reconnected client should be able to send.
        await client.SendAsync(new Message("post-reconnect"));
        await TestUtil.WaitForAsync(() => Volatile.Read(ref receivedAfterReconnect) == 1,
            TimeSpan.FromSeconds(2),
            "Reconnected client could not send a message to the new server.");

        client.Dispose();
        secondServerCts.Cancel();
        secondServer.Stop();
    }
}
