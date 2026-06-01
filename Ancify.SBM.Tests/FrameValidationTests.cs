using System.Net.Sockets;

using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

/// <summary>
/// Drives raw bytes at the server's listening port to test the framing layer
/// in isolation. The handshake is plaintext for these tests (no TLS).
/// </summary>
[TestClass]
public class FrameValidationTests
{
    [TestMethod]
    public async Task OversizeLengthPrefix_DoesNotAllocateOrCrashServer()
    {
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);
        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        using var attacker = new TcpClient();
        await attacker.ConnectAsync("127.0.0.1", port);
        var stream = attacker.GetStream();

        // 0x7FFFFFFF = ~2 GiB. Default MaxFrameSize is 16 MiB, so this must be rejected
        // and the receive loop must terminate without trying to allocate the buffer.
        byte[] oversize = BitConverter.GetBytes(0x7FFFFFFF);
        await stream.WriteAsync(oversize);
        await stream.FlushAsync();

        // Server should close the connection. Confirm via subsequent read returning 0
        // within a reasonable window.
        var buf = new byte[1];
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        int read;
        try { read = await stream.ReadAsync(buf, cts.Token); }
        catch (OperationCanceledException) { read = -1; }

        Assert.AreEqual(0, read, "Server should close the connection after rejecting an oversize frame.");

        // The server itself must still be running and the bad client removed.
        await TestUtil.WaitForAsync(() => server.ClientCount == 0,
            message: "Server did not evict the rejected client.");
        serverCts.Cancel();
    }

    [TestMethod]
    public async Task NegativeLengthPrefix_IsRejected()
    {
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);
        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        using var attacker = new TcpClient();
        await attacker.ConnectAsync("127.0.0.1", port);
        var stream = attacker.GetStream();

        byte[] negative = BitConverter.GetBytes(-1);
        await stream.WriteAsync(negative);
        await stream.FlushAsync();

        var buf = new byte[1];
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        int read;
        try { read = await stream.ReadAsync(buf, cts.Token); }
        catch (OperationCanceledException) { read = -1; }
        Assert.AreEqual(0, read, "Server should close the connection on negative frame length.");

        serverCts.Cancel();
    }

    [TestMethod]
    public async Task TruncatedPayload_DoesNotYieldGarbage()
    {
        // Send a valid length prefix then close before sending the payload bytes.
        // The server must not try to deserialize a half-filled buffer.
        int port = TestUtil.GetFreePort();
        using var serverCts = new CancellationTokenSource();
        var server = TestUtil.CreateServer(port);

        int handlerCalls = 0;
        server.ClientConnected += (_, e) =>
        {
            e.ClientSocket.On("anything", _ => { Interlocked.Increment(ref handlerCalls); return Task.CompletedTask; });
        };

        _ = server.StartAsync(serverCts.Token);
        await Task.Delay(50);

        using (var attacker = new TcpClient())
        {
            await attacker.ConnectAsync("127.0.0.1", port);
            var stream = attacker.GetStream();
            // Claim a 100-byte payload then immediately close.
            await stream.WriteAsync(BitConverter.GetBytes(100));
            await stream.FlushAsync();
            // Close happens automatically when the using block exits.
        }

        await Task.Delay(200);
        Assert.AreEqual(0, handlerCalls, "Truncated frame must not produce a deserialized message.");
        serverCts.Cancel();
    }
}
