namespace Ancify.SBM.Shared;

public static class SbmUtils
{
    public static List<T> ToConvertedList<T>(object data, Func<IReadOnlyDictionary<object, object>, T> converter)
    {
        if (data is not object[] rawArray)
            return [];

        return [.. rawArray.Cast<IReadOnlyDictionary<object, object>>().Select(converter)];
    }
}

