using System.Collections.Concurrent;
using System.Collections.Immutable;

using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared.Model.Networking;

using Microsoft.Extensions.Logging;

namespace Ancify.SBM.Shared;

public enum AuthStatus
{
    None,
    Anonymous,
    Authenticating,
    Authenticated,
    Failed
}

public enum EventType
{
    ConnectionStatusChanged,
    ClientIdReceived
}

public class Handler
{
    public required Func<Message, Task<Message?>> HandlerFunc { get; set; }
    public bool IsRespondingHandler { get; set; }
}

public class SbmSocketConfig
{
    public Func<Message, Exception, Message?>? ErrorHandler { get; set; }

    /// <summary>
    /// Maximum permitted size of a single inbound frame payload in bytes.
    /// Frames whose length prefix exceeds this value are rejected and the
    /// connection is closed without allocating the payload buffer. Defaults
    /// to 16 MiB.
    /// </summary>
    public int MaxFrameSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Per-frame inbound read timeout. If no byte of a length prefix or payload
    /// is observed within this window, the receive loop tears the connection
    /// down rather than waiting for OS TCP keepalive (which is minutes-to-hours
    /// on Windows by default). Defaults to 60s.
    /// </summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Thrown by SendRequestAsync when the underlying connection drops before a
/// reply has been received. Callers can distinguish this from a normal
/// TimeoutException to decide whether to retry immediately or after backoff.
/// </summary>
public class ConnectionLostException : Exception
{
    public ConnectionLostException() : base("Connection lost before reply was received.") { }
    public ConnectionLostException(string message) : base(message) { }
    public ConnectionLostException(string message, Exception inner) : base(message, inner) { }
}

public abstract class SbmSocket
{
    protected ITransport _transport;
    protected readonly ConcurrentDictionary<string, ImmutableList<Handler>> _handlers = new();
    protected readonly ConcurrentDictionary<EventType, ImmutableList<Func<object?, Task>>> _eventHandlers = new();
    protected readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Message>> _pendingRequests = new();

    public SbmSocketConfig Config { get; set; } = new();

    public SbmSocket(ITransport transport)
    {
        _transport = transport;
        _transport.ConnectionStatusChanged += (s, e) => OnConnectionStatusChanged(e);
    }

    public ITransport GetTransport()
    {
        return _transport;
    }
    public AuthStatus AuthStatus { get; protected set; }

    public Guid ClientId { get; init; }

    protected virtual void OnConnectionStatusChanged(ConnectionStatusEventArgs e)
    {
        if (e.Status == ConnectionStatus.Disconnected || e.Status == ConnectionStatus.Failed)
        {
            // Pending request awaiters would otherwise sit until their individual timeouts.
            // Failing them eagerly with a typed exception lets callers distinguish a
            // dropped connection from a slow peer.
            FailPendingRequests();
        }

        BroadcastEvent(EventType.ConnectionStatusChanged, e);
    }

    protected virtual void OnClientIdReceived(Guid clientId)
    {
        BroadcastEvent(EventType.ClientIdReceived, clientId);
    }

