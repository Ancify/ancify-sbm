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
}

public abstract class SbmSocket
{
    protected ITransport _transport;
    protected readonly Dictionary<string, List<Handler>> _handlers = [];
    protected readonly Dictionary<EventType, List<Func<object?, Task>>> _eventHandlers = [];
    protected readonly CancellationTokenSource _cts = new();

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
            var handlersCopy = handlers.ToList();

            foreach (var handler in handlersCopy)
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
                        //Console.WriteLine($"An exception occured while handling the message: {ex.Message}");
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
            }

            // Create a copy of the handlers list to safely iterate over it
            var handlersCopy = handlers.ToList();

            foreach (var handler in handlersCopy)
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
        if (!_handlers.TryGetValue(channel, out var handlers))
        {
            _handlers[channel] = [];
            handlers = _handlers[channel];
        }
        var newHandler = new Handler() { HandlerFunc = handler, IsRespondingHandler = isResponseFunc };

        handlers.Add(newHandler);

        return () =>
        {
            handlers.Remove(newHandler);
            if (handlers.Count == 0)
            {
                _handlers.Remove(channel);
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
        if (!_eventHandlers.TryGetValue(eventType, out var handlers))
        {
            _eventHandlers[eventType] = [];
            handlers = _eventHandlers[eventType];
        }
        handlers.Add(handler);

        return () =>
        {
            handlers.Remove(handler);
            if (handlers.Count == 0)
            {
                _eventHandlers.Remove(eventType);
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

        var tcs = new TaskCompletionSource<Message>();
        var replyId = request.MessageId;

        Action unregister = null!;

        unregister = On($"{request.Channel}_reply_{replyId}", message =>
        {
            if (message.ReplyTo == replyId)
            {
                tcs.SetResult(message);
                unregister();
            }
        });

        await SendAsync(request);

        if (await Task.WhenAny(tcs.Task, Task.Delay(timeout.Value)) == tcs.Task)
        {
            return tcs.Task.Result;
        }
        else
        {
            unregister();
            throw new TimeoutException("Request timed out.");
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