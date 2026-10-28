using System.Text.Json;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

public sealed record PersonRule(string Id, string Original, string? Chinese, string? English, string[] Aliases, long Revision);
public sealed record PersonPage(long Total, PersonRule[] Items, int Offset, int Limit);
public sealed record GenreTermView(string Id, string Original, long ControlRevision, string? Chinese, string? English, string? ChineseOrigin, string? EnglishOrigin);

public sealed class ConfigurationStore(UnifiedDatabase database)
{
    private static readonly Lazy<Dictionary<string, (string Chinese, string English)>> Builtin = new(() =>
    {
        using var stream = typeof(ConfigurationStore).Assembly.GetManifestResourceStream("Localizer.Unified.Genres.tsv")!;
        using var reader = new StreamReader(stream); if (reader.ReadLine() != "Original\tChinese\tEnglish") throw new InvalidOperationException("BuiltinDictionaryInvalid");
        var values = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line) { var fields = line.Split('\t'); if (fields.Length != 3) throw new InvalidOperationException("BuiltinDictionaryInvalid"); values.Add(fields[0], (fields[1], fields[2])); }
        return values;
    });
    public void SeedBuiltin() => database.Write((db, tx) =>
    {
        foreach (var (original, names) in Builtin.Value)
        {
            var term = Hash("term\0" + original); var source = Hash("term-source\0" + original);
            Execute(db, tx, "INSERT OR IGNORE INTO source_versions(id,kind,owner_id,content,content_hash,created_utc) VALUES($s,'Term',$t,$o,$h,$n)", ("$s", source), ("$t", term), ("$o", original), ("$h", Hash(original)), ("$n", Now()));
            Execute(db, tx, "INSERT OR IGNORE INTO genre_terms(id,original,source_id) VALUES($id,$o,$s)", ("$id", term), ("$o", original), ("$s", source));
            foreach (var (language, text) in new[] { ("zh-Hans", names.Chinese), ("en", names.English) })
            {
                if (Scalar<long>(db, tx, "SELECT COUNT(*) FROM translation_selections WHERE source_id=$s AND language=$l", ("$s", source), ("$l", language)) > 0) continue;
                var id = Hash("builtin\0" + original + "\0" + language + "\0" + text);
                Execute(db, tx, "INSERT OR IGNORE INTO translation_versions(id,source_id,language,text,text_hash,origin,provenance_json,created_utc) VALUES($id,$s,$l,$t,$h,'import','{\"builtin\":true}',$n)",
                    ("$id", id), ("$s", source), ("$l", language), ("$t", text), ("$h", Hash(text)), ("$n", Now()));
                ContentStore.Select(db, tx, source, language, id);
            }
        }
        return true;
    }, wakeExecutor: false);
    public GenreTermView[] Terms(string search = "", int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = database.OpenRead(); using var cmd = Command(db, null, """
            SELECT g.id,g.original,g.control_revision,z.text,e.text,z.origin,e.origin FROM genre_terms g
            LEFT JOIN translation_selections zs ON zs.source_id=g.source_id AND zs.language='zh-Hans' LEFT JOIN translation_versions z ON z.id=zs.translation_id
            LEFT JOIN translation_selections es ON es.source_id=g.source_id AND es.language='en' LEFT JOIN translation_versions e ON e.id=es.translation_id
            WHERE $q='' OR instr(g.original,$q)>0 OR instr(ifnull(z.text,''),$q)>0 OR instr(ifnull(e.text,''),$q)>0 ORDER BY g.original LIMIT $limit OFFSET $offset
            """, ("$q", search), ("$limit", limit), ("$offset", offset));
        using var r = cmd.ExecuteReader(); var values = new List<GenreTermView>();
        while (r.Read()) values.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6))); return [.. values];
    }
    public TranslationVersion SaveTerm(string original, string language, string text, long expectedControl, string commandId)
    {
        ContentStore.ValidateLanguage(language); ArgumentException.ThrowIfNullOrWhiteSpace(original); ArgumentException.ThrowIfNullOrWhiteSpace(text); ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        if (original.Length > 512 || text.Length > 512) throw new ArgumentException("InvalidTerm");
        return database.Write((db, tx) =>
        {
            var term = Hash("term\0" + original); var source = Hash("term-source\0" + original); var id = Hash("manual-term\0" + commandId);
            var fp = Hash(JsonSerializer.Serialize(new { original, language, text, expectedControl }));
            using (var old = Command(db, tx, "SELECT id,source_id,language,text,origin,created_utc,provenance_json FROM translation_versions WHERE id=$id", ("$id", id)))
            using (var r = old.ExecuteReader()) if (r.Read())
            {
                using var data = JsonDocument.Parse(r.GetString(6)); if (data.RootElement.GetProperty("command_hash").GetString() != fp) throw new InvalidOperationException("SubmissionConflict"); return ContentStore.Translation(r);
            }
            Execute(db, tx, "INSERT OR IGNORE INTO source_versions(id,kind,owner_id,content,content_hash,created_utc) VALUES($s,'Term',$t,$o,$h,$n)", ("$s", source), ("$t", term), ("$o", original), ("$h", Hash(original)), ("$n", Now()));
            Execute(db, tx, "INSERT OR IGNORE INTO genre_terms(id,original,source_id) VALUES($id,$o,$s)", ("$id", term), ("$o", original), ("$s", source));
            if (Scalar<long>(db, tx, "SELECT control_revision FROM genre_terms WHERE id=$t", ("$t", term)) != expectedControl) throw new InvalidOperationException("RevisionConflict");
            var stamp = Now(); Execute(db, tx, "INSERT INTO translation_versions(id,source_id,language,text,text_hash,origin,provenance_json,created_utc) VALUES($id,$s,$l,$t,$h,'manual',$p,$n)",
                ("$id", id), ("$s", source), ("$l", language), ("$t", text), ("$h", Hash(text)), ("$p", JsonSerializer.Serialize(new { command_hash = fp })), ("$n", stamp));
            Execute(db, tx, "UPDATE genre_terms SET control_revision=control_revision+1 WHERE id=$t", ("$t", term)); ContentStore.Select(db, tx, source, language, id);
            return new TranslationVersion(id, source, language, text, "manual", stamp);
        });
    }
    public void RestoreBuiltin(string original, string language, long expectedControl)
    {
        ContentStore.ValidateLanguage(language);
        if (!Builtin.Value.TryGetValue(original, out var values)) throw new InvalidOperationException("BuiltinTermUnavailable");
        SeedBuiltin();
        database.Write((db, tx) =>
        {
            var term = Hash("term\0" + original); var source = Hash("term-source\0" + original); var text = language == "en" ? values.English : values.Chinese;
            if (Scalar<long>(db, tx, "SELECT control_revision FROM genre_terms WHERE id=$t", ("$t", term)) != expectedControl) throw new InvalidOperationException("RevisionConflict");
            var id = Scalar<string>(db, tx, "SELECT id FROM translation_versions WHERE source_id=$s AND language=$l AND text=$t AND origin='import' ORDER BY created_utc LIMIT 1", ("$s", source), ("$l", language), ("$t", text));
            if (id is null)
            {
                id = Hash("builtin\0" + original + "\0" + language + "\0" + text);
                Execute(db, tx, "INSERT OR IGNORE INTO translation_versions(id,source_id,language,text,text_hash,origin,provenance_json,created_utc) VALUES($id,$s,$l,$t,$h,'import','{\"builtin\":true}',$n)",
                    ("$id", id), ("$s", source), ("$l", language), ("$t", text), ("$h", Hash(text)), ("$n", Now()));
            }
            Execute(db, tx, "UPDATE genre_terms SET control_revision=control_revision+1 WHERE id=$t", ("$t", term)); ContentStore.Select(db, tx, source, language, id); return true;
        });
    }
    public PersonRule[] People()
    {
        using var db = database.OpenRead(); using var cmd = Command(db, null, "SELECT id,original,zh_name,en_name,aliases_json,revision FROM person_name_rules ORDER BY original"); using var r = cmd.ExecuteReader();
        var values = new List<PersonRule>(); while (r.Read()) values.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), JsonSerializer.Deserialize<string[]>(r.GetString(4))!, r.GetInt64(5))); return [.. values];
    }
    public PersonPage People(string search, int offset, int limit)
    {
        ReadModel.Page(offset, limit); using var db = database.OpenRead(); using var tx = db.BeginTransaction(deferred: true);
        const string where = """
            $q='' OR instr(lower(original),lower($q))>0 OR instr(lower(ifnull(zh_name,'')),lower($q))>0
            OR instr(lower(ifnull(en_name,'')),lower($q))>0
            OR EXISTS(SELECT 1 FROM json_each(aliases_json) a WHERE instr(lower(a.value),lower($q))>0)
            """;
        var total = Scalar<long>(db, tx, "SELECT COUNT(*) FROM person_name_rules WHERE " + where, ("$q", search));
        using var cmd = Command(db, tx, "SELECT id,original,zh_name,en_name,aliases_json,revision FROM person_name_rules WHERE " + where + " ORDER BY original,id LIMIT $limit OFFSET $offset", ("$q", search), ("$limit", limit), ("$offset", offset));
        using var r = cmd.ExecuteReader(); var values = new List<PersonRule>();
        while (r.Read()) values.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), JsonSerializer.Deserialize<string[]>(r.GetString(4))!, r.GetInt64(5)));
        return new(total, [.. values], offset, limit);
    }
    public PersonRule SavePerson(string id, long expectedRevision, string original, string? chinese, string? english, string[] aliases)
    {
        if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(original) || original.Length > 200 || aliases.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200)
            || chinese is not null && (string.IsNullOrWhiteSpace(chinese) || chinese.Length > 200) || english is not null && (string.IsNullOrWhiteSpace(english) || english.Length > 200)) throw new ArgumentException("InvalidPersonRule");
        return database.Write((db, tx) =>
        {
            var revision = Scalar<long>(db, tx, "SELECT revision FROM person_name_rules WHERE id=$id", ("$id", id)); if (revision != expectedRevision) throw new InvalidOperationException("RevisionConflict");
            var next = revision + 1; var uniqueAliases = aliases.Distinct(StringComparer.Ordinal).ToArray();
            Execute(db, tx, "INSERT INTO person_name_rules VALUES($id,$o,$z,$e,$a,$r,'{}') ON CONFLICT(id) DO UPDATE SET original=excluded.original,zh_name=excluded.zh_name,en_name=excluded.en_name,aliases_json=excluded.aliases_json,revision=excluded.revision",
                ("$id", id), ("$o", original), ("$z", chinese), ("$e", english), ("$a", JsonSerializer.Serialize(uniqueAliases)), ("$r", next));
            return new PersonRule(id, original, chinese, english, uniqueAliases, next);
        }, wakeExecutor: false);
    }
    public void DeletePerson(string id, long expectedRevision) => database.Write((db, tx) =>
    {
        if (Execute(db, tx, "DELETE FROM person_name_rules WHERE id=$id AND revision=$r", ("$id", id), ("$r", expectedRevision)) != 1) throw new InvalidOperationException("RevisionConflict"); return true;
    }, wakeExecutor: false);
}
