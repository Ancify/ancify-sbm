using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model.Networking;
using Ancify.SBM.Shared.Transport.TCP;

namespace Ancify.SBM.Server;

public class ServerSocket(IPAddress host, int port)
{
    private readonly TcpListener _listener = new(host, port);
    private readonly ConcurrentDictionary<Guid, ConnectedClientSocket> _clients = new();

    public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
    public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _listener.Start();

        while (!cancellationToken.IsCancellationRequested)
        {
            var tcpClient = await _listener.AcceptTcpClientAsync(cancellationToken);
            var transport = new TcpTransport(tcpClient);

            var clientSocket = new ConnectedClientSocket(transport, this)
            {
                ClientId = Guid.NewGuid()
            };

            _clients[clientSocket.ClientId] = clientSocket;

            ClientConnected?.Invoke(this, new ClientConnectedEventArgs(clientSocket));
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

