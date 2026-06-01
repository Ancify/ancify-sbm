using Ancify.SBM.Server;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

[TestClass]
public class RequestResponseTests
{
    [TestMethod]
    public async Task SendRequestAsync_HappyPath_ReturnsCorrelatedReply()
    {
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);
        server.ClientConnected += (_, e) =>
        {
            e.ClientSocket.On("echo", m => Message.FromReply(m, "got:" + m.Data));
        };

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");

        var reply = await client.SendRequestAsync(new Message("echo", "hello"));
        Assert.AreEqual("got:hello", reply.Data);

        client.Dispose();
        serverCts.Cancel();
    }

    [TestMethod]
    public async Task SendRequestAsync_NoReplyHandler_TimesOut()
    {
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);
        // No 'silent' handler is registered.

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");

        await Assert.ThrowsExceptionAsync<TimeoutException>(async () =>
            await client.SendRequestAsync(new Message("silent"), TimeSpan.FromMilliseconds(200)));

        client.Dispose();
        serverCts.Cancel();
    }

    [TestMethod]
    public async Task SendRequestAsync_PeerDisconnect_RaisesConnectionLost()
    {
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);
        ConnectedClientSocket? serverSideClient = null;
        server.ClientConnected += (_, e) =>
        {
            serverSideClient = (ConnectedClientSocket)e.ClientSocket;
            // Register a 'hold' handler that never replies, so the request stays in flight.
            e.ClientSocket.On("hold", _ => new TaskCompletionSource<Message?>().Task);
        };

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");
        await TestUtil.WaitForAsync(() => serverSideClient is not null, message: "Server-side client not seen.");

        var requestTask = client.SendRequestAsync(new Message("hold"), TimeSpan.FromSeconds(10));

        await Task.Delay(100); // let the request sit in flight on the client
        // Force the server-side client to drop, which the client will observe as Disconnected.
        serverSideClient!.Dispose();

        await Assert.ThrowsExceptionAsync<ConnectionLostException>(async () => await requestTask);

        client.Dispose();
        serverCts.Cancel();
    }

    [TestMethod]
    public async Task SendRequestAsync_ConcurrentRequests_AllReturnCorrectReplies()
    {
        // C2 regression: thousands of parallel SendRequestAsyncs must not race the
        // handler registry. Each request's reply must be correlated to its own caller.
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);
        server.ClientConnected += (_, e) =>
        {
            e.ClientSocket.On("multiply", m =>
            {
                int n = Convert.ToInt32(m.Data);
                return Message.FromReply(m, n * 2);
            });
        };

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");

        const int N = 200;
        var tasks = new Task<Message>[N];
        for (int i = 0; i < N; i++)
        {
            int captured = i;
            tasks[i] = client.SendRequestAsync(new Message("multiply", captured));
        }

        var replies = await Task.WhenAll(tasks);
        for (int i = 0; i < N; i++)
        {
            Assert.AreEqual(i * 2, Convert.ToInt32(replies[i].Data), $"Reply {i} should equal {i * 2}");
        }

        client.Dispose();
        serverCts.Cancel();
    }

    [TestMethod]
    public async Task SendAsync_ConcurrentSends_PreservePerSenderOrdering()
    {
        // Verifies message-ordering: messages sent in order from a single client
        // must be received in order by the server. (Per-connection FIFO on TCP plus
        // the streamWriteLock should preserve this.)
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);
        var received = new System.Collections.Concurrent.ConcurrentQueue<int>();
        server.ClientConnected += (_, e) =>
        {
            e.ClientSocket.On("ordered", m =>
            {
                received.Enqueue(Convert.ToInt32(m.Data));
                return Task.CompletedTask;
            });
        };

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");

        const int N = 100;
        for (int i = 0; i < N; i++)
            await client.SendAsync(new Message("ordered", i));

        await TestUtil.WaitForAsync(() => received.Count == N, TimeSpan.FromSeconds(2),
            $"Expected {N} messages, received {received.Count}.");

        int expected = 0;
        foreach (var v in received)
            Assert.AreEqual(expected++, v, "Messages must arrive in send order.");

        client.Dispose();
        serverCts.Cancel();
    }
}
