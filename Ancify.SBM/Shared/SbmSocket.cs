using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Shared;

public abstract class SbmSocket
{
    protected ITransport? _transport;
    protected readonly Dictionary<string, List<Func<Message, Task<Message?>>>> _handlers = [];
    protected readonly CancellationTokenSource _cts = new();

    public Guid ClientId { get; init; }

    public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;
    public event EventHandler<Guid>? ClientIdReceived;

    protected virtual void OnConnectionStatusChanged(ConnectionStatusEventArgs e)
    {
        ConnectionStatusChanged?.Invoke(this, e);
    }

    protected virtual void OnClientIdReceived(Guid clientId)
    {
        ClientIdReceived?.Invoke(this, clientId);
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
                        Console.WriteLine($"An exception occured while handling the message: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An exception occured: {ex.Message}");
            }

            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Disconnected));
        });
    }

    protected virtual async Task HandleMessageAsync(Message message)
    {
        if (_handlers.TryGetValue(message.Channel, out var handlers))
        {
            // Create a copy of the handlers list to safely iterate over it
            var handlersCopy = handlers.ToList();

            foreach (var handler in handlersCopy)
            {
                var responseTask = handler?.Invoke(message);

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
        }
    }

    /// <summary>
    /// Registers an asynchronous handler that may return a response message.
    /// </summary>
    /// <param name="channel">The message channel to listen on.</param>
    /// <param name="handler">An asynchronous function that processes the message and optionally returns a response message.</param>
    /// <returns>An action that unregisters the handler when called.</returns>
    public Action On(string channel, Func<Message, Task<Message?>> handler)
    {
        if (!_handlers.TryGetValue(channel, out var handlers))
        {
            _handlers[channel] = [];
            handlers = _handlers[channel];
        }
        handlers.Add(handler);

        return () =>
        {
            handlers.Remove(handler);
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
        });
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
        });
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
        });
    }

    public virtual Task SendAsync(Message message)
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport is not initialized.");

        message.SenderId = ClientId;
        return _transport.SendAsync(message);
    }

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