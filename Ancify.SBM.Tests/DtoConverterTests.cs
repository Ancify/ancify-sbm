using Ancify.SBM.Generated;
using Ancify.SBM.Shared.Model.Networking;

namespace Ancify.SBM.Tests;

/// <summary>
/// Mirrors the shape of AMDS SbmEntry: a non-required value-type Id (server-assigned,
/// optional on the wire per the emit-only-when-set convention), required collections,
/// and assorted optional fields.
/// </summary>
[SbmDto(ignoreCasing: true)]
public class ConverterProbeDto
{
    public Guid Id { get; set; }
    public required List<string> Regions { get; set; }
    public bool IsParty { get; set; }
    public int Count { get; set; }
    public string? Env { get; set; }
    public bool? AllowsBackfill { get; set; }
    public List<string>? Platforms { get; set; }
}

/// <summary>
/// A non-required, non-nullable reference-type property must still be treated as
/// missing-is-an-error (defaulting it would smuggle in a null).
/// </summary>
[SbmDto]
public class ConverterRefProbeDto
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Exercises the [SbmDto] source-generated FromDictionary converters.
///
/// Regression coverage for the AMDS queue::join failure: a spec-compliant client
/// (py/ts SDK) omits server-assigned value-type fields such as Entry.Id, and the
/// generated converter used to throw KeyNotFoundException("Non-nullable property
/// 'Id' not found") instead of defaulting them.
/// </summary>
[TestClass]
public class DtoConverterTests
{
    private static Dictionary<object, object> BaseDict() => new()
    {
        ["Regions"] = new List<object> { "eu", "us" },
    };

    [TestMethod]
    public void MissingValueTypeProperties_DefaultInsteadOfThrowing()
    {
        // No Id / IsParty / Count in the payload — the py/ts SDKs omit unset fields.
        var dto = ConverterProbeDtoConverter.FromDictionary(BaseDict(), ignoreCasing: true);

        Assert.AreEqual(Guid.Empty, dto.Id);
        Assert.IsFalse(dto.IsParty);
        Assert.AreEqual(0, dto.Count);
        CollectionAssert.AreEqual(new List<string> { "eu", "us" }, dto.Regions);
    }

    [TestMethod]
    public void PresentValueTypeProperties_AreParsed()
    {
        var id = Guid.NewGuid();
        var data = BaseDict();
        data["Id"] = id.ToString();
        data["IsParty"] = true;
        data["Count"] = 3;

        var dto = ConverterProbeDtoConverter.FromDictionary(data, ignoreCasing: true);

        Assert.AreEqual(id, dto.Id);
        Assert.IsTrue(dto.IsParty);
        Assert.AreEqual(3, dto.Count);
    }

    [TestMethod]
    public void MissingRequiredProperty_StillThrows()
    {
        var data = new Dictionary<object, object> { ["Id"] = Guid.NewGuid().ToString() };

        var ex = Assert.ThrowsException<KeyNotFoundException>(
            () => ConverterProbeDtoConverter.FromDictionary(data, ignoreCasing: true));
        StringAssert.Contains(ex.Message, "Regions");
    }

    [TestMethod]
    public void MissingNullableProperties_DefaultToNull()
    {
        var dto = ConverterProbeDtoConverter.FromDictionary(BaseDict(), ignoreCasing: true);

        Assert.IsNull(dto.Env);
        Assert.IsNull(dto.AllowsBackfill);
        Assert.IsNull(dto.Platforms);
    }

    [TestMethod]
    public void MissingNonNullableReferenceProperty_StillThrows()
    {
        var ex = Assert.ThrowsException<KeyNotFoundException>(
            () => ConverterRefProbeDtoConverter.FromDictionary(
                new Dictionary<object, object>(), ignoreCasing: false));
        StringAssert.Contains(ex.Message, "Name");
    }

    [TestMethod]
    public void FromMessage_MissingValueTypeId_Defaults()
    {
        var message = new Message("probe", BaseDict());

        var dto = message.ToConverterProbeDto();

        Assert.AreEqual(Guid.Empty, dto.Id);
    }
}
