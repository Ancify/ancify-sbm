using System.Collections;
using System.Reflection;

using Ancify.SBM.Client;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

[TestClass]
public class MemoryHygieneTests
{
    private static IDictionary GetHandlersDict(SbmSocket socket)
    {
        var field = typeof(SbmSocket).GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IDictionary)field.GetValue(socket)!;
    }

    private static IDictionary GetPendingRequestsDict(SbmSocket socket)
    {
        var field = typeof(SbmSocket).GetField("_pendingRequests", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IDictionary)field.GetValue(socket)!;
    }

    [TestMethod]
    public async Task RegisterAndUnregisterHandler_CleansEmptyChannelEntry()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();

        var handlers = GetHandlersDict(client);
        int before = handlers.Count;

        var unregister = client.On("temp", (Message _) => { });
        Assert.AreEqual(before + 1, handlers.Count);

        unregister();
        Assert.AreEqual(before, handlers.Count, "Unregistering the last handler should drop the channel entry.");

        client.Dispose();
    }

    [TestMethod]
    public async Task UnregisterCalledTwice_NoThrow()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();

        var u = client.On("x", (Message _) => { });
        u();
        u(); // must be a no-op

        client.Dispose();
    }

    [TestMethod]
    public async Task TimeoutBurst_DoesNotLeakReplyHandlers()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("a", "k");

        for (int i = 0; i < 50; i++)
        {
            try { await client.SendRequestAsync(new Message("nowhere"), TimeSpan.FromMilliseconds(10)); }
            catch (TimeoutException) { }
        }

        var handlers = GetHandlersDict(client);
        foreach (var key in handlers.Keys)
        {
            var k = key?.ToString() ?? "";
            Assert.IsFalse(k.StartsWith("nowhere_reply_"), $"Leaked reply handler: {k}");
        }

        var pending = GetPendingRequestsDict(client);
        Assert.AreEqual(0, pending.Count, "Pending-request map must be empty after all timeouts settled.");

        client.Dispose();
    }

    [TestMethod]
    public async Task PeerDisconnect_DrainsPendingRequestsMap()
    {
        int port = TestUtil.GetFreePort();
        Ancify.SBM.Server.ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) =>
            {
                serverClient = (Ancify.SBM.Server.ConnectedClientSocket)e.ClientSocket;
                e.ClientSocket.On("hold", _ => new TaskCompletionSource<Message?>().Task);
            });

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("a", "k");
        await TestUtil.WaitForAsync(() => serverClient is not null);

        var pendingMap = GetPendingRequestsDict(client);

        var requests = new List<Task<Message>>();
        for (int i = 0; i < 10; i++)
            requests.Add(client.SendRequestAsync(new Message("hold"), TimeSpan.FromSeconds(20)));

        await TestUtil.WaitForAsync(() => pendingMap.Count == 10);

        serverClient!.Dispose();

        foreach (var r in requests)
        {
            await Assert.ThrowsExceptionAsync<ConnectionLostException>(async () => await r);
        }

        Assert.AreEqual(0, pendingMap.Count, "Pending-request map must be drained on disconnect.");
        client.Dispose();
    }

    [TestMethod]
    public async Task ConnectAndDispose_100x_NoSocketAccumulation()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);

        const int N = 100;
        for (int i = 0; i < N; i++)
        {
            var client = TestUtil.CreateClient(port);
            await client.ConnectAsync();
            client.Dispose();
        }

        // Eviction is driven by the server's receive loop observing EOF, not by the 5s
        // heartbeat — so this should resolve in well under a second even at N=100.
        await TestUtil.WaitForAsync(() => server.Server.ClientCount == 0,
            TimeSpan.FromSeconds(10),
            $"Server should have evicted all {N} disposed clients but holds {server.Server.ClientCount}.");
    }

    [TestMethod]
    public async Task SuccessfulRequest_RemovesPendingEntry()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) =>
                e.ClientSocket.On("echo", m => Message.FromReply(m, m.Data)));

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("a", "k");

        var pending = GetPendingRequestsDict(client);
        for (int i = 0; i < 20; i++)
        {
            var reply = await client.SendRequestAsync(new Message("echo", i));
            Assert.AreEqual(i, Convert.ToInt32(reply.Data));
        }

        Assert.AreEqual(0, pending.Count, "Successful requests must clean up the pending map.");
        client.Dispose();
    }
}
