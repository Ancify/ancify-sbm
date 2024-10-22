using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Client;

public class ClientSocket : SbmSocket
{
    public ClientSocket(ITransport transport)
    {
        _transport = transport;
        _transport.ConnectionStatusChanged += (s, e) => OnConnectionStatusChanged(e);
        StartReceiving();
    }

    public async Task ConnectAsync()
    {
        OnConnectionStatusChanged(new ConnectionStatusEventArgs(ConnectionStatus.Connecting));
        await _transport!.ConnectAsync();
        OnConnectionStatusChanged(new ConnectionStatusEventArgs(ConnectionStatus.Connected));
    }

    public override Task SendAsync(Message message)
    {
        message.SenderId = ClientId;
        return base.SendAsync(message);
    }

    public override Task<Message> SendRequestAsync(Message request, TimeSpan? timeout = null)
    {
        request.SenderId = ClientId;
        return base.SendRequestAsync(request, timeout);
    }
}