    public async void BroadcastEvent(EventType eventType, object? args = null)
    {
        if (_eventHandlers.TryGetValue(eventType, out var handlers))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var responseTask = handler?.Invoke(args);

                    if (responseTask is null)
                        continue;

                    await responseTask;
                }
                catch (Exception ex)
                {
                    SbmLogger.Get()?.LogError(ex, "Failed to broadcast event {EventType}.", eventType);
                }
            }
        }
    }

    internal void StartReceiving()
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport is not initialized.");

        Task.Run(async () =>
        {
            try
            {
                await foreach (var message in _transport.ReceiveAsync(_cts.Token))
                {
                    try
                    {
                        await HandleMessageAsync(message);
                    }
                    catch (Exception ex)
                    {
                        SbmLogger.Get()?.LogError(ex, "An exception occured while handling the message.");
                    }
                }
            }
            catch (Exception ex)
            {
                SbmLogger.Get()?.LogError(ex, "An exception occured while receiving data.");
            }

            OnConnectionStatusChanged(new ConnectionStatusEventArgs(ConnectionStatus.Disconnected));
        });
    }

    protected virtual Task<bool> IsMessageAllowedAsync(Message message)
    {
        return Task.FromResult(true);
    }

    protected virtual async Task HandleMessageAsync(Message message)
    {
        if (_handlers.TryGetValue(message.Channel, out var handlers))
        {
            if (!await IsMessageAllowedAsync(message))
            {
                SbmLogger.Get()?.LogInformation("Rejected message on channel {Channel} from client {SenderId}", message.Channel, message.SenderId);
                return;
            }

            foreach (var handler in handlers)
            {
                try
                {
                    var responseTask = handler.HandlerFunc?.Invoke(message);

                    if (responseTask is null)
                        continue;

                    var response = await responseTask;

                    if (response != null)
                    {
                        response.ReplyTo = message.MessageId;
                        response.TargetId = message.SenderId;
                        response.SenderId = ClientId;

                        if (_transport != null)
                            await _transport.SendAsync(response);
                    }
                }
                catch (Exception ex)
                {
                    SbmLogger.Get()?.LogError(ex, "Failed to handle message.");

                    if (handler.IsRespondingHandler && Config.ErrorHandler is not null)
                    {
                        var response = Config.ErrorHandler(message, ex);

                        if (response != null)
                        {
                            response.ReplyTo = message.MessageId;
                            response.TargetId = message.SenderId;
                            response.SenderId = ClientId;

                            if (_transport != null)
                                await _transport.SendAsync(response);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Registers an asynchronous handler that may return a response message.
    /// </summary>
    /// <param name="channel">The message channel to listen on.</param>
    /// <param name="handler">An asynchronous function that processes the message and optionally returns a response message.</param>
    /// <returns>An action that unregisters the handler when called.</returns>
    public Action On(string channel, Func<Message, Task<Message?>> handler, bool isResponseFunc = true)
    {
        var newHandler = new Handler() { HandlerFunc = handler, IsRespondingHandler = isResponseFunc };

        _handlers.AddOrUpdate(
            channel,
            _ => ImmutableList.Create(newHandler),
            (_, existing) => existing.Add(newHandler));

        return () =>
        {
            // Atomic remove-by-reference. If concurrent modifications race, retry until our handler
            // is gone (or never was present, e.g. when called twice).
            while (true)
            {
                if (!_handlers.TryGetValue(channel, out var current))
                    return;

                var updated = current.Remove(newHandler);
                if (ReferenceEquals(updated, current))
                    return; // already removed

                if (updated.IsEmpty)
                {
                    if (((ICollection<KeyValuePair<string, ImmutableList<Handler>>>)_handlers)
                        .Remove(new KeyValuePair<string, ImmutableList<Handler>>(channel, current)))
                        return;
                }
                else if (_handlers.TryUpdate(channel, updated, current))
                {
                    return;
                }
                // someone else mutated the list concurrently; retry
            }
        };
    }

    /// <summary>
    /// Registers an asynchronous handler that does not return a response message.
    /// </summary>
    /// <param name="channel">The message channel to listen on.</param>
    /// <param name="handler">An asynchronous action that processes the message.</param>
    /// <returns>An action that unregisters the handler when called.</returns>
    public Action On(string channel, Func<Message, Task> handler)
    {
        return On(channel, async (message) =>
        {
            await handler(message);
            return null;
        }, isResponseFunc: false);
    }

    /// <summary>
    /// Registers a synchronous handler that returns a response message.
    /// </summary>
    /// <param name="channel">The message channel to listen on.</param>
    /// <param name="handler">A function that processes the message and returns a response message.</param>
    /// <returns>An action that unregisters the handler when called.</returns>
    public Action On(string channel, Func<Message, Message> handler)
    {
        return On(channel, (message) =>
        {
            var response = handler(message);
            return Task.FromResult<Message?>(response);
        }, isResponseFunc: true);
    }

    /// <summary>
    /// Registers a synchronous handler that does not return a response message.
    /// </summary>
    /// <param name="channel">The message channel to listen on.</param>
    /// <param name="handler">An action that processes the message.</param>
    /// <returns>An action that unregisters the handler when called.</returns>
    public Action On(string channel, Action<Message> handler)
    {
        return On(channel, (message) =>
        {
            handler(message);
            return Task.FromResult<Message?>(null);
        }, isResponseFunc: false);
    }

    public Action On(EventType eventType, Func<object?, Task> handler)
    {
        _eventHandlers.AddOrUpdate(
            eventType,
            _ => ImmutableList.Create(handler),
            (_, existing) => existing.Add(handler));

        return () =>
        {
            while (true)
            {
                if (!_eventHandlers.TryGetValue(eventType, out var current))
                    return;

                var updated = current.Remove(handler);
                if (ReferenceEquals(updated, current))
                    return;

                if (updated.IsEmpty)
                {
                    if (((ICollection<KeyValuePair<EventType, ImmutableList<Func<object?, Task>>>>)_eventHandlers)
                        .Remove(new KeyValuePair<EventType, ImmutableList<Func<object?, Task>>>(eventType, current)))
                        return;
                }
                else if (_eventHandlers.TryUpdate(eventType, updated, current))
                {
                    return;
                }
            }
        };
    }

    public Action On<EventArgsType>(EventType eventType, Func<EventArgsType, Task> handler)
    {
        Task WrappedHandler(object? arg) => handler((EventArgsType)arg!);
        return On(eventType, WrappedHandler);
    }

    /// <summary>
    /// Registers a synchronous handler.
    /// </summary>
    /// <param name="eventType">The event type to listen on.</param>
    /// <param name="handler">A function that processes the event.</param>
    /// <returns>An action that unregisters the handler when called.</returns>
    public Action On(EventType eventType, Action<object?> handler)
    {
        Task WrappedHandler(object? arg)
        {
            handler(arg);
            return Task.CompletedTask;
        }
        return On(eventType, WrappedHandler);
    }

    /// <summary>
    /// Registers a synchronous handler with specific event args type.
    /// </summary>
    /// <param name="eventType">The event type to listen on.</param>
    /// <param name="handler">A function that processes the event.</param>
    /// <returns>An action that unregisters the handler when called.</returns>
    public Action On<EventArgsType>(EventType eventType, Action<EventArgsType> handler)
    {
        void WrappedHandler(object? arg) => handler((EventArgsType)arg!);
        return On(eventType, WrappedHandler);
    }


    public virtual Task SendAsync(Message message)
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport is not initialized.");

        message.SenderId = ClientId;
        return _transport.SendAsync(message);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="request"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="TimeoutException"></exception>
    public virtual async Task<Message> SendRequestAsync(Message request, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(15);

        if (_transport == null)
            throw new InvalidOperationException("Transport is not initialized.");

        var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyId = request.MessageId;

        Action unregister = null!;
        int unregistered = 0;
        void SafeUnregister()
        {
            if (Interlocked.Exchange(ref unregistered, 1) == 0)
            {
                _pendingRequests.TryRemove(replyId, out _);
                unregister?.Invoke();
            }
        }

        unregister = On($"{request.Channel}_reply_{replyId}", message =>
        {
            if (message.ReplyTo == replyId)
            {
                tcs.TrySetResult(message);
                SafeUnregister();
            }
        });

        _pendingRequests[replyId] = tcs;

        try
        {
            await SendAsync(request);
        }
        catch
        {
            SafeUnregister();
            throw;
        }

        if (await Task.WhenAny(tcs.Task, Task.Delay(timeout.Value)) == tcs.Task)
        {
            try
            {
                return await tcs.Task;
            }
            finally
            {
                SafeUnregister();
            }
        }
        else
        {
            SafeUnregister();
            throw new TimeoutException("Request timed out.");
        }
    }

    /// <summary>
    /// Fails every in-flight SendRequestAsync with a ConnectionLostException.
    /// Called automatically when the transport reports Disconnected.
    /// </summary>
    protected void FailPendingRequests()
    {
        if (_pendingRequests.IsEmpty)
            return;

        // Drain the map first so concurrent SafeUnregister calls don't race against us.
        var snapshot = _pendingRequests.ToArray();
        _pendingRequests.Clear();

        foreach (var kvp in snapshot)
        {
            kvp.Value.TrySetException(new ConnectionLostException());
        }
    }

    public bool IsAuthenticated()
    {
        return AuthStatus == AuthStatus.Authenticated;
    }

    public void AuthenticationGuard()
    {
        if (!IsAuthenticated())
            throw new UnauthorizedAccessException();
    }

    public virtual void Dispose()
    {
        _cts.Cancel();

        if (_transport is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}


public class ClientConnectedEventArgs(SbmSocket clientSocket) : EventArgs
{
    public SbmSocket ClientSocket { get; } = clientSocket;
}

public class ClientDisconnectedEventArgs(SbmSocket clientSocket) : EventArgs
{
    public SbmSocket ClientSocket { get; } = clientSocket;
}