using System.Text.Json;
using Microsoft.Data.Sqlite;
using Localizer.Core;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

internal static class PersonRenderer
{
    private sealed record Rule(string Id, string Original, string Display, string[] Aliases);
    internal static string Render(SqliteConnection db, SqliteTransaction? tx, string versionId, string language)
    {
        string text, origin, provenance; string? input;
        using (var cmd = Command(db, tx, "SELECT t.text,t.origin,i.names_json,t.provenance_json FROM translation_versions t LEFT JOIN generation_inputs i ON i.id=t.input_id WHERE t.id=$id", ("$id", versionId)))
        using (var r = cmd.ExecuteReader()) { if (!r.Read()) throw new InvalidOperationException("TranslationMissing"); text = r.GetString(0); origin = r.GetString(1); input = r.IsDBNull(2) ? null : r.GetString(2); provenance = r.GetString(3); }
        if (origin is "manual" or "dictionary") return text;
        var rules = new List<Rule>();
        using (var cmd = Command(db, tx, "SELECT id,original,zh_name,en_name,aliases_json FROM person_name_rules"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
            rules.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(language == "en" ? 3 : 2) ? r.GetString(1) : r.GetString(language == "en" ? 3 : 2), JsonSerializer.Deserialize<string[]>(r.GetString(4))!));
        string Resolve(string original, string? id)
        {
            var candidates = rules.Where(x => id is not null ? x.Id == id : x.Original == original || x.Aliases.Contains(original, StringComparer.Ordinal)).ToArray();
            if (candidates.Length > 1) throw new PersonAliasConflictException();
            return candidates.SingleOrDefault()?.Display ?? original;
        }
        using var record = JsonDocument.Parse(provenance);
        if (record.RootElement.TryGetProperty("person_template", out var metadata))
        {
            var template = metadata.Deserialize<PersonTextTemplate>() ?? throw new PersonTemplateException();
            if (template.Frozen() != text) throw new PersonTemplateException();
            return template.Render(slot => Resolve(slot.Original, slot.PersonId));
        }
        // Historical plain text has no trustworthy name positions. Never replace substrings
        // in it: even a Chinese name can also occur inside an ordinary word.
        if (input is not null)
        {
            using var snapshot = JsonDocument.Parse(input);
            foreach (var name in snapshot.RootElement.EnumerateArray())
            {
                var original = name.GetProperty("original").GetString()!;
                if (rules.Count(x => x.Original == original || x.Aliases.Contains(original, StringComparer.Ordinal)) > 1) throw new PersonAliasConflictException();
                var frozen = name.GetProperty("replacement");
                var old = frozen.ValueKind == JsonValueKind.String ? frozen.GetString()! : original;
                var id = name.TryGetProperty("id", out var key) ? key.GetString() : null;
                if (old != Resolve(original, id) && PersonNameMatching.Contains(text, old)) throw new PersonTemplateException(required: true);
            }
        }
        return text;
    }
}
