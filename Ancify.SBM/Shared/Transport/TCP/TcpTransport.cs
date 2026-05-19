using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared.Model.Networking;

using MessagePack;

using Microsoft.Extensions.Logging;

namespace Ancify.SBM.Shared.Transport.TCP;

public class SslConfig
{
    public X509Certificate2? Certificate { get; set; }
    public bool SslEnabled { get; set; }
    public bool RejectUnauthorized { get; set; } = true;
}

public class TcpTransport : ITransport, IDisposable
{
    private TcpClient _client;
    private Stream _stream;
    private readonly CancellationTokenSource _cts;
    private readonly string _host;
    private readonly ushort _port;
    public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;
    private readonly bool _isServer = false;
    private bool _isSettingUpSsl = false;
    private readonly SslConfig _sslConfig;
    private readonly SemaphoreSlim _streamWriteLock = new(1, 1);
    private int _disposed = 0;

    public TcpClient Client { get => _client; }

    public bool AlwaysReconnect { get; set; }
    public int MaxConnectWaitTime { get; set; } = 60 * 1000;

    /// <summary>
    /// Maximum permitted size of a single inbound frame payload in bytes.
    /// Frames whose length prefix exceeds this value (or is negative) cause
    /// the receive loop to terminate without allocating the payload buffer.
    /// Defaults to 16 MiB; callers can lower or raise this before connecting.
    /// </summary>
    public int MaxFrameSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Per-frame read timeout. Once at least one byte of a frame has begun
    /// arriving, the rest of the length prefix + payload must arrive within
    /// this window or the connection is torn down. TimeSpan.Zero disables
    /// the timeout entirely.
    /// </summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(60);

    // Server constructor – accepts an already connected TcpClient
    public TcpTransport(TcpClient client, SslConfig sslConfig)
    {
        _client = client;
        _cts = new CancellationTokenSource();
        _host = ((IPEndPoint)_client.Client.RemoteEndPoint!).Address.ToString();
        _port = (ushort)((IPEndPoint)_client.Client.RemoteEndPoint!).Port;
        _isServer = true;
        _sslConfig = sslConfig;
        _stream = client.GetStream();
        SetupTcpSocket();
    }

    // Client constructor – connection will be initiated later in ConnectAsync
    public TcpTransport(string host, ushort port, SslConfig sslConfig)
    {
        _host = host;
        _port = port;
        _client = new TcpClient();
        _cts = new CancellationTokenSource();
        _stream = null!;
        _isServer = false;
        _sslConfig = sslConfig;
        SetupTcpSocket();
    }

    private void SetupTcpSocket()
    {
        // These are actually only for blocking calls and thus completely useless ._.

        if (_client is not null)
        {
            _client.SendTimeout = 0;
            _client.ReceiveTimeout = 0;

            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }


        if (_stream is not null)
        {
            _stream.WriteTimeout = Timeout.Infinite;
            _stream.ReadTimeout = Timeout.Infinite;
        }
    }

