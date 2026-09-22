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

public sealed record GenreSource(long Id, MediaKey Key, long Version, string[] OriginalValues);
public sealed record GenreOverride(TargetLanguage Language, string Original, string? Replacement, long Revision);
public sealed record GenreDictionaryEntry(string Original, string? Builtin, string? Override, string? Effective,
    long Revision, bool Unknown);
public sealed record GenreMapping(string Id, long SourceId, TargetLanguage Language, string[] Values, string[] UnknownValues);
public sealed record GenreTargetSnapshot(MediaKey Key, bool IsMovie, bool Locked, bool MetadataSaversExplicitlyDisabled, string[] Values);
public sealed record GenreWriteRequest(MediaKey Key, string[] ExpectedValues, string[] ProposedValues);
public interface IGenreTarget
{
    ValueTask<GenreTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken cancellationToken);
    ValueTask<TargetWriteResult> WriteAsync(GenreWriteRequest request, CancellationToken cancellationToken);
}
public sealed record GenrePreviewEntry(MediaKey Key, TargetLanguage Language, PreviewStatus Status, long SourceId,
    string MappingId, string[] ExpectedValues, string[] ProposedValues, string[] UnknownValues, bool Original = false);
public sealed record GenreOperation(string Id, string RequestFingerprint, MediaKey Key, OperationKind Kind,
    OperationState State, long SourceId, string? MappingId, TargetLanguage Language,
    string[] BeforeValues, string[] PlannedValues, string? ParentApplyId, bool AcceptedUncertainAttribution,
    bool ObservedOnly, string? ErrorCategory, string CreatedUtc, string UpdatedUtc, bool Original = false);
public sealed record GenreRestorePoint(GenreOperation Application, bool Consumed);

internal static class GenreDictionary
{
    // Maintained once per exact source term. Includes source-supplied format and promotion
    // labels: dictionary updates translate these labels, never silently remove or classify them.
    private static readonly IReadOnlyDictionary<string, (string Chinese, string English)> Builtin = Load();
    private static IReadOnlyDictionary<string, (string Chinese, string English)> Load()
    {
        using var stream = typeof(GenreDictionary).Assembly.GetManifestResourceStream("Localizer.Genres.tsv")
            ?? throw new InvalidOperationException("Genre dictionary resource missing.");
        using var reader = new StreamReader(stream);
        if (reader.ReadLine() != "Original\tChinese\tEnglish") throw new InvalidOperationException("Invalid genre dictionary header.");
        var entries = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var values = line.Split('\t');
            if (values.Length != 3 || values.Any(string.IsNullOrWhiteSpace) || !entries.TryAdd(values[0], (values[1], values[2])))
                throw new InvalidOperationException("Invalid or duplicate genre dictionary entry.");
        }
        return entries;
    }
    public static string? Lookup(string value, TargetLanguage language)
    {
        _ = language.Tag();
        return Builtin.TryGetValue(value, out var found) ? language == TargetLanguage.SimplifiedChinese ? found.Chinese : found.English : null;
    }
}
