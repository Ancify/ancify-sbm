using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Client;

public class ClientSocket : SbmSocket
{
    public ClientSocket(ITransport transport) : base(transport)
    {
        StartReceiving();
    }


    public async Task ConnectAsync()
    {
        await _transport!.ConnectAsync();

    }
    public async Task<bool> AuthenticateAsync(string id, string key)
    {
        var message = new Message("_auth_", new { Id = id, Key = key });
        var response = await SendRequestAsync(message);

        var data = response.AsTypeless();

        var success = (bool)data["Success"];

        if (success)
        {
            _transport?.OnAuthenticated();
        }

        return success;
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
