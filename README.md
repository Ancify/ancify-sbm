# Ancify Simple Bidirectional Messaging System

## About

This was made as a simple bidirectional messaging system following a server<->clients architecture.
Protocol and serialization formats can be (somewhat) freely chosen. Default uses TCP + MessagePack serialization.

## Server Usage

Here's a basic example of how to set up a server

```cs
var serverSocket = new ServerSocket(System.Net.IPAddress.Loopback, port: 5555);

serverSocket.ClientConnected += (s, args) =>
{
    var socket = args.ClientSocket;

    Console.WriteLine($"Client connected: {socket.ClientId}");

    socket.On("message", message =>
    {
        Console.WriteLine(message.Data)
    });
};

_ = serverSocket.StartAsync();
```

socket.On(...) has multiple overloads to allow for several handler types like
- Asynchronous lambda
- Returning a message
- Synchronous lambda with & without return value...

## Client Usage

Here's a basic example of how to program a client


```cs
var transport = new TcpTransport("127.0.0.1", port: 5555);
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

await clientSocket.SendAsync(new Message
{
    Channel = "message",
    Data = "Hello, Server!",
});

```