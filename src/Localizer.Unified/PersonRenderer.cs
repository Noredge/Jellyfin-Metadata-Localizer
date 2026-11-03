using System.Text.Json;
using Microsoft.Data.Sqlite;
using Localizer.Core;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

internal static class PersonRenderer
{
    private sealed record Rule(string Id, string Original, string Display, string[] Aliases);
    internal static string Render(SqliteConnection db, SqliteTransaction? tx, string versionId, string language) =>
        new Session(db, tx, language).Render(versionId);

    // Bound to its caller's transaction/read operation, never cached across rule edits.
    internal sealed class Session(SqliteConnection db, SqliteTransaction? tx, string language)
    {
        private List<Rule>? rules;
        private List<Rule> Rules
        {
            get
            {
                if (rules is not null) return rules;
                using var timing = OperationTimings.Measure("person_rules_load");
                rules = [];
                using var cmd = Command(db, tx, "SELECT id,original,zh_name,en_name,aliases_json FROM person_name_rules");
                using var r = cmd.ExecuteReader();
                while (r.Read()) rules.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(language == "en" ? 3 : 2) ? r.GetString(1) : r.GetString(language == "en" ? 3 : 2), JsonSerializer.Deserialize<string[]>(r.GetString(4))!));
                return rules;
            }
        }
        private string Resolve(string original, string? id)
        {
            var candidates = Rules.Where(x => id is not null ? x.Id == id : x.Original == original || x.Aliases.Contains(original, StringComparer.Ordinal)).ToArray();
            if (candidates.Length > 1) throw new PersonAliasConflictException();
            return candidates.SingleOrDefault()?.Display ?? original;
        }
        internal string Render(string versionId)
        {
            string text, origin, provenance; string? input;
            using (var cmd = Command(db, tx, "SELECT t.text,t.origin,i.names_json,t.provenance_json FROM translation_versions t LEFT JOIN generation_inputs i ON i.id=t.input_id WHERE t.id=$id", ("$id", versionId)))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException("TranslationMissing");
                text = r.GetString(0); origin = r.GetString(1); input = r.IsDBNull(2) ? null : r.GetString(2); provenance = r.GetString(3);
            }
            if (origin is "manual" or "dictionary") return text;
            using var record = JsonDocument.Parse(provenance);
            if (record.RootElement.TryGetProperty("person_template", out var metadata))
            {
                var template = metadata.Deserialize<PersonTextTemplate>() ?? throw new PersonTemplateException();
                if (template.Frozen() != text) throw new PersonTemplateException();
                return template.Slots.Length == 0 ? text : template.Render(slot => Resolve(slot.Original, slot.PersonId));
            }
            // Historical plain text has no trustworthy name positions. An absent/empty
            // frozen context needs neither substitutions nor loading the person's dictionary.
            if (input is null) return text;
            using var snapshot = JsonDocument.Parse(input);
            foreach (var name in snapshot.RootElement.EnumerateArray())
            {
                var original = name.GetProperty("original").GetString()!;
                if (Rules.Count(x => x.Original == original || x.Aliases.Contains(original, StringComparer.Ordinal)) > 1) throw new PersonAliasConflictException();
                var frozen = name.GetProperty("replacement");
                var old = frozen.ValueKind == JsonValueKind.String ? frozen.GetString()! : original;
                var id = name.TryGetProperty("id", out var key) ? key.GetString() : null;
                if (old != Resolve(original, id) && PersonNameMatching.Contains(text, old)) throw new PersonTemplateException(required: true);
            }
            return text;
        }
    }
}
