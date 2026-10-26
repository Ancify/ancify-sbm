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

    /// <summary>
    /// The payload as an object-keyed map, whatever map types the sender used (a
    /// <c>Dictionary&lt;string, object?&gt;</c> arrives string-keyed). <see cref="Data"/> is
    /// left as received; a payload that needs converting is returned as a deep copy.
    /// </summary>
    public IReadOnlyDictionary<object, object> AsTypeless()
    {
        if (Data is null) return null!;
        return TypelessMap.Normalize(Data) as IReadOnlyDictionary<object, object>
            ?? throw new InvalidCastException(
                $"Unable to cast object of type '{Data.GetType()}' to type '{typeof(IReadOnlyDictionary<object, object>)}'.");
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
