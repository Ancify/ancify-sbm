using System.Net.WebSockets;
using System.Runtime.CompilerServices;

using Ancify.SBM.Interfaces;
using Ancify.SBM.Shared.Model.Networking;

using MessagePack;

using Microsoft.Extensions.Logging;

namespace Ancify.SBM.Shared.Transport.WS;

public class WebsocketTransport : ITransport, IDisposable
{
    private readonly ClientWebSocket _clientWebSocket;
    private readonly WebSocket? _serverWebSocket;
    private readonly CancellationTokenSource _cts;
    private readonly Uri? _uri;
    private readonly bool _isServer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool AlwaysReconnect { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int MaxConnectWaitTime { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;

    // Client constructor – connection will be initiated later via ConnectAsync.
    public WebsocketTransport(string uri)
    {
        _uri = new Uri(uri);
        _clientWebSocket = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        _isServer = false;
    }

    // Server constructor – accepts an already connected WebSocket.
    public WebsocketTransport(WebSocket webSocket)
    {
        _serverWebSocket = webSocket;
        _cts = new CancellationTokenSource();
        _isServer = true;
        _clientWebSocket = null!;
    }

    public async Task ConnectAsync(int maxRetries = 5, int delayMilliseconds = 1000)
    {
        if (_isServer)
        {
            // Already connected.
            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Connected));
            return;
        }

        int attempt = 0;
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Connecting));

        while (attempt < maxRetries)
        {
            try
            {
                await _clientWebSocket.ConnectAsync(_uri!, _cts.Token);
                if (_clientWebSocket.State == WebSocketState.Open)
                {
                    ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Connected));
                    return;
                }
            }
            catch (Exception ex)
            {
                attempt++;
                SbmLogger.Get()?.LogError(ex, "Attempt {attempt} failed", attempt);

                if (attempt >= maxRetries)
                {
                    ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Failed));
                    throw new InvalidOperationException($"Failed to connect to {_uri} after {maxRetries} attempts.", ex);
                }

                int waitTime = delayMilliseconds * (int)Math.Pow(2, attempt - 1);
                await Task.Delay(waitTime, _cts.Token);
            }
        }
    }

    public async Task SendAsync(Message message)
    {
        byte[] data = MessagePackSerializer.Serialize(message);
        await _sendLock.WaitAsync();
        try
        {
            WebSocket socket = _isServer ? _serverWebSocket! : _clientWebSocket;
            await socket.SendAsync(new ArraySegment<byte>(data),
                WebSocketMessageType.Binary, endOfMessage: true, _cts.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<Message> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        WebSocket socket = _isServer ? _serverWebSocket! : _clientWebSocket;
        var buffer = new byte[8192];

        while (!_cts.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            bool isEndOfMessage = false;

            do
            {
                if (socket.State is WebSocketState.None or WebSocketState.Connecting)
                {
                    await Task.Delay(10, cancellationToken);
                    continue;
                }
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                isEndOfMessage = result.EndOfMessage;

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by peer", cancellationToken);
                    yield break;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!isEndOfMessage);

            byte[] messageBytes = ms.ToArray();
            var message = MessagePackSerializer.Deserialize<Message>(messageBytes, cancellationToken: cancellationToken);
            yield return message;
        }
    }

    public void OnAuthenticated()
    {
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Authenticated));
    }

    public void Close()
    {
        Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (!_isServer)
        {
            _clientWebSocket.Dispose();
        }
        else
        {
            _serverWebSocket?.Dispose();
        }
        //ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(ConnectionStatus.Disconnected));
    }

    public Task ConnectAsync(int maxRetries = 5, int delayMilliseconds = 1000, bool isReconnect = false)
    {
        throw new NotImplementedException();
    }

    public Task Reconnect()
    {
        throw new NotImplementedException();
    }
}
