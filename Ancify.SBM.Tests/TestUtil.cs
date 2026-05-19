using System.Net;
using System.Net.Sockets;

using Ancify.SBM.Client;
using Ancify.SBM.Server;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Transport.TCP;

namespace Ancify.SBM.Tests;

internal static class TestUtil
{
    public static SslConfig NoSslConfig() => new() { SslEnabled = false, RejectUnauthorized = false };

    /// <summary>
    /// Reserve an ephemeral TCP port by binding-then-closing a TcpListener.
    /// There is a tiny race where another process can claim the port between
    /// the close and the next bind, but it is extremely unlikely on loopback
    /// in a test run.
    /// </summary>
    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static ServerSocket CreateServer(
        int port,
        Func<string, string, string, Task<AuthContext>>? authHandler = null,
        bool disallowAnonymous = false)
    {
        authHandler ??= (id, _, _) => Task.FromResult(new AuthContext(id, new List<string> { "user" }));
        var server = new ServerSocket(IPAddress.Loopback, port, NoSslConfig(), useWebSocket: false, authHandler);
        if (disallowAnonymous) server.DisallowAnonymous();
        return server;
    }

    public static TcpTransport CreateClientTransport(int port, bool alwaysReconnect = false)
    {
        return new TcpTransport("127.0.0.1", (ushort)port, NoSslConfig()) { AlwaysReconnect = alwaysReconnect };
    }

    public static ClientSocket CreateClient(int port, bool alwaysReconnect = false)
    {
        return new ClientSocket(CreateClientTransport(port, alwaysReconnect));
    }

    /// <summary>
    /// Spin until a predicate is true, or fail with a clear message. Used to wait
    /// for asynchronous events (e.g. ClientConnected handler) without sleeping for
    /// a fixed amount in normal-path tests.
    /// </summary>
    public static async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null, string? message = null)
    {
        timeout ??= TimeSpan.FromSeconds(3);
        var deadline = DateTime.UtcNow + timeout.Value;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.Fail(message ?? "Predicate did not become true within the timeout.");
    }
}
