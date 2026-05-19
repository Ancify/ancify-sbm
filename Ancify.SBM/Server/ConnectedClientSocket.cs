using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Model.Networking;

using Microsoft.Extensions.Logging;

namespace Ancify.SBM.Server;

public class ConnectedClientSocket : SbmSocket
{
    private readonly ServerSocket _server;

    public AuthContext Context { get; protected set; }

    public bool DisallowAnonymous { get; set; }

    private int _faults = 0;
    private readonly int _maxFaults = 3;
    private int _disposed = 0;

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

            string? id = null, key = null, scope = null;
            try
            {
                var data = message.AsTypeless();
                id = TryGetString(data, "Id");
                key = TryGetString(data, "Key");
                scope = TryGetString(data, "Scope");
            }
            catch (Exception ex)
            {
                SbmLogger.Get()?.LogWarning(ex, "Malformed auth payload from client {ClientId}.", ClientId);
                AuthStatus = AuthStatus.Failed;
                return Message.FromReply(message, new { Success = false });
            }

            var handlerTask = _server.AuthHandler?.Invoke(id ?? string.Empty, key ?? string.Empty, scope ?? string.Empty);

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

    private static string? TryGetString(IReadOnlyDictionary<object, object> data, string key)
    {
        if (!data.TryGetValue(key, out var raw) || raw is null)
            return null;
        return raw as string ?? raw.ToString();
    }

    protected override Task<bool> IsMessageAllowedAsync(Message message)
    {
        return DisallowAnonymous && !IsAuthenticated() && message.Channel != "_auth_"
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
        // Multiple disposal paths converge here (receive-loop exit, heartbeat fault threshold,
        // explicit server-side disconnect). Without this guard ClientDisconnected fires twice
        // and any listener tracking client counts ends up double-decrementing.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

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

    internal async Task CheckConnectionStatus()
    {
        try
        {
            // Short timeout: heartbeats should respond promptly. The previous 15s default
            // meant three consecutive missed heartbeats could take 45+ seconds to detect.
            await SendRequestAsync(new Message("__$status"), TimeSpan.FromSeconds(2));
            Interlocked.Exchange(ref _faults, 0);
        }
        catch
        {
            // This will close the connection, no need to retry from the server
            int current = Interlocked.Increment(ref _faults);

            if (current >= _maxFaults)
            {
                OnConnectionStatusChanged(new ConnectionStatusEventArgs(ConnectionStatus.Disconnected));
            }
        }
    }
}

