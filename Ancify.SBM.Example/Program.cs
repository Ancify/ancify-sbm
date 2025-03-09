// See https://aka.ms/new-console-template for more information
using System.Net;
using System.Text.Json;

using Ancify.SBM.Client;
using Ancify.SBM.Example;
using Ancify.SBM.Generated;
using Ancify.SBM.Server;
using Ancify.SBM.Shared;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Model.Networking;
using Ancify.SBM.Shared.Transport.TCP;

using Microsoft.Extensions.Logging;

Console.WriteLine("Hello, World!");

var port = 12345;

int counter = 0;

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
var serverSocket = new ServerSocket(IPAddress.Any, port, sslConfig, useWebSocket: false, (id, key, scope) => Task.FromResult(new AuthContext("1234", [])));

serverSocket.ServerConfig.ErrorHandler = (message, exception) => Message.FromReply(message, new
{
    Success = false,
    exception.Message
});

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

    e.ClientSocket.On("exception_test", message =>
    {
        throw new Exception();
        return Message.FromReply(message, null);
    });

    e.ClientSocket.On("test", async message =>
    {
        await Task.Delay(1000);
        return Message.FromReply(message, "reply!");
    });

    e.ClientSocket.On("heartbeat", message =>
    {
        counter++;
        Console.WriteLine($"server heatbeat {counter} ({serverSocket.ClientCount} connected client(s))");
        return Message.FromReply(message, $"heartbeat {counter}");
    });

    e.ClientSocket.On("dto", message =>
    {
        //Console.WriteLine(message.AsTypeless().FromDictionary());
        //var result = Generated.TestDtoConverter.FromMessage(message);
        var result = message.ToTestDto();
        var result2 = message.ToTestDto2();
        Console.WriteLine(result.Name);
    });

    e.ClientSocket.On<ConnectionStatusEventArgs>(EventType.ConnectionStatusChanged, args =>
    {
        if (args.Status == ConnectionStatus.Disconnected)
        {
            Console.WriteLine("Disconnected from server");
        }
    });

    //Test((ConnectedClientSocket)e.ClientSocket);
};

_ = serverSocket.StartAsync();

var transport = new TcpTransport("127.0.0.1", (ushort)port, sslConfig) { AlwaysReconnect = true };
//var transport = new WebsocketTransport($"ws://192.168.86.28:{port}");
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

var faultyReply = await clientSocket.SendRequestAsync(new Message
{
    Channel = "exception_test"
});

await clientSocket.SendAsync(new Message("dto", new { Name = "Red" }));

Console.WriteLine($"Faulty reply: {JsonSerializer.Serialize(faultyReply.Data)}");

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

[SbmDto]
public record TestDto(string Name);

[SbmDto]
public class TestDto2 { public required string Name { get; set; } }