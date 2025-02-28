using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Model.Networking;
using Ancify.SBM.Shared.Transport.TCP;

using Microsoft.Extensions.Logging;

namespace Ancify.SBM.Server;

using AuthHandlerType = Func<string /* Id */, string /* Key */, string /* Scope */, Task<AuthContext>>;

public class ServerSocket(IPAddress host, int port, SslConfig sslConfig, AuthHandlerType? authHandler = null)
{
    private readonly TcpListener _listener = new(host, port);
    private readonly ConcurrentDictionary<Guid, ConnectedClientSocket> _clients = new();

    public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
    public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;

    public AuthHandlerType? AuthHandler { get => authHandler; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _listener.Start();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(cancellationToken);
                var transport = new TcpTransport(tcpClient, sslConfig);

                await transport.SetupServerStream();

                var clientSocket = new ConnectedClientSocket(transport, this)
                {
                    ClientId = Guid.NewGuid()
                };

                _clients[clientSocket.ClientId] = clientSocket;

                ClientConnected?.Invoke(this, new ClientConnectedEventArgs(clientSocket));
            }
            catch (Exception ex)
            {
                SbmLogger.Get()?.LogError(ex, "Unexpected exception on client connection");
            }
        }
    }

    public Task BroadcastAsync(Message message)
    {
        var tasks = _clients.Values.Select(client => client.SendAsync(message));
        return Task.WhenAll(tasks);
    }

    public Task SendToClientAsync(Guid clientId, Message message)
    {
        if (_clients.TryGetValue(clientId, out var clientSocket))
        {
            return clientSocket.SendAsync(message);
        }
        else
        {
            throw new Exception("Client not connected");
        }
    }

    public void RemoveClient(Guid clientId)
    {
        _clients.TryRemove(clientId, out _);
    }

    public void OnClientDisconnected(ClientDisconnectedEventArgs e)
    {
        ClientDisconnected?.Invoke(this, e);
    }
}

