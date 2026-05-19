using Ancify.SBM.Server;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

[TestClass]
public class AuthTests
{
    [TestMethod]
    public async Task AuthenticateAsync_SuccessfulAuth_ReturnsTrueAndUpdatesStatus()
    {
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port, (id, key, _) =>
            Task.FromResult(key == "secret" ? new AuthContext(id) : AuthContext.Failed));

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50); // give the listener a tick to start

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        var ok = await client.AuthenticateAsync("alice", "secret");

        Assert.IsTrue(ok, "AuthenticateAsync should return true on success.");
        Assert.AreEqual(AuthStatus.Authenticated, client.AuthStatus, "Client AuthStatus must reflect Authenticated after success.");
        Assert.IsTrue(client.IsAuthenticated());

        client.Dispose();
        serverCts.Cancel();
    }

    [TestMethod]
    public async Task AuthenticateAsync_FailedAuth_ReturnsFalseAndMarksFailed()
    {
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        // Use Success=false but IsConnectionAllowed=true so the server returns the reply
        // before closing the connection. M6 (a race between sending the auth reply and
        // closing the transport when IsConnectionAllowed=false) is a known deferred
        // issue; this test focuses on the AuthStatus=Failed path, not that race.
        var server = TestUtil.CreateServer(port, (_, _, _) =>
            Task.FromResult(new AuthContext { Success = false, IsConnectionAllowed = true }));
        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        var ok = await client.AuthenticateAsync("alice", "wrong");

        Assert.IsFalse(ok);
        Assert.AreEqual(AuthStatus.Failed, client.AuthStatus);

        client.Dispose();
        serverCts.Cancel();
    }

    [TestMethod]
    public async Task DisallowAnonymous_BlocksHandlerInvocation_BeforeAuth()
    {
        // C10 regression: handlers must not run for unauthenticated clients when
        // DisallowAnonymous is set, even if the message channel exists.
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port,
            (id, _, _) => Task.FromResult(new AuthContext(id)),
            disallowAnonymous: true);

        int handlerInvocations = 0;
        server.ClientConnected += (_, e) =>
        {
            e.ClientSocket.On("ping", _ =>
            {
                Interlocked.Increment(ref handlerInvocations);
                return Task.CompletedTask;
            });
        };

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => server.ClientCount == 1, message: "Client did not connect.");

        // Anonymous send — must be silently dropped server-side.
        await client.SendAsync(new Message("ping"));

        await Task.Delay(150);
        Assert.AreEqual(0, handlerInvocations, "DisallowAnonymous must prevent handler from firing for unauthenticated clients.");

        // After auth, the same channel should now be reachable.
        await client.AuthenticateAsync("alice", "secret");
        await client.SendAsync(new Message("ping"));
        await TestUtil.WaitForAsync(() => Volatile.Read(ref handlerInvocations) == 1,
            message: "Authenticated client must reach the handler.");

        client.Dispose();
        serverCts.Cancel();
    }

    [TestMethod]
    public async Task AuthenticateAsync_MalformedReplyShape_DoesNotThrow()
    {
        // M4 regression: a server that returns an unexpected auth reply payload
        // (missing or wrong-typed Success key) must produce a clean 'false'
        // rather than an InvalidCastException out of AuthenticateAsync.
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();

        // Auth handler throws; ErrorHandler returns a malformed shape that lacks the Success key.
        var server = new ServerSocket(
            System.Net.IPAddress.Loopback, port, TestUtil.NoSslConfig(),
            useWebSocket: false,
            authHandler: (_, _, _) => throw new InvalidOperationException("boom"));
        server.ServerConfig.ErrorHandler = (msg, _) => Message.FromReply(msg, new { NotSuccess = 1 });
        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        bool ok = await client.AuthenticateAsync("alice", "secret");

        Assert.IsFalse(ok, "Malformed auth reply must result in false, not throw.");
        Assert.AreEqual(AuthStatus.Failed, client.AuthStatus);

        client.Dispose();
        serverCts.Cancel();
    }
}
