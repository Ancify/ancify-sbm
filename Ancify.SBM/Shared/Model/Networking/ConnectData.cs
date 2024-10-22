namespace Ancify.SBM.Shared.Model.Networking;
public class ConnectData
{
    public required string Host { get; set; }
    public required ushort Port { get; set; }
    public List<string> Meta { get; set; } = [];
}
