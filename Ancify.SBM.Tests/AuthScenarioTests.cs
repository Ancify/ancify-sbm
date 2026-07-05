using Ancify.SBM.Server;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

[TestClass]
public class AuthScenarioTests
{
    [TestMethod]
    public async Task ScopedAuth_PassesGuardForMatchingScope()
    {
        int port = TestUtil.GetFreePort();
        ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            authHandler: (id, _, _) => Task.FromResult(new AuthContext(id, new List<string> { "admin" }, scope: "tenant-a")),
            configure: s => s.ClientConnected += (_, e) => serverClient = (ConnectedClientSocket)e.ClientSocket);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "k", scope: "tenant-a");
        await TestUtil.WaitForAsync(() => serverClient is not null && serverClient.IsAuthenticated());

        serverClient!.AuthenticationGuard(role: "admin", scope: "tenant-a"); // no throw
        client.Dispose();
    }

    [TestMethod]
    public async Task ScopedAuth_FailsGuardForWrongScope()
    {
        int port = TestUtil.GetFreePort();
        ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            authHandler: (id, _, _) => Task.FromResult(new AuthContext(id, scope: "tenant-a")),
            configure: s => s.ClientConnected += (_, e) => serverClient = (ConnectedClientSocket)e.ClientSocket);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "k");
        await TestUtil.WaitForAsync(() => serverClient is not null && serverClient.IsAuthenticated());

        Assert.ThrowsException<UnauthorizedAccessException>(
            () => serverClient!.AuthenticationGuard(scope: "tenant-b"));
        client.Dispose();
    }

    [TestMethod]
    public async Task AuthenticationGuardAny_PassesIfAnyRoleMatches()
    {
        int port = TestUtil.GetFreePort();
        ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            authHandler: (id, _, _) => Task.FromResult(new AuthContext(id, new List<string> { "viewer" })),
            configure: s => s.ClientConnected += (_, e) => serverClient = (ConnectedClientSocket)e.ClientSocket);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "k");
        await TestUtil.WaitForAsync(() => serverClient is not null && serverClient.IsAuthenticated());

        serverClient!.AuthenticationGuardAny(roles: new[] { "admin", "viewer" }); // viewer matches
        client.Dispose();
    }

    [TestMethod]
    public async Task AuthenticationGuardAny_RolesOnly_RejectsWhenNoRoleMatches()
    {
        // Regression guard: a single-argument GuardAny(roles: …) must actually enforce the
        // roles. It historically could never throw (the unsupplied scopes dimension defaulted
        // to "satisfied"), which silently admitted any authenticated principal.
        int port = TestUtil.GetFreePort();
        ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            authHandler: (id, _, _) => Task.FromResult(new AuthContext(id, new List<string> { "viewer" })),
            configure: s => s.ClientConnected += (_, e) => serverClient = (ConnectedClientSocket)e.ClientSocket);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "k");
        await TestUtil.WaitForAsync(() => serverClient is not null && serverClient.IsAuthenticated());

        Assert.ThrowsException<UnauthorizedAccessException>(
            () => serverClient!.AuthenticationGuardAny(roles: new[] { "admin" }));
        client.Dispose();
    }

    [TestMethod]
    public async Task AuthenticationGuardAny_ScopesOnly_RejectsWhenNeitherScopeNorRoleMatches()
    {
        int port = TestUtil.GetFreePort();
        ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            authHandler: (id, _, _) => Task.FromResult(new AuthContext(id, new List<string> { "viewer" }, scope: "tenant-a")),
            configure: s => s.ClientConnected += (_, e) => serverClient = (ConnectedClientSocket)e.ClientSocket);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "k", scope: "tenant-a");
        await TestUtil.WaitForAsync(() => serverClient is not null && serverClient.IsAuthenticated());

        Assert.ThrowsException<UnauthorizedAccessException>(
            () => serverClient!.AuthenticationGuardAny(scopes: new[] { "admin", "dev" }));
        client.Dispose();
    }

    [TestMethod]
    public async Task AuthenticationGuardAny_ScopesOnly_PassesWhenRequestedScopeHeldAsRole()
    {
        // API-key / portal principals carry authority in Context.Roles under a generic scope
        // ("apikey" / "portal"); a scope guard must accept them when they hold the scope name
        // as a role. This is the case the strict fix must NOT regress.
        int port = TestUtil.GetFreePort();
        ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            authHandler: (id, _, _) => Task.FromResult(new AuthContext(id, new List<string> { "hoster" }, scope: "apikey")),
            configure: s => s.ClientConnected += (_, e) => serverClient = (ConnectedClientSocket)e.ClientSocket);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "k", scope: "apikey");
        await TestUtil.WaitForAsync(() => serverClient is not null && serverClient.IsAuthenticated());

        serverClient!.AuthenticationGuardAny(scopes: new[] { "hoster", "dev" }); // no throw
        client.Dispose();
    }

    [TestMethod]
    public async Task AuthenticationGuardAll_FailsIfAnyRoleMissing()
    {
        int port = TestUtil.GetFreePort();
        ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            authHandler: (id, _, _) => Task.FromResult(new AuthContext(id, new List<string> { "viewer" })),
            configure: s => s.ClientConnected += (_, e) => serverClient = (ConnectedClientSocket)e.ClientSocket);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "k");
        await TestUtil.WaitForAsync(() => serverClient is not null && serverClient.IsAuthenticated());

        Assert.ThrowsException<UnauthorizedAccessException>(
            () => serverClient!.AuthenticationGuardAll(roles: new[] { "viewer", "admin" }));
        client.Dispose();
    }

    [TestMethod]
    public async Task AnonymousAllowedByDefault_HandlersRun()
    {
        int port = TestUtil.GetFreePort();
        int hits = 0;
        using var server = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) =>
                e.ClientSocket.On("anon", _ => { Interlocked.Increment(ref hits); return Task.CompletedTask; }));

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        // ClientConnected runs in a Task.Run (C9 fix), so the handler may not be wired
        // synchronously when SendAsync would otherwise race ahead.
        await TestUtil.WaitForAsync(() => server.Server.ClientCount == 1);
        await Task.Delay(50);
        await client.SendAsync(new Message("anon"));
        await TestUtil.WaitForAsync(() => Volatile.Read(ref hits) == 1);
        client.Dispose();
    }

    [TestMethod]
    public async Task DisallowAnonymous_AllowsAuthChannel_BlocksOthers()
    {
        int port = TestUtil.GetFreePort();
        int otherHits = 0;
        using var server = await TestUtil.StartServerAsync(port,
            disallowAnonymous: true,
            configure: s => s.ClientConnected += (_, e) =>
                e.ClientSocket.On("other", _ => { Interlocked.Increment(ref otherHits); return Task.CompletedTask; }));

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();

        // _auth_ is the one channel that DisallowAnonymous lets through pre-auth.
        var ok = await client.AuthenticateAsync("alice", "k");
        Assert.IsTrue(ok);

        await client.SendAsync(new Message("other"));
        await TestUtil.WaitForAsync(() => Volatile.Read(ref otherHits) == 1);

        client.Dispose();
    }

    [TestMethod]
    public async Task FailedAuth_RetrySucceedsOnSecondAttempt()
    {
        int port = TestUtil.GetFreePort();
        int attempts = 0;
        using var server = await TestUtil.StartServerAsync(port,
            authHandler: (id, key, _) =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(key == "right"
                    ? new AuthContext(id)
                    : new AuthContext { Success = false, IsConnectionAllowed = true });
            });

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();

        Assert.IsFalse(await client.AuthenticateAsync("alice", "wrong"));
        Assert.AreEqual(AuthStatus.Failed, client.AuthStatus);
        Assert.IsTrue(await client.AuthenticateAsync("alice", "right"));
        Assert.AreEqual(AuthStatus.Authenticated, client.AuthStatus);
        Assert.IsTrue(attempts >= 2);

        client.Dispose();
    }

    [TestMethod]
    public async Task AuthenticationGuard_DefaultArgs_AcceptsAnyAuthenticatedClient()
    {
        int port = TestUtil.GetFreePort();
        ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) => serverClient = (ConnectedClientSocket)e.ClientSocket);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("alice", "k");
        await TestUtil.WaitForAsync(() => serverClient is not null && serverClient.IsAuthenticated());

        serverClient!.AuthenticationGuard();
        serverClient.AuthenticationGuardAny();
        serverClient.AuthenticationGuardAll();
        client.Dispose();
    }
}