    public async Task SetupServerStream()
    {
        if (_sslConfig.SslEnabled)
        {
            _isSettingUpSsl = true;

            if (_sslConfig.Certificate == null)
                throw new InvalidOperationException("SSL is enabled but no certificate was provided for server authentication.");

            // Wrap the underlying stream in an SslStream
            var sslStream = new SslStream(_client.GetStream(), false);
            _stream = sslStream;
            // Synchronously perform the server handshake using the certbot certificate.
            await sslStream.AuthenticateAsServerAsync(
                _sslConfig.Certificate,
                clientCertificateRequired: false,
                enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: _sslConfig.RejectUnauthorized
            );

            _isSettingUpSsl = false;
        }
        else
        {
            _stream = _client.GetStream();
        }

        SetupTcpSocket();

        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Connected));
    }

    public async Task ConnectAsync(int maxRetries = 5, int delayMilliseconds = 1000, bool isReconnect = false)
    {
        if (isReconnect)
        {
            _client.Dispose();
            _client = new TcpClient();
            _stream = null!;
            SetupTcpSocket();
        }
        int attempt = 0;
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(isReconnect ? ConnectionStatus.Reconnecting : ConnectionStatus.Connecting));

        while (attempt < maxRetries)
        {
            try
            {
                await _client.ConnectAsync(_host, _port);
                var networkStream = _client.GetStream();

                if (_sslConfig.SslEnabled)
                {
                    _isSettingUpSsl = true;
                    // Create an SslStream with a certificate validation callback.
                    var sslStream = new SslStream(networkStream, false, (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        // If RejectUnauthorized is false, accept any certificate.
                        return !_sslConfig.RejectUnauthorized || sslPolicyErrors == SslPolicyErrors.None;
                    });
                    _stream = sslStream;
                    // Perform the client-side handshake.
                    await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = _host,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.Online,
                    });
                    _isSettingUpSsl = false;
                }
                else
                {
                    _stream = networkStream;
                }

                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(isReconnect ? ConnectionStatus.Reconnected : ConnectionStatus.Connected));
                return;
            }
            catch (SocketException ex)
            {
                attempt++;
                SbmLogger.Get()?.LogError(ex, "Attempt {attempt} failed", attempt);

                if (attempt >= maxRetries)
                {
                    ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Failed));
                    throw new InvalidOperationException($"Failed to connect to {_host}:{_port} after {maxRetries} attempts.", ex);
                }

                // Wait before retrying (exponential backoff)
                int waitTime = delayMilliseconds * (int)Math.Pow(2, attempt - 1);
                waitTime = Math.Clamp(waitTime, 0, MaxConnectWaitTime);
                await Task.Delay(waitTime);

                if (_cts.Token.IsCancellationRequested)
                {
                    ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Cancelled));
                    throw new TaskCanceledException("Connection attempt cancelled.");
                }
            }
            catch (Exception ex)
            {
                SbmLogger.Get()?.LogError(ex, "Unexpected exception");
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Failed));
                throw;
            }
        }
    }

    public void OnAuthenticated()
    {
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Authenticated));
    }

    public async Task SendAsync(Message message)
    {
        byte[] data = MessagePackSerializer.Serialize(message);
        var lengthPrefix = BitConverter.GetBytes(data.Length);

        await _streamWriteLock.WaitAsync();
        try
        {
            if (_stream is not null)
            {
                await _stream.WriteAsync(lengthPrefix);
                await _stream.WriteAsync(data);
            }
            else
            {
                throw new InvalidOperationException("Connection not open");
            }
        }
        finally
        {
            _streamWriteLock.Release();
        }
    }

    public async Task Reconnect()
    {
        await ConnectAsync(
            maxRetries: int.MaxValue,
            delayMilliseconds: 100,
            isReconnect: true
        );
    }

    public virtual async IAsyncEnumerable<Message> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!_cts.IsCancellationRequested)
        {
            var frame = await TryReadFrameAsync(cancellationToken);
            if (frame is null)
            {
                // Disconnect requested (clean EOF, truncated read, or fatal read error).
                yield break;
            }

            if (frame.Length == 0)
            {
                // Transient skip (SSL handshake in progress, recoverable error). Loop and try again.
                continue;
            }

            Message message;
            try
            {
                message = MessagePackSerializer.Deserialize<Message>(frame, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                SbmLogger.Get()?.LogError(ex, "Failed to deserialize incoming frame ({Length} bytes); closing connection.", frame.Length);
                yield break;
            }

            yield return message;
        }
    }

    /// <summary>
    /// Reads a single length-prefixed frame from the stream.
    /// Returns:
    ///   null              => terminal: EOF, truncated frame, or unrecoverable error (caller should yield-break).
    ///   Array.Empty<byte> => transient: nothing to read this turn (SSL setup, reconnect in progress).
    ///   non-empty byte[]  => a complete frame payload.
    /// </summary>
    private async Task<byte[]?> TryReadFrameAsync(CancellationToken cancellationToken)
    {
        // Snapshot the stream reference; it can be swapped out during reconnect.
        var stream = _stream;
        if (stream is null || _isSettingUpSsl)
        {
            try
            {
                await Task.Delay(10, cancellationToken);
            }
            catch (OperationCanceledException) { return null; }
            return Array.Empty<byte>();
        }

        byte[] lengthPrefix = new byte[4];
        int lengthRead;
        try
        {
            lengthRead = await ReadExactlyAsync(stream, lengthPrefix, 0, 4, cancellationToken);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            SbmLogger.Get()?.LogError(ex, "Failed to read frame length prefix.");
            return await HandleReadLossAsync();
        }

        if (lengthRead == 0)
        {
            // Clean EOF before any bytes — peer closed. Treat as a connection loss so
            // AlwaysReconnect clients try to reconnect rather than silently exiting.
            SbmLogger.Get()?.LogInformation("Peer closed connection.");
            return await HandleReadLossAsync();
        }

        if (lengthRead < 4)
        {
            SbmLogger.Get()?.LogWarning("Connection closed mid length-prefix ({Read}/4 bytes read).", lengthRead);
            return null;
        }

        int length = BitConverter.ToInt32(lengthPrefix, 0);

        if (length < 0 || length > MaxFrameSize)
        {
            SbmLogger.Get()?.LogError("Rejecting oversize/invalid frame length {Length} (MaxFrameSize={Max}).", length, MaxFrameSize);
            return null;
        }

        if (length == 0)
        {
            // Zero-length frame is malformed (Message always has fields); reject defensively.
            SbmLogger.Get()?.LogWarning("Received zero-length frame; closing connection.");
            return null;
        }

        byte[] data = new byte[length];
        int payloadRead;
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linkedCts = null;
        try
        {
            CancellationToken readToken = cancellationToken;
            if (ReadTimeout > TimeSpan.Zero)
            {
                timeoutCts = new CancellationTokenSource(ReadTimeout);
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                readToken = linkedCts.Token;
            }

            try
            {
                payloadRead = await ReadExactlyAsync(stream, data, 0, length, readToken);
            }
            catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                SbmLogger.Get()?.LogWarning("Frame payload read timed out after {Timeout} ({Length} bytes).", ReadTimeout, length);
                return null;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                SbmLogger.Get()?.LogError(ex, "Failed to read frame payload ({Length} bytes).", length);
                return null;
            }
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }

        if (payloadRead < length)
        {
            SbmLogger.Get()?.LogWarning("Connection closed mid-frame ({Read}/{Expected} bytes); discarding.", payloadRead, length);
            return null;
        }

        return data;
    }

    /// <summary>
    /// Common path when a read fails or observes EOF. Server-side connections and
    /// client connections without AlwaysReconnect terminate; client connections
    /// with AlwaysReconnect signal Disconnected (so SbmSocket can fail in-flight
    /// requests) and attempt to reconnect.
    /// </summary>
    private async Task<byte[]?> HandleReadLossAsync()
    {
        if (_isServer || !AlwaysReconnect)
            return null;

        // Surface the loss so SbmSocket fails pending requests. The follow-up Reconnect
        // call will subsequently fire Reconnecting → Reconnected via ConnectAsync.
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Disconnected));

        // Tear down the old client socket; ConnectAsync(isReconnect:true) will rebuild it.
        try { _stream?.Dispose(); } catch { }
        _stream = null!;

        try { await Reconnect(); }
        catch (Exception rex)
        {
            SbmLogger.Get()?.LogError(rex, "Reconnect failed.");
            return null;
        }
        return Array.Empty<byte>();
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), cancellationToken);
            if (n == 0) break; // EOF
            totalRead += n;
        }
        return totalRead;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        GC.SuppressFinalize(this);
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
    }

    public void Close()
    {
        Dispose();
    }
}
