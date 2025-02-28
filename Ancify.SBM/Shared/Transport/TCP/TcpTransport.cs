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
    private readonly TcpClient _client;
    private Stream _stream;
    private readonly CancellationTokenSource _cts;
    private readonly string _host;
    private readonly ushort _port;
    public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;
    private readonly bool _isServer = false;
    private bool _isSettingUpSsl = false;
    private readonly SslConfig _sslConfig;
    private readonly SemaphoreSlim _streamWriteLock = new(1, 1);

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

        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Connected));
    }

    public async Task ConnectAsync(int maxRetries = 5, int delayMilliseconds = 1000)
    {
        int attempt = 0;
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Connecting));

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
                        return !_sslConfig.RejectUnauthorized ? true : sslPolicyErrors == SslPolicyErrors.None;
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

                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Connected));
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
            await _stream.WriteAsync(lengthPrefix);
            await _stream.WriteAsync(data);
        }
        finally
        {
            _streamWriteLock.Release();
        }
    }

    private static bool AreMessagesEqual(Message original, Message deserialized)
    {
        return original.Channel == deserialized.Channel &&
               Equals(original.Data, deserialized.Data) &&
               original.ReplyTo == deserialized.ReplyTo &&
               original.MessageId == deserialized.MessageId &&
               original.SenderId == deserialized.SenderId &&
               original.TargetId == deserialized.TargetId;
    }

    public virtual async IAsyncEnumerable<Message> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!_cts.IsCancellationRequested)
        {
            byte[] lengthPrefix = new byte[4];

            try
            {
                if (_stream is not null && !_isSettingUpSsl)
                {
                    int read = await _stream.ReadAsync(lengthPrefix.AsMemory(0, 4), cancellationToken);

                    if (read == 0)
                    {
                        // Connection closed
                        break;
                    }
                }
                else
                {
                    await Task.Delay(10, cancellationToken);
                    continue;
                }
            }
            catch (Exception ex)
            {
                SbmLogger.Get()?.LogError(ex, "Failed to read stream.");

                if (_stream is not null && !_client.Client.Connected)
                    break;

                continue;
            }

            int length = BitConverter.ToInt32(lengthPrefix, 0);
            byte[] data = new byte[length];
            int totalRead = 0;

            while (totalRead < length)
            {
                int bytesRead = await _stream.ReadAsync(data.AsMemory(totalRead, length - totalRead), cancellationToken);
                if (bytesRead == 0)
                {
                    // Connection closed
                    break;
                }
                totalRead += bytesRead;
            }

            var message = MessagePackSerializer.Deserialize<Message>(data, cancellationToken: cancellationToken);
            yield return message;
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _cts?.Cancel();
        _stream?.Dispose();
        _client?.Close();
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Disconnected));
    }

    public void Close()
    {
        Dispose();
    }
}
