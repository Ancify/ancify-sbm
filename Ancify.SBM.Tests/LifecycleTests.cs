using Ancify.SBM.Server;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

[TestClass]
public class LifecycleTests
{
    [TestMethod]
    public async Task ClientDisconnect_RemovesFromServerDict_AndFiresOnce()
    {
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);

        int disconnectCount = 0;
        server.ClientDisconnected += (_, _) => Interlocked.Increment(ref disconnectCount);

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");
        await TestUtil.WaitForAsync(() => server.ClientCount == 1, message: "Expected one connected client.");

        // Tear the client down — the server should see exactly one Disconnected event
        // (C4 regression: Dispose was reachable from multiple paths and could fire twice).
        client.Dispose();

        await TestUtil.WaitForAsync(() => server.ClientCount == 0, message: "Server did not remove disconnected client.");
        await Task.Delay(100); // small window for any racy second-Dispose to fire

        Assert.AreEqual(1, disconnectCount, "ClientDisconnected must fire exactly once per disconnect.");

        serverCts.Cancel();
    }

    [TestMethod]
    public async Task PendingRequest_LeavesNoLeakedHandler_AfterTimeout()
    {
        // C2 regression: after timeout, the per-correlation reply handler must be unregistered.
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");

        // Send a request to a channel with no handler; it must time out.
        await Assert.ThrowsExceptionAsync<TimeoutException>(async () =>
            await client.SendRequestAsync(new Message("nowhere"), TimeSpan.FromMilliseconds(100)));

        // Repeatedly do this; if handlers leaked, the protected _handlers dictionary
        // would grow indefinitely. We can't read it directly without reflection,
        // so use a behavioral check: 50 timed-out requests must all complete in the
        // expected sub-second window. (A leak wouldn't cause failures here, just a
        // slow regression, but the sanity of the assertion is the lack of OOM/hang.)
        for (int i = 0; i < 50; i++)
        {
            try
            {
                await client.SendRequestAsync(new Message("nowhere"), TimeSpan.FromMilliseconds(20));
            }
            catch (TimeoutException) { /* expected */ }
        }

        // After timeouts, the channel-level reply handler should be unregistered. Use
        // reflection to peek at the registry.
        var field = typeof(Ancify.SBM.Shared.SbmSocket)
            .GetField("_handlers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(field, "Expected _handlers field on SbmSocket.");
        var handlers = field!.GetValue(client) as System.Collections.IDictionary;
        Assert.IsNotNull(handlers, "_handlers should be enumerable as an IDictionary.");

        // Only the built-in __$status handler should remain.
        foreach (var key in handlers!.Keys)
        {
            var keyStr = key?.ToString() ?? string.Empty;
            Assert.IsFalse(keyStr.StartsWith("nowhere_reply_"),
                $"Stale reply handler leaked for channel: {keyStr}");
        }

        client.Dispose();
        serverCts.Cancel();
    }

    [TestMethod]
    public async Task DisposeIsIdempotent_OnClientSocket()
    {
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);

        int disconnectCount = 0;
        server.ClientDisconnected += (_, _) => Interlocked.Increment(ref disconnectCount);

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");
        await TestUtil.WaitForAsync(() => server.ClientCount == 1);

        // Call Dispose twice. Idempotency guard means the second call is a no-op.
        client.Dispose();
        client.Dispose();

        await TestUtil.WaitForAsync(() => server.ClientCount == 0);
        await Task.Delay(150);

        Assert.AreEqual(1, disconnectCount, "Second Dispose must not refire ClientDisconnected.");

        serverCts.Cancel();
    }
}
