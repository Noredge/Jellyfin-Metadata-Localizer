using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Localizer.Core;

public enum TargetLanguage { SimplifiedChinese, English }

public static class Languages
{
    public static string Tag(this TargetLanguage language) => language switch
    {
        TargetLanguage.SimplifiedChinese => "zh-Hans",
        TargetLanguage.English => "en",
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };
    public static TargetLanguage Parse(string tag) => tag switch
    {
        "zh-Hans" => TargetLanguage.SimplifiedChinese,
        "en" => TargetLanguage.English,
        _ => throw new ArgumentException("Unsupported language tag.", nameof(tag))
    };
}

public sealed record LibraryKey(string ServerId, string LibraryId);
public sealed record MediaKey(string ServerId, string LibraryId, string ItemId)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ServerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(LibraryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ItemId);
    }
}

public sealed record SourceSnapshot(long Id, MediaKey Key, long Version,
    string OriginalTitle, string DisplayPrefix, string Hash);

public sealed record GenerationSpec(string Endpoint, string Model, string PromptVersion,
    string RulesVersion, string ParametersJson, string ContextJson, string PromptText,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ServiceKind = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ServiceId = null)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(PromptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(RulesVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(PromptText);
        using var parameters = JsonDocument.Parse(ParametersJson);
        using var context = JsonDocument.Parse(ContextJson);
        if (parameters.RootElement.ValueKind != JsonValueKind.Object || context.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Generation parameters and context must be JSON objects.");
    }
}

public sealed record Candidate(string Id, long SourceId, TargetLanguage Language,
    string InputFingerprint, string GeneratedText, string? EditedText,
    long Revision, bool Approved, string ProvenanceJson)
{
    public string Text => EditedText ?? GeneratedText;
    public bool IsHumanEdited => EditedText is not null;
}

public sealed record CandidateSelection(Candidate Candidate, long Revision);

public sealed class RevisionConflictException() : InvalidOperationException("The stored revision changed; reload before editing.");
public sealed class SourceChangedException() : InvalidOperationException("The candidate does not belong to the current source version.");

internal static class Identity
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string Fingerprint(SourceSnapshot source, TargetLanguage language, GenerationSpec spec) =>
        Hash(JsonSerializer.Serialize(new { source.Key, source.Id, source.Hash, Target = language.Tag(), Profile = "default", Spec = spec }));
}
