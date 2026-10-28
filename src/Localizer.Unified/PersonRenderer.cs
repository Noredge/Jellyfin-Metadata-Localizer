using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

internal static class PersonRenderer
{
    internal static string Render(SqliteConnection db, SqliteTransaction? tx, string versionId, string language)
    {
        string text, source, origin; string? input;
        using (var cmd = Command(db, tx, "SELECT t.text,s.content,t.origin,i.names_json FROM translation_versions t JOIN source_versions s ON s.id=t.source_id LEFT JOIN generation_inputs i ON i.id=t.input_id WHERE t.id=$id", ("$id", versionId)))
        using (var r = cmd.ExecuteReader()) { if (!r.Read()) throw new InvalidOperationException("TranslationMissing"); text = r.GetString(0); source = r.GetString(1); origin = r.GetString(2); input = r.IsDBNull(3) ? null : r.GetString(3); }
        if (origin is "manual" or "dictionary") return text;
        var current = new Dictionary<string, (string Value, string[] Aliases)>(StringComparer.Ordinal);
        using (var cmd = Command(db, tx, "SELECT original,zh_name,en_name,aliases_json FROM person_name_rules ORDER BY original"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            var aliases = JsonSerializer.Deserialize<string[]>(r.GetString(3))!; var name = r.GetString(0);
            if (source.Contains(name, StringComparison.Ordinal) || aliases.Any(a => source.Contains(a, StringComparison.Ordinal))) current[name] = (r.IsDBNull(language == "en" ? 2 : 1) ? name : r.GetString(language == "en" ? 2 : 1), aliases);
        }
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (original, rule) in current)
        { replacements.TryAdd(original, rule.Value); foreach (var alias in rule.Aliases) replacements.TryAdd(alias, rule.Value); }
        if (input is not null)
        {
            using var snapshot = JsonDocument.Parse(input);
            foreach (var item in snapshot.RootElement.EnumerateArray())
            {
                var original = item.GetProperty("original").GetString()!; var old = item.GetProperty("replacement");
                if (old.ValueKind == JsonValueKind.String && old.GetString() is { Length: > 0 } value)
                    replacements.TryAdd(value, current.TryGetValue(original, out var rule) ? rule.Value : original);
            }
        }
        var tokens = replacements.Where(x => x.Key != x.Value && text.Contains(x.Key, StringComparison.Ordinal)).OrderByDescending(x => x.Key.Length).Select(x =>
            (char.IsAsciiLetterOrDigit(x.Key[0]) ? "(?<![A-Za-z0-9_])" : "") + Regex.Escape(x.Key) + (char.IsAsciiLetterOrDigit(x.Key[^1]) ? "(?![A-Za-z0-9_])" : "")).ToArray();
        return tokens.Length == 0 ? text : Regex.Replace(text, string.Join('|', tokens), match => replacements[match.Value], RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    }
}
