using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Localizer.Core;

public sealed record LocalTerminologyRule(string Id, string Pattern, string Instruction);
public sealed record LocalTitleRules(string Version, string Instructions, LocalTerminologyRule[] Rules);
public sealed record LocalGenreTerm(string Original, string Chinese, string English);

/// <summary>One installation's optional, offline rules. Never stores credentials or changes runtime settings.</summary>
public sealed class LocalRulePack
{
    public const string FileName = "local-rules.json";
    public const int MaximumBytes = 1024 * 1024;
    private sealed record Document(int Format, Dictionary<string, LocalTitleRules>? TitleRules, LocalGenreTerm[]? GenreTerms);
    public bool Loaded { get; }
    public string? Digest { get; }
    public IReadOnlyDictionary<string, LocalTitleRules> Titles { get; }
    public IReadOnlyDictionary<string, (string Chinese, string English)>? Genres { get; }
    public static LocalRulePack Empty { get; } = new(false, null, new Dictionary<string, LocalTitleRules>(), null);
    private LocalRulePack(bool loaded, string? digest, Dictionary<string, LocalTitleRules> titles,
        Dictionary<string, (string Chinese, string English)>? genres)
    { Loaded = loaded; Digest = digest; Titles = titles.ToFrozenDictionary(StringComparer.Ordinal); Genres = genres?.ToFrozenDictionary(StringComparer.Ordinal); }

    public static LocalRulePack Load(string dataRoot)
    {
        if (!Path.IsPathFullyQualified(dataRoot)) throw new ArgumentException("Absolute data root required.");
        var path = Path.Combine(dataRoot, FileName);
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Empty; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new JsonException();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumBytes) throw new JsonException();
            var bytes = new byte[MaximumBytes + 1]; var length = 0;
            while (length < bytes.Length)
            {
                var count = file.Read(bytes, length, bytes.Length - length); if (count == 0) break; length += count;
            }
            if (length > MaximumBytes) throw new JsonException();
            return Parse(bytes.AsSpan(0, length));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { throw new InvalidOperationException("LocalRulesInvalid"); }
    }
    public static LocalRulePack Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length > MaximumBytes) throw new JsonException();
            if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
            using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(json.RootElement);
            var document = json.Deserialize<Document>(new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 }) ?? throw new JsonException();
            if (document.Format != 1 || document.TitleRules?.Count > 2 || document.GenreTerms?.Length > 5000) throw new JsonException();
            var titles = new Dictionary<string, LocalTitleRules>(StringComparer.Ordinal);
            foreach (var (language, rules) in document.TitleRules ?? [])
            {
                if (language is not ("zh-Hans" or "en") || rules is null || !Text(rules.Version, 128, true)
                    || !Text(rules.Instructions, 65536) || rules.Rules is null || rules.Rules.Length > 64) throw new JsonException();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rule in rules.Rules)
                {
                    if (rule is null || !Text(rule.Id, 128, true) || !ids.Add(rule.Id) || !Text(rule.Pattern, 1024)
                        || !Text(rule.Instruction, 8192)) throw new JsonException();
                    _ = new Regex(rule.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                // Changing content with an unchanged human version still creates distinct request provenance.
                var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rules))).ToLowerInvariant();
                titles.Add(language, rules with { Version = rules.Version + "/local-" + hash[..16] });
            }
            Dictionary<string, (string Chinese, string English)>? genres = document.GenreTerms is null ? null : new(StringComparer.Ordinal);
            foreach (var term in document.GenreTerms ?? [])
            {
                if (term is null || !Text(term.Original, 512, true) || !Text(term.Chinese, 512, true) || !Text(term.English, 512, true)
                    || !genres!.TryAdd(term.Original, (term.Chinese, term.English))) throw new JsonException();
            }
            return new(true, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), titles, genres);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException)
        { throw new InvalidOperationException("LocalRulesInvalid"); }
    }
    private static bool Text(string? value, int limit, bool singleLine = false) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= limit && value.Trim() == value && !value.Any(c => char.IsControl(c) && (singleLine || c is not ('\r' or '\n' or '\t')));
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException(); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) RejectDuplicates(value);
    }
}

/// <summary>Lazy loading keeps invalid local configuration from aborting Jellyfin startup. Restart to reload.</summary>
public sealed class LocalRulePackProvider(string root)
{
    private readonly Lazy<LocalRulePack> value = new(() => LocalRulePack.Load(root));
    public LocalRulePack Read() => value.Value;
}
