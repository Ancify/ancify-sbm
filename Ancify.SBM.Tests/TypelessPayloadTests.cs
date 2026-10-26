using Ancify.SBM.Generated;
using Ancify.SBM.Shared.Model.Networking;

using MessagePack;

namespace Ancify.SBM.Tests;

[SbmDto(ignoreCasing: true)]
public class TypelessNestedProbeDto
{
    public string Name { get; set; } = string.Empty;
    public TypelessLeafProbeDto? Leaf { get; set; }
    public List<TypelessLeafProbeDto> Leaves { get; set; } = [];
    public List<string> Tags { get; set; } = [];
}

[SbmDto(ignoreCasing: true)]
public class TypelessLeafProbeDto
{
    public int Count { get; set; }
}

/// <summary>
/// The typeless formatter keeps a sender's concrete map type, so a C# peer that builds
/// <c>Dictionary&lt;string, object?&gt;</c> payloads used to make <c>AsTypeless()</c> throw on
/// the receiving side.
/// </summary>
[TestClass]
public class TypelessPayloadTests
{
    private static Message OverTheWire(object payload)
        => MessagePackSerializer.Deserialize<Message>(MessagePackSerializer.Serialize(new Message("ch", payload)));

    private static Dictionary<string, object?> StringKeyedPayload() => new()
    {
        ["Name"] = "probe",
        ["Leaf"] = new Dictionary<string, object?> { ["Count"] = 1 },
        ["Leaves"] = new List<Dictionary<string, object?>> { new() { ["Count"] = 2 }, new() { ["Count"] = 3 } },
        ["Tags"] = new List<string> { "a", "b" },
        ["Missing"] = null,
    };

    [TestMethod]
    public void AsTypeless_ReadsStringKeyedMapsAtEveryDepth()
    {
        var message = OverTheWire(StringKeyedPayload());
        Assert.IsInstanceOfType<Dictionary<string, object>>(message.Data, "the formatter keeps the sender's type");

        var data = message.AsTypeless();

        Assert.AreEqual("probe", data["Name"]);
        Assert.AreEqual(1, Convert.ToInt32(((IReadOnlyDictionary<object, object>)data["Leaf"])["Count"]));
        var leaves = ((IEnumerable<object>)data["Leaves"]).Cast<IReadOnlyDictionary<object, object>>().ToList();
        Assert.AreEqual(3, Convert.ToInt32(leaves[1]["Count"]));
        Assert.IsNull(data["Missing"]);
    }

    [TestMethod]
    public void GeneratedConverter_ReadsAStringKeyedPayload()
    {
        var dto = OverTheWire(StringKeyedPayload()).ToTypelessNestedProbeDto();

        Assert.AreEqual("probe", dto.Name);
        Assert.AreEqual(1, dto.Leaf!.Count);
        CollectionAssert.AreEqual(new[] { 2, 3 }, dto.Leaves.Select(l => l.Count).ToArray());
        CollectionAssert.AreEqual(new[] { "a", "b" }, dto.Tags);
    }

    [TestMethod]
    public void AsTypeless_ReturnsAnObjectKeyedPayloadAsIs()
    {
        var message = OverTheWire(new Dictionary<object, object?>
        {
            ["Leaf"] = new Dictionary<object, object?> { ["Count"] = 1 },
            ["Tags"] = new[] { "a" },
        });

        Assert.AreSame(message.Data, message.AsTypeless());
    }

    [TestMethod]
    public void AsTypeless_LeavesDataAsReceived()
    {
        var message = OverTheWire(StringKeyedPayload());
        var received = message.Data;

        message.AsTypeless();

        Assert.AreSame(received, message.Data);
    }

    [TestMethod]
    public void AsTypeless_StillThrowsInvalidCastForANonMapPayload()
    {
        Assert.ThrowsException<InvalidCastException>(() => OverTheWire("not a map").AsTypeless());
    }

    [TestMethod]
    public void AsTypeless_StillReturnsNullForNoPayload()
    {
        Assert.IsNull(new Message("ch").AsTypeless());
    }
}
