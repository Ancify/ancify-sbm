// See https://aka.ms/new-console-template for more information
using Ancify.SBM.Client;
using Ancify.SBM.Server;
using Ancify.SBM.Shared.Model.Networking;
using Ancify.SBM.Shared.Transport.TCP;

Console.WriteLine("Hello, World!");

var serverSocket = new ServerSocket(System.Net.IPAddress.Loopback, 12345);

serverSocket.ClientConnected += (s, e) =>
{
    Console.WriteLine($"Client connected: {e.ClientSocket.ClientId}");

    e.ClientSocket.On("message", async message =>
    {
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

    e.ClientSocket.On("test", async message =>
    {
        return Message.FromReply(message, "reply!");
    });
};

_ = serverSocket.StartAsync();

var transport = new TcpTransport("127.0.0.1", 11361);
var clientSocket = new ClientSocket(transport);

clientSocket.ClientIdReceived += (s, id) =>
{
    Console.WriteLine($"Assigned Client ID: {id}");
};

await clientSocket.ConnectAsync();

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

await Task.Delay(-1);