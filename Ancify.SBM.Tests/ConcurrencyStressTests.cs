using System.Collections.Concurrent;

using Ancify.SBM.Client;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

[TestClass]
public class ConcurrencyStressTests
{
    [TestMethod]
    public async Task ManyConcurrentClients_AllAuthSucceed()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);

        const int N = 30;
        var clients = new ClientSocket[N];
        for (int i = 0; i < N; i++) clients[i] = TestUtil.CreateClient(port);

        // Parallel connect + auth.
        await Task.WhenAll(clients.Select(async c =>
        {
            await c.ConnectAsync();
            var ok = await c.AuthenticateAsync("u", "k");
            Assert.IsTrue(ok);
        }));

        await TestUtil.WaitForAsync(() => server.Server.ClientCount == N,
            TimeSpan.FromSeconds(5),
            $"Expected {N} clients, server saw {server.Server.ClientCount}.");

        foreach (var c in clients) c.Dispose();
    }

    [TestMethod]
    public async Task RequestBurst_FromManyClients_AllCorrelated()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) =>
                e.ClientSocket.On("inc", m => Message.FromReply(m, Convert.ToInt32(m.Data) + 1)));

        const int N = 10;
        const int RequestsPerClient = 50;
        var clients = new ClientSocket[N];
        for (int i = 0; i < N; i++)
        {
            clients[i] = TestUtil.CreateClient(port);
            await clients[i].ConnectAsync();
            await clients[i].AuthenticateAsync("u" + i, "k");
        }

        var tasks = new List<Task<Message>>();
        for (int c = 0; c < N; c++)
        {
            for (int r = 0; r < RequestsPerClient; r++)
            {
                int val = c * 1000 + r;
                tasks.Add(clients[c].SendRequestAsync(new Message("inc", val)));
            }
        }

        var replies = await Task.WhenAll(tasks);
        for (int c = 0; c < N; c++)
        {
            for (int r = 0; r < RequestsPerClient; r++)
            {
                int idx = c * RequestsPerClient + r;
                int expected = c * 1000 + r + 1;
                Assert.AreEqual(expected, Convert.ToInt32(replies[idx].Data));
            }
        }

        foreach (var c in clients) c.Dispose();
    }

    [TestMethod]
    public async Task MixedSendReceive_DuringReconnect_NoCorruption()
    {
        int port = TestUtil.GetFreePort();
        var firstReceived = new ConcurrentBag<int>();
        var firstServer = TestUtil.CreateServer(port);
        firstServer.ClientConnected += (_, e) =>
            e.ClientSocket.On("v", m => { firstReceived.Add(Convert.ToInt32(m.Data)); return Task.CompletedTask; });
        var firstRunning = new TestUtil.RunningServer(port, firstServer);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port, alwaysReconnect: true);
        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => firstServer.ClientCount == 1);

        for (int i = 0; i < 20; i++) await client.SendAsync(new Message("v", i));
        await TestUtil.WaitForAsync(() => firstReceived.Count == 20);

        firstRunning.Dispose();
        await Task.Delay(150);

        var secondReceived = new ConcurrentBag<int>();
        using var secondServer = await TestUtil.StartServerAsync(port, configure: s =>
            s.ClientConnected += (_, e) =>
                e.ClientSocket.On("v", m => { secondReceived.Add(Convert.ToInt32(m.Data)); return Task.CompletedTask; }));

        await TestUtil.WaitForAsync(() => secondServer.Server.ClientCount == 1, TimeSpan.FromSeconds(8));

        for (int i = 100; i < 120; i++) await client.SendAsync(new Message("v", i));
        await TestUtil.WaitForAsync(() => secondReceived.Count == 20, TimeSpan.FromSeconds(5));

        // Pre-reconnect and post-reconnect message sets must be disjoint and complete.
        Assert.AreEqual(20, firstReceived.Count);
        Assert.AreEqual(20, secondReceived.Count);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 20).ToArray(), firstReceived.OrderBy(x => x).ToArray());
        CollectionAssert.AreEquivalent(Enumerable.Range(100, 20).ToArray(), secondReceived.OrderBy(x => x).ToArray());

        client.Dispose();
    }

    [TestMethod]
    public async Task ConcurrentBroadcast_DeliversToAllClients()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);

        const int N = 8;
        var counters = new int[N];
        var clients = new ClientSocket[N];
        for (int i = 0; i < N; i++)
        {
            int captured = i;
            clients[i] = TestUtil.CreateClient(port);
            clients[i].On("bcast", _ => { Interlocked.Increment(ref counters[captured]); return Task.CompletedTask; });
            await clients[i].ConnectAsync();
            await clients[i].AuthenticateAsync("u" + i, "k");
        }

        await TestUtil.WaitForAsync(() => server.Server.ClientCount == N);

        await server.Server.BroadcastAsync(new Message("bcast", "hi"));
        await TestUtil.WaitForAsync(() => counters.All(c => c == 1));

        foreach (var c in clients) c.Dispose();
    }
}
