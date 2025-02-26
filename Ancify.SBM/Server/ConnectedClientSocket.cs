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
        SetupAuthHandlers();
    }

    private void SetupAuthHandlers()
    {
        On("_auth_", async message =>
        {
            AuthStatus = AuthStatus.Authenticating;

            var data = message.AsTypeless();

            var id = (string)data["Id"];
            var key = (string)data["Key"];

            var handlerTask = _server.AuthHandler?.Invoke(id, key);

            if (handlerTask is not null)
            {
                if (!await handlerTask)
                {
                    AuthStatus = AuthStatus.Failed;
                    return Message.FromReply(message, new { Success = false });
                }
            }

            AuthStatus = AuthStatus.Authenticated;

            _transport?.OnAuthenticated();

            return Message.FromReply(message, new { Success = true });
        });
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

    protected override void OnConnectionStatusChanged(ConnectionStatusEventArgs e)
    {
        base.OnConnectionStatusChanged(e);

        if (e.Status == ConnectionStatus.Disconnected)
        {
            Dispose();
        }
    }
}

