namespace Ancify.SBM.Shared.Model.Networking;
public enum ConnectionStatus
{
    Connecting,
    Connected,
    Disconnected,
    Authenticating,
    Failed,
    Cancelled
}


public class ConnectionStatusEventArgs(ConnectionStatus status) : EventArgs
{
    public ConnectionStatus Status { get; set; } = status;
}
