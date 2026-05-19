using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

[TestClass]
public class ConcurrencyTests
{
    [TestMethod]
    public async Task ConcurrentSendAndRequest_OnSameSocket_NoExceptions()
    {
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);
        int fireAndForgetCount = 0;
        server.ClientConnected += (_, e) =>
        {
            e.ClientSocket.On("fire", _ => { Interlocked.Increment(ref fireAndForgetCount); return Task.CompletedTask; });
            e.ClientSocket.On("echo", m => Message.FromReply(m, m.Data));
        };

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");

        const int N = 100;
        var senders = new List<Task>();
        var requesters = new List<Task<Message>>();

        for (int i = 0; i < N; i++)
        {
            senders.Add(client.SendAsync(new Message("fire", i)));
            requesters.Add(client.SendRequestAsync(new Message("echo", i)));
        }

        await Task.WhenAll(senders);
        var replies = await Task.WhenAll(requesters);

        Assert.AreEqual(N, replies.Length);
        for (int i = 0; i < N; i++)
            Assert.AreEqual(i, Convert.ToInt32(replies[i].Data));

        await TestUtil.WaitForAsync(() => fireAndForgetCount == N, message: $"Expected {N} fire-and-forget, got {fireAndForgetCount}.");

        client.Dispose();
        serverCts.Cancel();
    }
}
