using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Interfaces;

public interface ITransport
{
    public bool AlwaysReconnect { get; set; }
    public int MaxConnectWaitTime { get; set; }
    Task ConnectAsync(int maxRetries = 5, int delayMilliseconds = 1000, bool isReconnect = false);
    Task SendAsync(Message message);
    Task Reconnect();
    IAsyncEnumerable<Message> ReceiveAsync(CancellationToken cancellationToken = default);
    event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;
    void OnAuthenticated();
    void Close();
}