using System.Net;

using Ancify.SBM.Client;
using Ancify.SBM.Server;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Model.Networking;
using Ancify.SBM.Shared.Transport.TCP;

namespace Ancify.SBM.Tests;

[TestClass]
public class PublicApiSurfaceTests
{
    [TestMethod]
    public void Message_ParameterlessCtor_DefaultsToEmptyChannel()
    {
        var m = new Message();
        Assert.AreEqual(string.Empty, m.Channel);
        Assert.IsNotNull(m.MessageId);
    }

    [TestMethod]
    public void Message_FullCtor_AssignsAllFields()
    {
        var msg = new Message("ch", "data", Guid.NewGuid());
        Assert.AreEqual("ch", msg.Channel);
        Assert.AreEqual("data", msg.Data);
        Assert.IsNotNull(msg.TargetId);
    }

    [TestMethod]
    public void Message_SerializationCtor_AssignsAllSixFields()
    {
        var reply = Guid.NewGuid();
        var msgId = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var target = Guid.NewGuid();
        var m = new Message("ch", "data", reply, msgId, sender, target);
        Assert.AreEqual("ch", m.Channel);
        Assert.AreEqual("data", m.Data);
        Assert.AreEqual(reply, m.ReplyTo);
        Assert.AreEqual(msgId, m.MessageId);
        Assert.AreEqual(sender, m.SenderId);
        Assert.AreEqual(target, m.TargetId);
    }

    [TestMethod]
    public void Message_FromReply_ConstructsReplyChannel()
    {
        var src = new Message("ping", "x");
        src.SenderId = Guid.NewGuid();
        var reply = Message.FromReply(src, "y");
        Assert.AreEqual($"ping_reply_{src.MessageId}", reply.Channel);
        Assert.AreEqual(src.MessageId, reply.ReplyTo);
        Assert.AreEqual(src.SenderId, reply.TargetId);
    }

    [TestMethod]
    public void Message_SenderIsServer_TrueWhenSenderEmpty()
    {
        var m = new Message("c");
        Assert.IsTrue(m.SenderIsServer());
        m.SenderId = Guid.NewGuid();
        Assert.IsFalse(m.SenderIsServer());
    }

    [TestMethod]
    public void Message_As_CastsData()
    {
        var m = new Message("c", 42);
        Assert.AreEqual(42, m.As<int>());
    }

    [TestMethod]
    public void AuthContext_Failed_DefaultsToUnsuccess()
    {
        var f = AuthContext.Failed;
        Assert.IsFalse(f.Success);
        Assert.IsFalse(f.IsConnectionAllowed);
    }

    [TestMethod]
    public void AuthContext_FullCtor_MarksSuccessAndConnectionAllowed()
    {
        var c = new AuthContext("user-id", new List<string> { "r" }, "scope", new { x = 1 });
        Assert.IsTrue(c.Success);
        Assert.IsTrue(c.IsConnectionAllowed);
        Assert.AreEqual("user-id", c.UserId);
        Assert.AreEqual("scope", c.Scope);
        CollectionAssert.AreEqual(new[] { "r" }, c.Roles);
    }

    [TestMethod]
    public void SslConfig_DefaultsToRejectUnauthorized()
    {
        var s = new SslConfig();
        Assert.IsTrue(s.RejectUnauthorized);
        Assert.IsFalse(s.SslEnabled);
        Assert.IsNull(s.Certificate);
    }

    [TestMethod]
    public void SbmSocketConfig_HasDefaults()
    {
        var c = new SbmSocketConfig();
        Assert.AreEqual(16 * 1024 * 1024, c.MaxFrameSize);
        Assert.AreEqual(TimeSpan.FromSeconds(60), c.ReadTimeout);
        Assert.IsNull(c.ErrorHandler);
    }

    [TestMethod]
    public void ConnectionLostException_HasMessage()
    {
        var e1 = new ConnectionLostException();
        Assert.IsFalse(string.IsNullOrEmpty(e1.Message));
        var e2 = new ConnectionLostException("custom");
        Assert.AreEqual("custom", e2.Message);
        var inner = new Exception("inner");
        var e3 = new ConnectionLostException("outer", inner);
        Assert.AreSame(inner, e3.InnerException);
    }

