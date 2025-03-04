using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Model.Networking;
using Ancify.SBM.Shared.Transport.TCP;
using Ancify.SBM.Shared.Transport.WS;

using Microsoft.Extensions.Logging;

namespace Ancify.SBM.Server
{
    using AuthHandlerType = Func<string /* Id */, string /* Key */, string /* Scope */, Task<AuthContext>>;

    public class ServerSocket
    {
        private readonly bool _useWebSocket;
        private readonly TcpListener? _tcpListener;
        private readonly HttpListener? _httpListener;
        private readonly SslConfig _sslConfig;
        private readonly AuthHandlerType? _authHandler;
        private readonly ConcurrentDictionary<Guid, ConnectedClientSocket> _clients = new();

        public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
        public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;

        public AuthHandlerType? AuthHandler => _authHandler;

        public bool AnonymousDisallowed { get; protected set; }

        /// <summary>
        /// Creates a server that supports either TCP or WebSocket transports.
        /// For WebSocket support, set useWebSocket to true.
        /// </summary>
        /// <param name="host">The IP address to listen on.</param>
        /// <param name="port">The port to listen on.</param>
        /// <param name="sslConfig">The SSL configuration (used for TCP connections).</param>
        /// <param name="useWebSocket">If true, the server listens for WebSocket (HTTP upgrade) requests.</param>
        /// <param name="authHandler">Optional authentication handler.</param>
        public ServerSocket(IPAddress host, int port, SslConfig sslConfig, bool useWebSocket = false, AuthHandlerType? authHandler = null)
        {
            _sslConfig = sslConfig;
            _authHandler = authHandler;
            _useWebSocket = useWebSocket;

            if (_useWebSocket)
            {
                // Set up an HttpListener to accept WebSocket upgrade requests.
                _httpListener = new HttpListener();
                // For secure WebSocket connections (wss), you would need to use "https://"
                _httpListener.Prefixes.Add($"http://{host}:{port}/");
            }
            else
            {
                _tcpListener = new TcpListener(host, port);
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_useWebSocket)
            {
                _httpListener!.Start();
                SbmLogger.Get()?.LogInformation("HTTP Listener started for WebSocket connections.");

                while (!cancellationToken.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _httpListener.GetContextAsync();
                    }
                    catch (Exception ex)
                    {
                        SbmLogger.Get()?.LogError(ex, "Error accepting HTTP context for WebSocket connection.");
                        continue;
                    }

                    if (context.Request.IsWebSocketRequest)
                    {
                        try
                        {
                            // Accept the WebSocket upgrade request.
                            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                            var transport = new WebsocketTransport(wsContext.WebSocket);
                            var clientSocket = new ConnectedClientSocket(transport, this)
                            {
                                ClientId = Guid.NewGuid(),
                                DisallowAnonymous = AnonymousDisallowed
                            };

                            _clients[clientSocket.ClientId] = clientSocket;
                            ClientConnected?.Invoke(this, new ClientConnectedEventArgs(clientSocket));
                        }
                        catch (Exception ex)
                        {
                            SbmLogger.Get()?.LogError(ex, "Failed to accept WebSocket connection.");
                            context.Response.StatusCode = 500;
                            context.Response.Close();
                        }
                    }
                    else
                    {
                        // Reject non-WebSocket requests.
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
            }
            else
            {
                _tcpListener!.Start();
                SbmLogger.Get()?.LogInformation("TCP Listener started.");

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var tcpClient = await _tcpListener.AcceptTcpClientAsync(cancellationToken);
                        var transport = new TcpTransport(tcpClient, _sslConfig);
                        await transport.SetupServerStream();

                        var clientSocket = new ConnectedClientSocket(transport, this)
                        {
                            ClientId = Guid.NewGuid(),
                            DisallowAnonymous = AnonymousDisallowed
                        };

                        _clients[clientSocket.ClientId] = clientSocket;
                        ClientConnected?.Invoke(this, new ClientConnectedEventArgs(clientSocket));
                    }
                    catch (Exception ex)
                    {
                        SbmLogger.Get()?.LogError(ex, "Unexpected exception on client connection.");
                    }
                }
            }
        }

        public Task BroadcastAsync(Message message)
        {
            var tasks = _clients.Values.Select(client => client.SendAsync(message));
            return Task.WhenAll(tasks);
        }

        public Task SendToClientAsync(Guid clientId, Message message)
        {
            if (_clients.TryGetValue(clientId, out var clientSocket))
            {
                return clientSocket.SendAsync(message);
            }
            else
            {
                throw new Exception("Client not connected");
            }
        }

        public void RemoveClient(Guid clientId)
        {
            _clients.TryRemove(clientId, out _);
        }

        public void OnClientDisconnected(ClientDisconnectedEventArgs e)
        {
            ClientDisconnected?.Invoke(this, e);
        }

        public void DisallowAnonymous()
        {
            AnonymousDisallowed = true;
        }
    }
}
