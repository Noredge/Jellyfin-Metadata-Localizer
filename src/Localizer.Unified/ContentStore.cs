using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

public sealed record MediaIdentity(string ServerId, string LibraryId, string ItemId);
public sealed record FieldState(string MediaId, string Field, string SourceId, long SourceRevision, long ControlRevision,
    long RenderRevision, string Prefix, string Availability, string Content, string RenderJson);
public sealed record TranslationVersion(string Id, string SourceId, string Language, string Text, string Origin, string CreatedUtc);
public sealed record MediaDetailField(FieldState State, TranslationVersion? Selection, Dictionary<string, object?>? Intent);
public sealed record MediaDetailsRead(UnifiedSettings Settings, MediaDetailField[] Fields);

public sealed class ContentStore(UnifiedDatabase database)
{
    public UnifiedDatabase Database => database;
    public string RegisterMedia(MediaIdentity key, string path, string evidence) => database.Write((db, tx) =>
    {
        var id = Hash(JsonSerializer.Serialize(new[] { key.ServerId, key.LibraryId, key.ItemId }));
        Execute(db, tx, """
            INSERT INTO media_items(id,server_id,library_id,item_id,media_path,path_evidence) VALUES($id,$s,$l,$i,$p,$e)
            ON CONFLICT(server_id,library_id,item_id) DO UPDATE SET media_path=excluded.media_path,path_evidence=excluded.path_evidence,present=1
            """, ("$id", id), ("$s", key.ServerId), ("$l", key.LibraryId), ("$i", key.ItemId), ("$p", path), ("$e", evidence));
        return id;
    }, wakeExecutor: false);
    public void MarkRemoved(string mediaId) => database.Write((db, tx) =>
    {
        Execute(db, tx, "UPDATE media_items SET present=0 WHERE id=$m", ("$m", mediaId));
        Execute(db, tx, "UPDATE work_items SET state='expired',completion_reason='media_removed',lease_owner=NULL,lease_until_utc=NULL,updated_utc=$n WHERE media_id=$m AND state IN('queued','waiting','running','paused')",
            ("$m", mediaId), ("$n", Now()));
        Execute(db, tx, "UPDATE display_intents SET state='expired',error_code='media_removed',updated_utc=$n WHERE media_id=$m", ("$m", mediaId), ("$n", Now()));
        return true;
    });
    public bool RestorePresence(string mediaId, string verifiedPath) => database.Write((db, tx) =>
    {
        var path = Scalar<string>(db, tx, "SELECT media_path FROM media_items WHERE id=$m AND present=0", ("$m", mediaId));
        if (path is null || !string.Equals(path, verifiedPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        Execute(db, tx, "UPDATE media_items SET present=1 WHERE id=$m", ("$m", mediaId));
        // The returning identity retains all evidence and selections; previous execution authority stays revoked.
        Execute(db, tx, "UPDATE media_fields SET control_revision=control_revision+1 WHERE media_id=$m", ("$m", mediaId));
        return true;
    });
    // Caller supplies verified original text/evidence, never the host's translated display value.
    public FieldState Observe(string mediaId, string field, string original, string evidenceJson,
        string prefix = "", string? observedDisplay = null)
    {
        ValidateField(field);
        var available = string.IsNullOrWhiteSpace(original) ? "empty" : "available";
        string content = original.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var render = "{}";
        if (field == "Genres")
        {
            var order = (JsonSerializer.Deserialize<string[]>(original) ?? []).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).ToArray();
            render = JsonSerializer.Serialize(order);
            content = JsonSerializer.Serialize(order.Order(StringComparer.Ordinal).ToArray());
            available = order.Length == 0 ? "empty" : "available";
        }
        if (available == "empty" && field != "Genres") content = "";
        using (var db = database.OpenRead())
        {
            var prior = ReadField(db, null, mediaId, field);
            var sameContent = prior is not null && (field == "Genres"
                ? JsonSerializer.Deserialize<string[]>(prior.Content)!.SequenceEqual(JsonSerializer.Deserialize<string[]>(content)!, StringComparer.Ordinal) : prior.Content == content);
            var sameRender = prior is not null && (field == "Genres"
                ? JsonSerializer.Deserialize<string[]>(prior.RenderJson)!.SequenceEqual(JsonSerializer.Deserialize<string[]>(render)!, StringComparer.Ordinal) : prior.RenderJson == render);
            if (sameContent && sameRender && prior!.Prefix == prefix && prior.Availability == available
                && Scalar<long>(db, null, "SELECT COUNT(*) FROM media_fields WHERE media_id=$m AND field=$f AND observation_error IS NULL AND observed_display_hash IS $h",
                    ("$m", mediaId), ("$f", field), ("$h", observedDisplay is null ? null : Hash(observedDisplay))) == 1) return prior;
        }
        return database.Write((db, tx) =>
        {
            var hash = field == "Genres" ? GenreFingerprint(JsonSerializer.Deserialize<string[]>(content)!) : Hash(content);
            var source = Scalar<string>(db, tx, "SELECT id FROM source_versions WHERE owner_id=$m AND kind=$f AND content_hash=$h",
                ("$m", mediaId), ("$f", field), ("$h", hash));
            if (source is null)
            {
                source = Id();
                Execute(db, tx, "INSERT INTO source_versions(id,kind,owner_id,content,content_hash,evidence_json,created_utc) VALUES($id,$f,$m,$c,$h,$e,$n)",
                    ("$id", source), ("$f", field), ("$m", mediaId), ("$c", content), ("$h", hash), ("$e", evidenceJson), ("$n", Now()));
            }
            else
            {
                var prior = Scalar<string>(db, tx, "SELECT content FROM source_versions WHERE id=$id", ("$id", source));
                if (field == "Genres" ? !JsonSerializer.Deserialize<string[]>(prior)!.SequenceEqual(JsonSerializer.Deserialize<string[]>(content)!, StringComparer.Ordinal) : prior != content)
                    throw new InvalidOperationException("SourceFingerprintCollision");
            }
            var old = ReadField(db, tx, mediaId, field);
            var contentChanged = old is null || old.SourceId != source;
            var renderChanged = old is null || old.Prefix != prefix || (field == "Genres"
                ? !JsonSerializer.Deserialize<string[]>(old.RenderJson)!.SequenceEqual(JsonSerializer.Deserialize<string[]>(render)!, StringComparer.Ordinal)
                : old.RenderJson != render);
            Execute(db, tx, """
                INSERT INTO media_fields(media_id,field,source_id,source_revision,control_revision,render_revision,display_prefix,render_json,availability,observed_utc,observed_display_hash)
                VALUES($m,$f,$s,1,0,1,$p,$r,$a,$n,$d)
                ON CONFLICT(media_id,field) DO UPDATE SET source_id=excluded.source_id,
                  source_revision=media_fields.source_revision+$changed,render_revision=media_fields.render_revision+$render,
                  display_prefix=excluded.display_prefix,render_json=excluded.render_json,availability=excluded.availability,
                  observed_utc=excluded.observed_utc,observed_display_hash=excluded.observed_display_hash,observation_error=NULL
                """, ("$m", mediaId), ("$f", field), ("$s", source), ("$p", prefix), ("$r", render), ("$a", available),
                ("$n", Now()), ("$d", observedDisplay is null ? null : Hash(observedDisplay)),
                ("$changed", contentChanged || renderChanged ? 1 : 0), ("$render", renderChanged ? 1 : 0));
            if (field == "Genres") EnsureTerms(db, tx, source, JsonSerializer.Deserialize<string[]>(content)!);
            if (old is not null && (contentChanged || renderChanged))
                Execute(db, tx, "UPDATE work_items SET state='expired',completion_reason='source_changed',lease_owner=NULL,lease_until_utc=NULL,updated_utc=$n WHERE media_id=$m AND field=$f AND state IN('queued','running','waiting','paused')",
                    ("$m", mediaId), ("$f", field), ("$n", Now()));
            return ReadField(db, tx, mediaId, field)!;
        });
    }
    public FieldState? ReadField(string mediaId, string field)
    { using var db = database.OpenRead(); return ReadField(db, null, mediaId, field); }
    public MediaDetailsRead ReadDetails(string mediaId)
    {
        using var timing = Localizer.Core.OperationTimings.Measure("detail_data");
        using var db = database.OpenRead(); using var tx = db.BeginTransaction(deferred: true);
        var settings = SettingsStore.Read(db, tx); var fields = new List<MediaDetailField>();
        using var cmd = Command(db, tx, """
            SELECT f.field,f.source_id,f.source_revision,f.control_revision,f.render_revision,f.display_prefix,f.availability,s.content,f.render_json,
              t.id,t.source_id,t.language,t.text,t.origin,t.created_utc,d.mode,d.state,d.keep_original,d.error_code,d.translation_id
            FROM media_fields f JOIN source_versions s ON s.id=f.source_id
            LEFT JOIN translation_selections selected ON selected.source_id=f.source_id AND selected.language=$language
            LEFT JOIN translation_versions t ON t.id=selected.translation_id
            LEFT JOIN display_intents d ON d.media_id=f.media_id AND d.field=f.field
            WHERE f.media_id=$id ORDER BY f.field
            """, ("$id", mediaId), ("$language", settings.Language));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var field = new FieldState(mediaId,r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetInt64(3),r.GetInt64(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8));
            var selection = r.IsDBNull(9) ? null : new TranslationVersion(r.GetString(9),r.GetString(10),r.GetString(11),r.GetString(12),r.GetString(13),r.GetString(14));
            Dictionary<string, object?>? intent = null;
            if (!r.IsDBNull(15)) { intent = new(); for (var i=15;i<20;i++) intent[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i); }
            fields.Add(new(field, selection, intent));
        }
        return new(settings, fields.ToArray());
    }
    public void ObserveUnavailable(string mediaId, string field, string reason) => database.Write((db, tx) =>
    {
        ValidateField(field);
        Execute(db, tx, """
            INSERT INTO media_fields(media_id,field,availability,observed_utc,observation_error) VALUES($m,$f,'unreadable',$n,$e)
            ON CONFLICT(media_id,field) DO UPDATE SET availability='unreadable',observed_utc=excluded.observed_utc,observation_error=excluded.observation_error
            """, ("$m", mediaId), ("$f", field), ("$n", Now()), ("$e", reason));
        return true;
    });
    public FieldState ProtectExternal(FieldState expected, string actualDisplay) => database.Write((db, tx) =>
    {
        var current = RequireCurrent(db, tx, expected);
        if (Scalar<long>(db, tx, "SELECT COUNT(*) FROM display_intents WHERE media_id=$m AND field=$f AND mode='external' AND expected_value=$v AND source_revision=$sr AND control_revision=$cr",
            ("$m", current.MediaId), ("$f", current.Field), ("$v", actualDisplay), ("$sr", current.SourceRevision), ("$cr", current.ControlRevision)) == 1) return current;
        AdvanceControl(db, tx, current);
        Execute(db, tx, """
            INSERT INTO display_intents(id,media_id,field,revision,mode,source_id,source_revision,control_revision,render_revision,target_epoch,language,expected_value,state,updated_utc)
            VALUES($id,$m,$f,1,'external',$s,$sr,$cr,$rr,(SELECT target_epoch FROM settings),(SELECT target_language FROM settings),$v,'protected',$n)
            ON CONFLICT(media_id,field) DO UPDATE SET revision=display_intents.revision+1,mode='external',source_id=excluded.source_id,source_revision=excluded.source_revision,
              control_revision=excluded.control_revision,render_revision=excluded.render_revision,target_epoch=excluded.target_epoch,language=excluded.language,
              translation_id=NULL,desired_value=NULL,expected_value=excluded.expected_value,state='protected',error_code=NULL,updated_utc=excluded.updated_utc
            """, ("$id", Id()), ("$m", current.MediaId), ("$f", current.Field), ("$s", current.SourceId), ("$sr", current.SourceRevision), ("$cr", current.ControlRevision + 1),
            ("$rr", current.RenderRevision), ("$v", actualDisplay), ("$n", Now()));
        return ReadField(db, tx, current.MediaId, current.Field)!;
    });
    internal FieldState AllowOriginalReset(FieldState expected, string actualDisplay) => database.Write((db, tx) =>
    {
        var current = RequireCurrent(db, tx, expected);
        if (Scalar<long>(db, tx, "SELECT COUNT(*) FROM display_intents WHERE media_id=$m AND field=$f AND mode='external'", ("$m", current.MediaId), ("$f", current.Field)) == 0) return current;
        var keep = Scalar<long>(db, tx, "SELECT keep_original FROM display_intents WHERE media_id=$m AND field=$f", ("$m", current.MediaId), ("$f", current.Field)) == 1;
        AdvanceControl(db, tx, current); current = current with { ControlRevision = current.ControlRevision + 1 };
        SetIntent(db, tx, current, Scalar<string>(db, tx, "SELECT target_language FROM settings"), null, "original", UnifiedExecutor.OriginalValue(current), actualDisplay, keep);
        Execute(db, tx, "UPDATE display_intents SET state='applied' WHERE media_id=$m AND field=$f", ("$m", current.MediaId), ("$f", current.Field)); return current;
    });
    internal static FieldState? ReadField(SqliteConnection db, SqliteTransaction? tx, string mediaId, string field)
    {
        using var cmd = Command(db, tx, """
            SELECT f.source_id,f.source_revision,f.control_revision,f.render_revision,f.display_prefix,f.availability,s.content,f.render_json
            FROM media_fields f JOIN source_versions s ON s.id=f.source_id WHERE f.media_id=$m AND f.field=$f
            """, ("$m", mediaId), ("$f", field));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(mediaId, field, r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3),
            r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)) : null;
    }
    internal static void EnsureTerms(SqliteConnection db, SqliteTransaction tx, string sourceId, string[] terms)
    {
        for (var i = 0; i < terms.Length; i++)
        {
            var termId = Hash("term\0" + terms[i]);
            var termSource = Hash("term-source\0" + terms[i]);
            Execute(db, tx, "INSERT OR IGNORE INTO source_versions(id,kind,owner_id,content,content_hash,created_utc) VALUES($s,'Term',$t,$c,$h,$n)",
                ("$s", termSource), ("$t", termId), ("$c", terms[i]), ("$h", Hash(terms[i])), ("$n", Now()));
            Execute(db, tx, "INSERT OR IGNORE INTO genre_terms(id,original,source_id) VALUES($t,$c,$s)", ("$t", termId), ("$c", terms[i]), ("$s", termSource));
            Execute(db, tx, "INSERT OR IGNORE INTO genre_members(source_id,term_id,ordinal) VALUES($s,$t,$o)",
                ("$s", sourceId), ("$t", termId), ("$o", i));
        }
    }
    public static string GenreFingerprint(IEnumerable<string> values) => Hash("genres/v1\n" + string.Concat(values.Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal).Select(x => System.Text.Encoding.UTF8.GetByteCount(x).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + x)));
    public TranslationVersion? Selected(string sourceId, string language)
    {
        ValidateLanguage(language);
        using var db = database.OpenRead();
        using var cmd = Command(db, null, """
            SELECT t.id,t.source_id,t.language,t.text,t.origin,t.created_utc FROM translation_selections s
            JOIN translation_versions t ON t.id=s.translation_id WHERE s.source_id=$s AND s.language=$l
            """, ("$s", sourceId), ("$l", language));
        using var r = cmd.ExecuteReader(); return r.Read() ? Translation(r) : null;
    }
    public TranslationVersion[] History(string sourceId, string language, int offset, int limit)
    {
        ValidateLanguage(language); if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = database.OpenRead(); using var cmd = Command(db, null, """
            SELECT id,source_id,language,text,origin,created_utc FROM translation_versions
            WHERE source_id=$s AND language=$l ORDER BY created_utc DESC,id DESC LIMIT $limit OFFSET $offset
            """, ("$s", sourceId), ("$l", language), ("$limit", limit), ("$offset", offset));
        using var r = cmd.ExecuteReader(); var values = new List<TranslationVersion>(); while (r.Read()) values.Add(Translation(r)); return [.. values];
    }
    public TranslationVersion SaveManual(FieldState expected, string language, string text, bool show, string expectedDisplay, string commandId)
    {
        ValidateLanguage(language); ArgumentException.ThrowIfNullOrWhiteSpace(text); ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        if (expected.Field == "Genres" && (JsonSerializer.Deserialize<string[]>(text) is not { } genres || genres.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("InvalidGenreValues");
        return database.Write((db, tx) =>
        {
            var id = Hash("manual\0" + commandId);
            var commandHash = Hash(JsonSerializer.Serialize(new { expected, language, text, show, expectedDisplay }));
            using (var old = Command(db, tx, "SELECT id,source_id,language,text,origin,created_utc,provenance_json FROM translation_versions WHERE id=$id", ("$id", id)))
            using (var r = old.ExecuteReader())
                if (r.Read())
                {
                    var value = Translation(r);
                    using var provenance = JsonDocument.Parse(r.GetString(6));
                    if (value.SourceId != expected.SourceId || value.Language != language || value.Text != text || value.Origin != "manual"
                        || provenance.RootElement.GetProperty("command_hash").GetString() != commandHash)
                        throw new InvalidOperationException("SubmissionConflict");
                    return value;
                }
            var current = RequireCurrent(db, tx, expected);
            var stamp = Now();
            Execute(db, tx, "INSERT INTO translation_versions(id,source_id,language,text,text_hash,origin,provenance_json,created_utc) VALUES($id,$s,$l,$t,$h,'manual',$p,$n)",
                ("$id", id), ("$s", current.SourceId), ("$l", language), ("$t", text), ("$h", Hash(text)),
                ("$p", JsonSerializer.Serialize(new { command_hash = commandHash })), ("$n", stamp));
            AdvanceControl(db, tx, current);
            if (show)
            {
                Select(db, tx, current.SourceId, language, id);
                SetIntent(db, tx, current with { ControlRevision = current.ControlRevision + 1 }, language, id, "translated", text, expectedDisplay);
            }
            return new(id, current.SourceId, language, text, "manual", stamp);
        });
    }
    public void Choose(FieldState expected, string language, string translationId, string expectedDisplay) => database.Write((db, tx) =>
    {
        ValidateLanguage(language); var current = RequireCurrent(db, tx, expected);
        using var cmd = Command(db, tx, "SELECT text FROM translation_versions WHERE id=$id AND source_id=$s AND language=$l",
            ("$id", translationId), ("$s", current.SourceId), ("$l", language));
        var text = cmd.ExecuteScalar() as string ?? throw new InvalidOperationException("TranslationSourceMismatch");
        AdvanceControl(db, tx, current); Select(db, tx, current.SourceId, language, translationId);
        SetIntent(db, tx, current with { ControlRevision = current.ControlRevision + 1 }, language, translationId, "translated", text, expectedDisplay);
        return true;
    });
    public void KeepOriginal(FieldState expected, string expectedDisplay) => database.Write((db, tx) =>
    {
        var current = RequireCurrent(db, tx, expected); AdvanceControl(db, tx, current);
        var language = Scalar<string>(db, tx, "SELECT target_language FROM settings WHERE singleton=1");
        var value = current.Field == "Name" ? current.Prefix + current.Content : current.Field == "Genres" ? current.RenderJson : current.Content;
        SetIntent(db, tx, current with { ControlRevision = current.ControlRevision + 1 }, language, null, "original", value, expectedDisplay, keepOriginal: true);
        return true;
    });
    public void RestoreOriginal(FieldState expected, string expectedDisplay) => database.Write((db, tx) =>
    {
        var current = RequireCurrent(db, tx, expected); AdvanceControl(db, tx, current);
        var language = Scalar<string>(db, tx, "SELECT target_language FROM settings WHERE singleton=1");
        var value = current.Field == "Name" ? current.Prefix + current.Content : current.Field == "Genres" ? current.RenderJson : current.Content;
        var keep = Scalar<long>(db, tx, "SELECT keep_original FROM display_intents WHERE media_id=$m AND field=$f", ("$m", current.MediaId), ("$f", current.Field)) == 1;
        SetIntent(db, tx, current with { ControlRevision = current.ControlRevision + 1 }, language, null, "original", value, expectedDisplay, keep);
        return true;
    });
    public void FollowGlobal(FieldState expected) => database.Write((db, tx) =>
    {
        var current = RequireCurrent(db, tx, expected); AdvanceControl(db, tx, current);
        Execute(db, tx, "UPDATE display_intents SET keep_original=0 WHERE media_id=$m AND field=$f", ("$m", current.MediaId), ("$f", current.Field)); return true;
    });
    internal static FieldState RequireCurrent(SqliteConnection db, SqliteTransaction tx, FieldState expected)
    {
        var current = ReadField(db, tx, expected.MediaId, expected.Field) ?? throw new InvalidOperationException("SourceUnavailable");
        if (current.SourceId != expected.SourceId || current.SourceRevision != expected.SourceRevision || current.ControlRevision != expected.ControlRevision
            || current.RenderRevision != expected.RenderRevision) throw new InvalidOperationException("RevisionConflict");
        return current;
    }
    internal static void AdvanceControl(SqliteConnection db, SqliteTransaction tx, FieldState field)
    {
        Execute(db, tx, "UPDATE media_fields SET control_revision=control_revision+1 WHERE media_id=$m AND field=$f", ("$m", field.MediaId), ("$f", field.Field));
        Execute(db, tx, "UPDATE display_intents SET state='expired',error_code='manual_control_changed',updated_utc=$n WHERE media_id=$m AND field=$f",
            ("$m", field.MediaId), ("$f", field.Field), ("$n", Now()));
    }
    internal static void Select(SqliteConnection db, SqliteTransaction tx, string sourceId, string language, string translationId) =>
        Execute(db, tx, """
            INSERT INTO translation_selections(source_id,language,translation_id,revision) VALUES($s,$l,$t,1)
            ON CONFLICT(source_id,language) DO UPDATE SET translation_id=excluded.translation_id,revision=translation_selections.revision+1
            """, ("$s", sourceId), ("$l", language), ("$t", translationId));
    internal static void SetIntent(SqliteConnection db, SqliteTransaction tx, FieldState field, string language, string? translationId,
        string mode, string text, string expectedDisplay, bool keepOriginal = false)
    {
        var target = Scalar<string>(db, tx, "SELECT target_language FROM settings WHERE singleton=1");
        if (target != language) throw new InvalidOperationException("TargetLanguageChanged");
        var rendered = mode == "translated" && field.Field == "Name" ? field.Prefix + text : text;
        Execute(db, tx, """
            INSERT INTO display_intents(id,media_id,field,revision,mode,keep_original,source_id,source_revision,control_revision,render_revision,
              target_epoch,language,translation_id,desired_value,expected_value,state,updated_utc)
            VALUES($id,$m,$f,1,$mode,$keep,$s,$sr,$cr,$rr,(SELECT target_epoch FROM settings WHERE singleton=1),$l,$t,$v,$e,'pending',$n)
            ON CONFLICT(media_id,field) DO UPDATE SET revision=display_intents.revision+1,mode=excluded.mode,keep_original=excluded.keep_original,source_id=excluded.source_id,
              source_revision=excluded.source_revision,control_revision=excluded.control_revision,render_revision=excluded.render_revision,
              target_epoch=excluded.target_epoch,language=excluded.language,translation_id=excluded.translation_id,
              desired_value=excluded.desired_value,expected_value=excluded.expected_value,state='pending',error_code=NULL,updated_utc=excluded.updated_utc
            """, ("$id", Id()), ("$m", field.MediaId), ("$f", field.Field), ("$mode", mode), ("$s", field.SourceId),
            ("$sr", field.SourceRevision), ("$cr", field.ControlRevision), ("$rr", field.RenderRevision), ("$l", language),
            ("$t", translationId), ("$v", rendered), ("$e", expectedDisplay), ("$keep", keepOriginal), ("$n", Now()));
    }
    internal static TranslationVersion Translation(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5));
    internal static void ValidateField(string field)
    { if (field is not ("Name" or "Overview" or "Genres")) throw new ArgumentException("InvalidField"); }
    internal static void ValidateLanguage(string language)
    { if (language is not ("zh-Hans" or "en")) throw new ArgumentException("InvalidLanguage"); }
}
