// See https://aka.ms/new-console-template for more information
using Ancify.SBM.Client;
using Ancify.SBM.Example;
using Ancify.SBM.Server;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Model.Networking;
using Ancify.SBM.Shared.Transport.WS;

using Microsoft.Extensions.Logging;

Console.WriteLine("Hello, World!");

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddConsole();
});

SbmLogger.SetLoggerFromFactory(loggerFactory);

var sslConfig = CertificateHelper.CreateDevSslConfig("./dev.pfx", password: "abcd");
sslConfig.RejectUnauthorized = false;
sslConfig.SslEnabled = false;


// @todo: dissallow by default & handler exceptions (anonymous handlers)
var serverSocket = new ServerSocket(System.Net.IPAddress.Loopback, 12345, sslConfig, useWebSocket: true, (id, key, scope) => Task.FromResult(new AuthContext("1234", [])));

serverSocket.ClientConnected += (s, e) =>
{
    Console.WriteLine($"Client connected: {e.ClientSocket.ClientId}");

    e.ClientSocket.On("message", async message =>
    {
        e.ClientSocket.AuthenticationGuard();

        Console.WriteLine($"Received message from {message.SenderId}: {message.Data}");

        // Send acknowledgment back to sender
        var response = new Message
        {
            Channel = "message_received",
            Data = $"Acknowledged: {message.Data}",
            MessageId = Guid.NewGuid(),
            SenderId = Guid.Empty, // Server ID
        };

        await e.ClientSocket.SendAsync(response);

        // Broadcast to other clients
        /*
        await serverSocket.SendAsync(new Message
        {
            Type = "broadcast",
            Data = $"Client {message.SenderId} says: {message.Data}",
            MessageId = Guid.NewGuid(),
        });
        */

        return null;
    });

    e.ClientSocket.On("test", message => Message.FromReply(message, "reply!"));

    e.ClientSocket.On("heartbeat", message =>
    {
        Console.WriteLine("server heatbeat");
        return Message.FromReply(message, "heartbeat");
    });
};

_ = serverSocket.StartAsync();

//var transport = new TcpTransport("127.0.0.1", 12345, sslConfig);
var transport = new WebsocketTransport("ws://127.0.0.1:12345");
var clientSocket = new ClientSocket(transport);

clientSocket.On<ConnectionStatusEventArgs>(EventType.ConnectionStatusChanged, args =>
{
    Console.WriteLine($"Status changed to {args.Status}");
});

await clientSocket.ConnectAsync();
await clientSocket.AuthenticateAsync("testid", "key");

clientSocket.On("message_received", message =>
{
    Console.WriteLine($"Server acknowledged: {message.Data}");
});

clientSocket.On("broadcast", message =>
{
    Console.WriteLine($"Broadcast received: {message.Data}");
});

// Send a message to the server
await clientSocket.SendAsync(new Message
{
    Channel = "message",
    Data = "Hello, Server!",
});


var reply = await clientSocket.SendRequestAsync(new Message
{
    Channel = "test",
    Data = "request!"
});

Console.WriteLine($"Reply: {reply.Data}");

while (true)
{
    await Task.Delay(1 * 1000);
    try
    {
        await clientSocket.SendRequestAsync(new Message("heartbeat"));
    }
    catch (Exception ex)
    {
        Console.WriteLine("request failed");
    }
}

await Task.Delay(-1);