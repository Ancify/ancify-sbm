using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared.Model.Networking;

using MessagePack;

namespace Ancify.SBM.Shared.Transport.TCP;

public class TcpTransport : ITransport, IDisposable
{
    private readonly TcpClient _client;
    private NetworkStream _stream;
    private readonly CancellationTokenSource _cts;
    private readonly string _host;
    private readonly ushort _port;

    public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;
    private readonly bool _isServer = false;

    public TcpTransport(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _cts = new CancellationTokenSource();
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Connected));
        _host = ((IPEndPoint)_client.Client.RemoteEndPoint!).Address.ToString();
        _port = (ushort)((IPEndPoint)_client.Client.RemoteEndPoint!).Port;
        _isServer = true;
    }

    public TcpTransport(string host, ushort port)
    {
        _host = host;
        _port = port;
        _client = new TcpClient();
        _cts = new CancellationTokenSource();
        _stream = null!;
        _isServer = false;
    }

    public async Task ConnectAsync()
    {
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Connecting));
        await _client.ConnectAsync(_host, _port);
        _stream = _client.GetStream();
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Connected));
    }

    public async Task SendAsync(Message message)
    {
        byte[] data = MessagePackSerializer.Serialize(message);
        var lengthPrefix = BitConverter.GetBytes(data.Length);

        await _stream.WriteAsync(lengthPrefix);
        await _stream.WriteAsync(data);
    }

    public async IAsyncEnumerable<Message> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!_cts.IsCancellationRequested)
        {
            byte[] lengthPrefix = new byte[4];

            try
            {
                if (_stream is not null)
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
                    continue;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read stream: {ex.Message}");
                if (_stream is not null && !_stream.Socket.Connected)
                {
                    Console.WriteLine($"Uh oh, stream was closed! Exiting now.");
                    // (@todo: maybe check if the deployer process is still running and decide based on that?)
                    Environment.Exit(0);
                    break;
                }
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
}