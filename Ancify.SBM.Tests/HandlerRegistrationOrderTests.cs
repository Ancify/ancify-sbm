using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Ancify.SBM.Client;
using Ancify.SBM.Interfaces;
using Ancify.SBM.Server;
using Ancify.SBM.Shared.Model;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

/// <summary>
/// Sockets start their receive loop in the constructor. A frame that is already
/// buffered when the socket is built (a client sends _auth_ right after connect) is
/// dispatched on a pool thread, so the built-in handlers must exist before that loop
/// starts, or the frame finds no handler and is dropped without a reply.
/// </summary>
[TestClass]
public class HandlerRegistrationOrderTests
{
    private const int Iterations = 500;

    [TestMethod]
    public async Task ConnectedClientSocket_AnswersAuthFrameBufferedBeforeConstruction()
    {
        var server = TestUtil.CreateServer(TestUtil.GetFreePort(),
            (id, _, _) => Task.FromResult(new AuthContext(id)));

        for (var i = 0; i < Iterations; i++)
        {
            var auth = new Message("_auth_", new Dictionary<string, object> { ["Id"] = "alice", ["Key"] = "k" });
            var transport = new PreloadedTransport(auth);
            var socket = new ConnectedClientSocket(transport, server) { ClientId = Guid.NewGuid() };
            try
            {
                var reply = await transport.FirstSent.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.AreEqual(auth.MessageId, reply.ReplyTo, $"Iteration {i}: the buffered _auth_ frame was not answered.");
            }
            finally { socket.Dispose(); }
        }
    }

    [TestMethod]
    public async Task ClientSocket_AnswersStatusFrameBufferedBeforeConstruction()
    {
        for (var i = 0; i < Iterations; i++)
        {
            var status = new Message("__$status");
            var transport = new PreloadedTransport(status);
            var socket = new ClientSocket(transport);
            try
            {
                var reply = await transport.FirstSent.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.AreEqual(status.MessageId, reply.ReplyTo, $"Iteration {i}: the buffered __$status frame was not answered.");
            }
            finally { socket.Dispose(); }
        }
    }

    private sealed class PreloadedTransport(Message preloaded) : ITransport
    {
        private readonly TaskCompletionSource<Message> _firstSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _closed = new();

        public Task<Message> FirstSent => _firstSent.Task;
        public bool AlwaysReconnect { get; set; }
        public int MaxConnectWaitTime { get; set; }
        public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged { add { } remove { } }

        public Task ConnectAsync(int maxRetries = 5, int delayMilliseconds = 1000, bool isReconnect = false) => Task.CompletedTask;
        public Task Reconnect() => Task.CompletedTask;
        public void OnAuthenticated() { }
        public void Close() => _closed.Cancel();

        public Task SendAsync(Message message)
        {
            _firstSent.TrySetResult(message);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Message> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return preloaded;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            try { await Task.Delay(Timeout.Infinite, linked.Token); }
            catch (OperationCanceledException) { }
        }
    }
}
