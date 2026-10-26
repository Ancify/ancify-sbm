using System.Collections;

namespace Ancify.SBM.Shared.Model.Networking;

/// <summary>
/// Brings a typeless payload into the shape an object-keyed sender produces. The typeless
/// formatter keeps the sender's concrete types, so a peer that builds a
/// <c>Dictionary&lt;string, object?&gt;</c> arrives as <c>Dictionary&lt;string, object&gt;</c>,
/// which is not an <c>IReadOnlyDictionary&lt;object, object&gt;</c>.
/// </summary>
internal static class TypelessMap
{
    /// <summary>
    /// Returns <paramref name="value"/> itself when every map in it is already object-keyed,
    /// otherwise a deep copy with maps as <c>Dictionary&lt;object, object&gt;</c> and lists as
    /// <c>object[]</c> (what an object-keyed payload deserializes to).
    /// </summary>
    public static object? Normalize(object? value) => IsCanonical(value) ? value : Copy(value);

    private static bool IsCanonical(object? value) => value switch
    {
        null or string or byte[] => true,
        IReadOnlyDictionary<object, object> map => map.Values.All(IsCanonical),
        IDictionary => false,
        IEnumerable items => items.Cast<object?>().All(IsCanonical),
        _ => true,
    };

    private static object? Copy(object? value)
    {
        switch (value)
        {
            case null or string or byte[]:
                return value;
            case IDictionary map:
            {
                // IDictionary's own enumerator yields DictionaryEntry; going through
                // IEnumerable on a generic dictionary would yield KeyValuePair instead.
                Dictionary<object, object> copy = new(map.Count);
                var entries = map.GetEnumerator();
                while (entries.MoveNext()) copy[entries.Key] = Copy(entries.Value)!;
                return copy;
            }
            case IReadOnlyDictionary<object, object> map:
                return map.ToDictionary(e => e.Key, e => Copy(e.Value)!);
            case IEnumerable items:
                return items.Cast<object?>().Select(Copy).ToArray();
            default:
                return value;
        }
    }
}
