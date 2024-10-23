using MessagePack;

namespace Ancify.SBM.Shared.Model.Networking;

[MessagePackObject]
public class Message
{
    [Key(0)]
    public required string Channel { get; set; }

    [Key(1)]
    public object? Data { get; set; }

    [Key(2)]
    public Guid? ReplyTo { get; set; }

    [Key(3)]
    public Guid MessageId { get; set; } = Guid.NewGuid();

    [Key(4)]
    public Guid SenderId { get; set; }

    [Key(5)]
    public Guid? TargetId { get; set; }

    public T As<T>()
    {
        return (T)Data!;
    }

    public bool SenderIsServer()
    {
        return SenderId == Guid.Empty;
    }

    public static Message FromReply(Message source, object? data)
    {
        return new Message
        {
            Channel = $"{source.Channel}_reply",
            SenderId = source.TargetId ?? Guid.Empty,
            TargetId = source.SenderId,
            ReplyTo = source.MessageId,
            Data = data
        };
    }
}
