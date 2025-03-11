namespace Ancify.SBM.Shared;

public static class SbmUtils
{
    public static List<T> ToConvertedList<T>(object data, Func<IReadOnlyDictionary<object, object>, T> converter)
    {
        var rawArray = (IReadOnlyDictionary<object, object>[])data;
        return [.. rawArray.Select(x => converter(x))];
    }
}
