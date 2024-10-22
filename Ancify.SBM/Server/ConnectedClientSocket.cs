using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Server;

public class ConnectedClientSocket : SbmSocket
{
    private readonly ServerSocket _server;

    public ConnectedClientSocket(ITransport transport, ServerSocket server)
    {
        _transport = transport;
        _transport.ConnectionStatusChanged += (s, e) => OnConnectionStatusChanged(e);
        _server = server;
        StartReceiving();
    }

    protected override async Task HandleMessageAsync(Message message)
    {
        message.SenderId = ClientId;
        await base.HandleMessageAsync(message);
    }

    public override void Dispose()
    {
        base.Dispose();
        _server.RemoveClient(ClientId);
        _server.OnClientDisconnected(new ClientDisconnectedEventArgs(this));
    }
}

