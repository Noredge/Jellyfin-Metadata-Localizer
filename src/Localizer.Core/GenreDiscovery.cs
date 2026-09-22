namespace Localizer.Core;

public static class GenreDiscovery
{
    // Current display is observation only, never an implicit source confirmation. Suppress
    // translations only for this item's confirmed source, not every translated word in the dictionary.
    public static string[] ObservedTerms(IEnumerable<string> current, IEnumerable<string>? confirmedOriginal,
        Func<string, TargetLanguage, string?> lookup)
    {
        var originals = confirmedOriginal?.Distinct(StringComparer.Ordinal).ToArray();
        if (originals is null) return GenreValues.Copy(current).Distinct(StringComparer.Ordinal).ToArray();
        var rendered = originals.SelectMany(x => new[] { lookup(x, TargetLanguage.SimplifiedChinese), lookup(x, TargetLanguage.English) })
            .Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        return originals.Concat(GenreValues.Copy(current).Where(x => !rendered.Contains(x)))
            .Distinct(StringComparer.Ordinal).ToArray();
    }
}
