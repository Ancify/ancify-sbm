using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Server;

public class ConnectedClientSocket : SbmSocket
{
    private readonly ServerSocket _server;

    public AuthContext Context { get; protected set; }

    public bool DisallowAnonymous { get; set; }

    public ConnectedClientSocket(ITransport transport, ServerSocket server) : base(transport)
    {
        Context = new();
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
            var scope = (string)data["Scope"];

            var handlerTask = _server.AuthHandler?.Invoke(id, key, scope);

            if (handlerTask is not null)
            {
                var result = await handlerTask;
                Context = result;

                if (!result.Success)
                {
                    AuthStatus = AuthStatus.Failed;

                    if (!result.IsConnectionAllowed)
                    {
                        _transport?.Close();
                    }

                    return Message.FromReply(message, new { Success = false });
                }
            }

            AuthStatus = AuthStatus.Authenticated;

            _transport?.OnAuthenticated();

            return Message.FromReply(message, new { Success = true });
        });
    }

    protected override Task<bool> IsMessageAllowedAsync(Message message)
    {
        return DisallowAnonymous && !IsAuthenticated()
            ? Task.FromResult(false)
            : base.IsMessageAllowedAsync(message);
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

    public void AuthenticationGuard(string? role = null, string? scope = null)
    {
        if (!IsAuthenticated() || !Context.Success)
        {
            throw new UnauthorizedAccessException("Not authenticated.");
        }

        if (role is not null && !Context.Roles.Contains(role))
        {
            throw new UnauthorizedAccessException("Client does not have required role.");
        }

        if (scope is not null && scope != Context.Scope)
        {
            throw new UnauthorizedAccessException("Client does not have required scope.");
        }
    }

    public void AuthenticationGuardAny(string[]? roles = null, string[]? scopes = null)
    {
        if (!IsAuthenticated() || !Context.Success)
        {
            throw new UnauthorizedAccessException("Not authenticated.");
        }

        bool hasValidRole = roles is null || roles.Any(Context.Roles.Contains);
        bool hasValidScope = scopes is null || scopes.Any(scope => scope == Context.Scope);

        if (!hasValidRole && !hasValidScope)
        {
            throw new UnauthorizedAccessException("Client does not have any of the required roles or scopes.");
        }
    }

    public void AuthenticationGuardAll(string[]? roles = null, string[]? scopes = null)
    {
        if (!IsAuthenticated() || !Context.Success)
        {
            throw new UnauthorizedAccessException("Not authenticated.");
        }

        bool hasAllRoles = roles is null || roles.All(Context.Roles.Contains);
        bool hasAllScopes = scopes is null || scopes.All(scope => scope == Context.Scope);

        if (!hasAllRoles || !hasAllScopes)
        {
            throw new UnauthorizedAccessException("Client does not have all the required roles and scopes.");
        }
    }

}

