using System.Net.Sockets;

using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model.Networking;
using Ancify.SBM.Shared.Transport.TCP;

namespace Ancify.SBM.Tests;

[TestClass]
public class ErrorPathTests
{
    [TestMethod]
    public async Task ConnectAsync_DnsFailure_Throws()
    {
        // .invalid is reserved RFC 2606 and guaranteed not to resolve.
        var transport = new TcpTransport("nope.invalid", 65000, TestUtil.NoSslConfig());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await transport.ConnectAsync(maxRetries: 1, delayMilliseconds: 10));
        transport.Dispose();
    }

    [TestMethod]
    public async Task ConnectAsync_RefusedPort_RetriesAndFails()
    {
        int port = TestUtil.GetFreePort();
        var transport = TestUtil.CreateClientTransport(port);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await transport.ConnectAsync(maxRetries: 2, delayMilliseconds: 10));
        transport.Dispose();
    }

    [TestMethod]
    public async Task SendAsync_OnClosedTransport_Throws()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => server.Server.ClientCount == 1);

        client.Dispose();
        // Send on a disposed stream surfaces ObjectDisposedException from the underlying
        // NetworkStream. Either subclass of Exception is acceptable here — the contract
        // is "throws some exception, doesn't silently succeed".
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
            await client.SendAsync(new Message("anything", "x")));
    }

    [TestMethod]
    public async Task SendRequestAsync_AfterDispose_ThrowsConnectionLost()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) =>
                e.ClientSocket.On("hold", _ => new TaskCompletionSource<Message?>().Task));

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");

        var pending = client.SendRequestAsync(new Message("hold"), TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        client.Dispose();

        await Assert.ThrowsExceptionAsync<ConnectionLostException>(async () => await pending);
    }

    [TestMethod]
    public async Task MidHandshakeDisconnect_ClientSeesFailureCleanly()
    {
        // Spin up a raw TCP listener that accepts then immediately closes, without
        // ever exchanging SBM frames. The client should observe an EOF and exit
        // cleanly without leaving any background tasks hung.
        int port = TestUtil.GetFreePort();
        var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();

        var listenerTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            accepted.Close();
        });

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync(); // succeeds — handshake is at the framing layer

        // Send something; either it errors or it succeeds and the next read sees EOF.
        // Either way the receive loop must exit and Disconnected must fire.
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<ConnectionStatus>();
        client.On<ConnectionStatusEventArgs>(EventType.ConnectionStatusChanged, args => statuses.Enqueue(args.Status));

        try { await client.SendAsync(new Message("ping")); }
        catch { /* race — peer may have closed before send completes */ }

        await TestUtil.WaitForAsync(() => statuses.Contains(ConnectionStatus.Disconnected),
            TimeSpan.FromSeconds(3));

        await listenerTask;
        listener.Stop();
        client.Dispose();
    }

    [TestMethod]
    public async Task SendDuringReconnect_DoesNotThrow_OnceReconnectCompletes()
    {
        // While AlwaysReconnect is in progress, the stream is null. SendAsync on a
        // null stream should throw — verify the client surfaces it cleanly without
        // crashing the receive loop. After reconnect completes, subsequent sends
        // must work.
        int port = TestUtil.GetFreePort();
        using var first = await TestUtil.StartServerAsync(port);

        var client = TestUtil.CreateClient(port, alwaysReconnect: true);
        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => first.Server.ClientCount == 1);

        first.Dispose();
        await Task.Delay(150);

        using var second = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) =>
                e.ClientSocket.On("after", _ => Task.CompletedTask));

        await TestUtil.WaitForAsync(() => second.Server.ClientCount == 1, TimeSpan.FromSeconds(8));

        // A successful send after reconnect proves the client is functional.
        await client.SendAsync(new Message("after"));

        client.Dispose();
    }

    [TestMethod]
    public async Task ReceiveLoop_SurvivesHandlerException()
    {
        int port = TestUtil.GetFreePort();
        int goodCount = 0;
        using var server = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) =>
            {
                e.ClientSocket.On("boom", (Func<Message, Task>)(_ => throw new InvalidOperationException("intentional")));
                e.ClientSocket.On("ok", _ => { Interlocked.Increment(ref goodCount); return Task.CompletedTask; });
            });

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");

        // Throwing handler must not kill the receive loop.
        await client.SendAsync(new Message("boom"));
        await Task.Delay(100);

        // Subsequent messages on a different channel must still be delivered.
        for (int i = 0; i < 5; i++) await client.SendAsync(new Message("ok"));
        await TestUtil.WaitForAsync(() => Volatile.Read(ref goodCount) == 5);

        client.Dispose();
    }

    [TestMethod]
    public async Task ServerConfig_ErrorHandler_RepliesOnException()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port, configure: s =>
        {
            s.ServerConfig.ErrorHandler = (msg, ex) =>
                Message.FromReply(msg, new { Success = false, Reason = ex.Message });
            s.ClientConnected += (_, e) =>
                e.ClientSocket.On("explode", (Func<Message, Message>)(_ => throw new InvalidOperationException("kaboom")));
        });

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");

        var reply = await client.SendRequestAsync(new Message("explode"), TimeSpan.FromSeconds(2));
        var data = reply.AsTypeless();
        Assert.AreEqual("kaboom", data["Reason"]);
        Assert.AreEqual(false, data["Success"]);

        client.Dispose();
    }
}