    [TestMethod]
    public async Task ServerSocket_GetTransport_ReturnsTransportInstance()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        Assert.IsNotNull(client.GetTransport());
        Assert.IsInstanceOfType(client.GetTransport(), typeof(TcpTransport));
        client.Dispose();
    }

    [TestMethod]
    public async Task ServerSocket_SendToClientAsync_DeliversToTarget()
    {
        int port = TestUtil.GetFreePort();
        ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) => serverClient = (ConnectedClientSocket)e.ClientSocket);

        int hits = 0;
        var client = TestUtil.CreateClient(port);
        client.On("from-server", _ => { Interlocked.Increment(ref hits); return Task.CompletedTask; });
        await client.ConnectAsync();
        await client.AuthenticateAsync("a", "k");
        await TestUtil.WaitForAsync(() => serverClient is not null);

        await server.Server.SendToClientAsync(serverClient!.ClientId, new Message("from-server"));
        await TestUtil.WaitForAsync(() => Volatile.Read(ref hits) == 1);
        client.Dispose();
    }

    [TestMethod]
    public async Task ServerSocket_SendToUnknownClient_Throws()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        await Assert.ThrowsExceptionAsync<Exception>(async () =>
            await server.Server.SendToClientAsync(Guid.NewGuid(), new Message("x")));
    }

    [TestMethod]
    public async Task ServerSocket_RemoveClient_TakesEffect()
    {
        int port = TestUtil.GetFreePort();
        ConnectedClientSocket? serverClient = null;
        using var server = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) => serverClient = (ConnectedClientSocket)e.ClientSocket);

        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => serverClient is not null);

        server.Server.RemoveClient(serverClient!.ClientId);
        // RemoveClient does not close the underlying socket — it just clears the dict.
        Assert.AreEqual(0, server.Server.ClientCount);
        client.Dispose();
    }

    [TestMethod]
    public async Task ServerSocket_Stop_IsIdempotent()
    {
        int port = TestUtil.GetFreePort();
        var server = TestUtil.CreateServer(port);
        var running = new TestUtil.RunningServer(port, server);
        await Task.Delay(50);

        running.Dispose();
        running.Dispose();
        server.Stop();
    }

    [TestMethod]
    public async Task ClientSocket_ClientIdProperty_AccessibleAfterConstruction()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        var client = TestUtil.CreateClient(port);
        _ = client.ClientId;
        await client.ConnectAsync();
        client.Dispose();
    }

    [TestMethod]
    public async Task ServerSocket_ClientCount_TracksConnections()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        Assert.AreEqual(0, server.Server.ClientCount);

        var c1 = TestUtil.CreateClient(port);
        var c2 = TestUtil.CreateClient(port);
        await c1.ConnectAsync();
        await c2.ConnectAsync();

        await TestUtil.WaitForAsync(() => server.Server.ClientCount == 2);
        c1.Dispose();
        await TestUtil.WaitForAsync(() => server.Server.ClientCount == 1);
        c2.Dispose();
        await TestUtil.WaitForAsync(() => server.Server.ClientCount == 0);
    }

    [TestMethod]
    public void EventArgs_ConstructorAndProperties()
    {
        var connArgs = new ConnectionStatusEventArgs(ConnectionStatus.Connected);
        Assert.AreEqual(ConnectionStatus.Connected, connArgs.Status);
        connArgs.Status = ConnectionStatus.Disconnected;
        Assert.AreEqual(ConnectionStatus.Disconnected, connArgs.Status);

        var connectedClientArgs = new ClientConnectedEventArgs(null!);
        Assert.IsNull(connectedClientArgs.ClientSocket);

        var disconnectedArgs = new ClientDisconnectedEventArgs(null!);
        Assert.IsNull(disconnectedArgs.ClientSocket);
    }

    [TestMethod]
    public async Task SbmSocket_OnEventType_RegistersAndUnregisters()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        var client = TestUtil.CreateClient(port);

        int hits = 0;
        var unreg = client.On<ConnectionStatusEventArgs>(EventType.ConnectionStatusChanged,
            _ => Interlocked.Increment(ref hits));

        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => Volatile.Read(ref hits) >= 1);

        int snapshot = Volatile.Read(ref hits);
        unreg();
        client.Dispose();
        await Task.Delay(100);
        // After unregister, the Dispose-driven Disconnected event should not increment.
        Assert.AreEqual(snapshot, Volatile.Read(ref hits));
    }

    [TestMethod]
    public async Task SbmSocket_OnEvent_AsyncOverload_Works()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        var client = TestUtil.CreateClient(port);

        int hits = 0;
        client.On(EventType.ConnectionStatusChanged, async _ =>
        {
            await Task.Yield();
            Interlocked.Increment(ref hits);
        });
        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => Volatile.Read(ref hits) >= 1);
        client.Dispose();
    }

    [TestMethod]
    public async Task SbmSocket_OnEvent_SyncOverload_Works()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        var client = TestUtil.CreateClient(port);

        int hits = 0;
        client.On(EventType.ConnectionStatusChanged, (object? _) => Interlocked.Increment(ref hits));
        await client.ConnectAsync();
        await TestUtil.WaitForAsync(() => Volatile.Read(ref hits) >= 1);
        client.Dispose();
    }

    [TestMethod]
    public async Task SbmSocket_OnChannel_SyncOverloads_Work()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port,
            configure: s => s.ClientConnected += (_, e) =>
            {
                e.ClientSocket.On("syncReply", (Func<Message, Message>)(m => Message.FromReply(m, "synced")));
                e.ClientSocket.On("syncVoid", (Action<Message>)(_ => { }));
            });
        var client = TestUtil.CreateClient(port);
        await client.ConnectAsync();
        await client.AuthenticateAsync("a", "k");
        var reply = await client.SendRequestAsync(new Message("syncReply"), TimeSpan.FromSeconds(2));
        Assert.AreEqual("synced", reply.Data);
        await client.SendAsync(new Message("syncVoid"));
        client.Dispose();
    }

    [TestMethod]
    public void SbmUtils_ToConvertedList_NonArrayReturnsEmpty()
    {
        var result = SbmUtils.ToConvertedList<string>("not-an-array", _ => "x");
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task ServerSocket_DisallowAnonymous_Property_ReportsTrue()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port, disallowAnonymous: true);
        Assert.IsTrue(server.Server.AnonymousDisallowed);
    }

    [TestMethod]
    public void SbmLogger_Get_DoesNotThrow()
    {
        // Other tests in this assembly may have set a logger; we only care that the
        // accessor itself is safe to call regardless of init state.
        _ = SbmLogger.Get();
    }

    [TestMethod]
    public async Task ServerSocket_AuthHandler_PropertyExposesHandler()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        Assert.IsNotNull(server.Server.AuthHandler);
    }

    [TestMethod]
    public async Task TcpTransport_CloseAndDispose_BothNoThrow()
    {
        int port = TestUtil.GetFreePort();
        using var server = await TestUtil.StartServerAsync(port);
        var t = TestUtil.CreateClientTransport(port);
        await t.ConnectAsync();
        t.Close();
        t.Dispose();
        t.Close();
    }

    [TestMethod]
    public void TcpTransport_AlwaysReconnect_RoundTrips()
    {
        var t = new TcpTransport("127.0.0.1", 65000, TestUtil.NoSslConfig()) { AlwaysReconnect = true };
        Assert.IsTrue(t.AlwaysReconnect);
        t.AlwaysReconnect = false;
        Assert.IsFalse(t.AlwaysReconnect);
        t.MaxConnectWaitTime = 5000;
        Assert.AreEqual(5000, t.MaxConnectWaitTime);
        t.MaxFrameSize = 1024;
        Assert.AreEqual(1024, t.MaxFrameSize);
        t.Dispose();
    }
}
