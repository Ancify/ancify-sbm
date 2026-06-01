using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Client;

public class ClientSocket : SbmSocket
{
    public ClientSocket(ITransport transport) : base(transport)
    {
        StartReceiving();
        SetupHandlers();
    }

    private void SetupHandlers()
    {
        On("__$status", message => Message.FromReply(message, new { Success = true }));
    }


    public async Task ConnectAsync()
    {
        await _transport!.ConnectAsync();

    }

    /// <summary>
    /// Sends an _auth_ frame and updates AuthStatus based on the server's reply.
    /// </summary>
    /// <remarks>
    /// Reconnect at the transport level re-establishes the socket only. SBM does not
    /// retain credentials. Applications using AlwaysReconnect=true must subscribe to
    /// ConnectionStatusChanged → Reconnected and call AuthenticateAsync again.
    /// </remarks>
    public async Task<bool> AuthenticateAsync(string id, string key, string? scope = null)
    {
        AuthStatus = AuthStatus.Authenticating;

        var message = new Message("_auth_", new { Id = id, Key = key, Scope = scope });
        Message response;
        try
        {
            response = await SendRequestAsync(message);
        }
        catch
        {
            AuthStatus = AuthStatus.Failed;
            throw;
        }

        bool success = false;
        try
        {
            var data = response.AsTypeless();
            if (data.TryGetValue("Success", out var raw) && raw is bool b)
                success = b;
        }
        catch
        {
            // Malformed auth reply: treat as failure rather than throwing
            // InvalidCastException out of a method whose contract is bool.
            success = false;
        }

        AuthStatus = success ? AuthStatus.Authenticated : AuthStatus.Failed;

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
