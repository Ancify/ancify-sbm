using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Interfaces;

public interface ITransport
{
    Task ConnectAsync(int maxRetries = 5, int delayMilliseconds = 1000);
    Task SendAsync(Message message);
    IAsyncEnumerable<Message> ReceiveAsync(CancellationToken cancellationToken = default);
    event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;
    void OnAuthenticated();
    void Close();
}