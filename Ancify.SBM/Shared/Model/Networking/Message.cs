using MessagePack;
using MessagePack.Formatters;

namespace Ancify.SBM.Shared.Model.Networking;

[MessagePackObject]
public class Message
{
    [Key(0)]
    public string Channel { get; set; }

    [Key(1)]
    [MessagePackFormatter(typeof(TypelessFormatter))]
    public object? Data { get; set; }

    [Key(2)]
    public Guid? ReplyTo { get; set; }

    [Key(3)]
    public Guid MessageId { get; set; } = Guid.NewGuid();

    [Key(4)]
    public Guid SenderId { get; set; }

    [Key(5)]
    public Guid? TargetId { get; set; }

    public Message()
    {
        Channel = string.Empty;
    }

    public Message(string channel, object? data = null, Guid? targetId = null)
    {
        Channel = channel;
        Data = data;
        TargetId = targetId;
    }

    [SerializationConstructor]
    public Message(string channel, object? data, Guid? replyTo, Guid messageId, Guid senderId, Guid? targetId)
    {
        Channel = channel;
        Data = data;
        ReplyTo = replyTo;
        MessageId = messageId;
        SenderId = senderId;
        TargetId = targetId;
    }

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
            Channel = $"{source.Channel}_reply_{source.MessageId}",
            SenderId = source.TargetId ?? Guid.Empty,
            TargetId = source.SenderId,
            ReplyTo = source.MessageId,
            Data = data
        };
    }
}
