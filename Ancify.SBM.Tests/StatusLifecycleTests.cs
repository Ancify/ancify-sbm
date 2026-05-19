using System.Collections.Concurrent;

using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

[TestClass]
public class StatusLifecycleTests
{
    private static ConcurrentQueue<ConnectionStatus> CaptureStatus(SbmSocket socket)
    {
        var q = new ConcurrentQueue<ConnectionStatus>();
        socket.On<ConnectionStatusEventArgs>(EventType.ConnectionStatusChanged, args => q.Enqueue(args.Status));
        return q;
    }

    [TestMethod]
    public async Task FreshConnect_GoesThroughConnecting_Then_Connected()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);

        var client = TestUtil.CreateClient(port);
        var statuses = CaptureStatus(client);

        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => statuses.Contains(ConnectionStatus.Connected));

        var arr = statuses.ToArray();
        Assert.IsTrue(arr.Length >= 2, $"Expected at least Connecting+Connected, saw {string.Join(",", arr)}");
        Assert.AreEqual(ConnectionStatus.Connecting, arr[0]);
        Assert.AreEqual(ConnectionStatus.Connected, arr[1]);

        client.Dispose();
    }

    [TestMethod]
    public async Task SuccessfulAuth_FiresAuthenticated()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);

        var client = TestUtil.CreateClient(port);
        var statuses = CaptureStatus(client);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "key");

        await TestUtil.WaitForAsync(() => statuses.Contains(ConnectionStatus.Authenticated));
        client.Dispose();
    }

    [TestMethod]
    public async Task PeerClose_FiresDisconnected()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);

        var client = TestUtil.CreateClient(port);
        var statuses = CaptureStatus(client);
        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => server.Server.ClientCount == 1);

        server.Dispose();
        await TestUtil.WaitForAsync(() => statuses.Contains(ConnectionStatus.Disconnected),
            TimeSpan.FromSeconds(5));

        client.Dispose();
    }

    [TestMethod]
    public async Task Reconnect_FiresReconnecting_Then_Reconnected()
    {
        int port = TestUtil.GetFreePort();
        using var first = await TestUtil.StartServerAsync(port);

        var client = TestUtil.CreateClient(port, alwaysReconnect: true);
        var statuses = CaptureStatus(client);
        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => first.Server.ClientCount == 1);

        first.Dispose();
        await Task.Delay(200);

        using var second = await TestUtil.StartServerAsync(port);
        await TestUtil.WaitForAsync(() => statuses.Contains(ConnectionStatus.Reconnected),
            TimeSpan.FromSeconds(8),
            $"Status log: [{string.Join(",", statuses)}]");

        Assert.IsTrue(statuses.Contains(ConnectionStatus.Reconnecting), "Reconnecting must precede Reconnected.");

        client.Dispose();
    }

    [TestMethod]
    public async Task ConnectFailure_ExhaustingRetries_FiresFailed()
    {
        int port = TestUtil.GetFreePort();
        var transport = TestUtil.CreateClientTransport(port);
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        transport.ConnectionStatusChanged += (_, e) => statuses.Enqueue(e.Status);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await transport.ConnectAsync(maxRetries: 2, delayMilliseconds: 10));

        Assert.IsTrue(statuses.Contains(ConnectionStatus.Failed), $"Expected Failed, saw [{string.Join(",", statuses)}]");
        transport.Dispose();
    }

    [TestMethod]
    public async Task IsAuthenticated_TogglesWithStatus()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        Assert.IsFalse(client.IsAuthenticated());

        await client.AuthenticateAsync("alice", "key");
        Assert.IsTrue(client.IsAuthenticated());

        client.Dispose();
    }

    [TestMethod]
    public async Task AuthenticationGuard_ThrowsBeforeAuth_PassesAfter()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        Assert.ThrowsException<UnauthorizedAccessException>(() => client.AuthenticationGuard());

        await client.AuthenticateAsync("alice", "key");
        client.AuthenticationGuard(); // no throw

        client.Dispose();
    }
}
