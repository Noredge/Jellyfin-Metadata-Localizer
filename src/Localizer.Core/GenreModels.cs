using System.Text.Json;

namespace Localizer.Core;

// Exact ordinal set semantics: ignore only order and exact duplicates. No case folding,
// Unicode normalization, trimming, synonyms or semantic merging. Preserve raw arrays for restore.
public static class GenreValues
{
    public static string[] Copy(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = values.ToArray();
        if (copy.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Genre values cannot be blank.");
        return copy;
    }
    public static string Canonical(IEnumerable<string> values) => JsonSerializer.Serialize(Copy(values).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    public static bool Equal(IEnumerable<string> left, IEnumerable<string> right) => Canonical(left) == Canonical(right);
}

public sealed record GenreDictionaryEntry(string Original, string? Builtin, string? Override, string? Effective, long Revision, bool Unknown);
