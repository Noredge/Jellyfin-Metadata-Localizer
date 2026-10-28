using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

public sealed record WorkSeed(FieldState Field, string ExpectedDisplay);
public sealed record BatchReceipt(string Id, string Language, long TargetEpoch, string[] WorkIds, bool Cancelled);
public sealed record UnifiedWork(string Id, string Operation, string ExpectedDisplay, string TargetKind, string? MediaId, string Field,
    string? TermId, string SourceId, long SourceRevision, long ControlRevision, long RenderRevision, string Language, long TargetEpoch,
    string? InputId, string? TranslationId, string GenerationKey, bool ForceGeneration, bool AutonomousDemand,
    string State, string Step, string? WaitReason, string? IssueKind, string? Error, int Attempts);

public sealed class WorkStore(UnifiedDatabase database, Func<DateTimeOffset>? utcNow = null)
{
    public UnifiedDatabase Database => database;
    public DateTimeOffset UtcNow => (utcNow?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime();
    public string Stamp() => UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    internal const string Columns = "id,operation,expected_display,target_kind,media_id,field,term_id,source_id,source_revision,control_revision,render_revision,language,target_epoch,input_id,translation_id,generation_key,force_generation,autonomous_demand,state,step,wait_reason,issue_kind,error_code,attempt_count";
    private static string? Nullable(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    internal static UnifiedWork Read(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Nullable(r, 4), r.GetString(5),
        Nullable(r, 6), r.GetString(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetString(11), r.GetInt64(12), Nullable(r, 13), Nullable(r, 14),
        r.GetString(15), r.GetBoolean(16), r.GetBoolean(17), r.GetString(18), r.GetString(19), Nullable(r, 20), Nullable(r, 21), Nullable(r, 22), r.GetInt32(23));
    public UnifiedWork? Find(string id) { using var db = database.Open(); return Find(db, null, id); }
    internal static UnifiedWork? Find(SqliteConnection db, SqliteTransaction? tx, string id)
    { using var cmd = Command(db, tx, "SELECT " + Columns + " FROM work_items WHERE id=$id", ("$id", id)); using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null; }
    public UnifiedWork[] List(string? batchId = null, int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = database.Open(); using var cmd = Command(db, null, "SELECT " + Columns + " FROM work_items WHERE ($b IS NULL OR id IN(SELECT work_id FROM batch_items WHERE batch_id=$b)) ORDER BY created_utc,id LIMIT $limit OFFSET $offset",
            ("$b", batchId), ("$limit", limit), ("$offset", offset)); using var r = cmd.ExecuteReader(); var values = new List<UnifiedWork>(); while (r.Read()) values.Add(Read(r)); return [.. values];
    }
    public BatchReceipt Submit(string commandId, string kind, long settingsRevision, WorkSeed[] seeds, bool force = false, string operation = "sync")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        if (operation is not ("sync" or "display" or "restore") || kind is not ("fill" or "language" or "retranslate" or "manual" or "dictionary")) throw new ArgumentException("InvalidWorkIntent");
        var fingerprint = SubmissionFingerprint(kind, settingsRevision, seeds.Select(x => (x.Field.MediaId, x.Field.Field)), force, operation);
        return database.Write((db, tx) =>
        {
            var id = Hash("batch\0" + commandId);
            var old = Scalar<string>(db, tx, "SELECT request_fingerprint FROM batches WHERE id=$id", ("$id", id));
            if (old is not null)
            {
                if (old != fingerprint) throw new InvalidOperationException("SubmissionConflict");
                return Batch(db, tx, id);
            }
            var settings = SettingsStore.Read(db, tx);
            if (settings.Revision != settingsRevision) throw new InvalidOperationException("RevisionConflict");
            Execute(db, tx, "INSERT INTO batches(id,submission_key,request_fingerprint,kind,language,target_epoch,created_utc) VALUES($id,$key,$fp,$k,$l,$e,$n)",
                ("$id", id), ("$key", commandId), ("$fp", fingerprint), ("$k", kind), ("$l", settings.Language), ("$e", settings.TargetEpoch), ("$n", Stamp()));
            foreach (var seed in seeds.DistinctBy(x => (x.Field.MediaId, x.Field.Field)))
            {
                var f = ContentStore.RequireCurrent(db, tx, seed.Field);
                if (!InScope(db, tx, f.MediaId, f.Field, settings) || f.Availability == "empty" && operation == "sync") continue;
                if (kind == "fill" && !force && operation == "sync" && !FillPlanner.CanFill(db, tx, f, settings)) continue;
                if (force)
                {
                    ContentStore.AdvanceControl(db, tx, f);
                    Execute(db, tx, "UPDATE display_intents SET keep_original=0 WHERE media_id=$m AND field=$f", ("$m", f.MediaId), ("$f", f.Field));
                    f = f with { ControlRevision = f.ControlRevision + 1 };
                }
                var generation = force ? "force:" + commandId : operation == "sync" ? "reuse" : operation + ":" + f.ControlRevision;
                var work = EnqueueMedia(db, tx, f, seed.ExpectedDisplay, settings, generation, force, false, operation, Hash(id + "\0" + f.MediaId + "\0" + f.Field));
                Execute(db, tx, "INSERT OR IGNORE INTO batch_items VALUES($b,$w,1)", ("$b", id), ("$w", work));
            }
            return Batch(db, tx, id);
        });
    }
    private static string SubmissionFingerprint(string kind, long settingsRevision, IEnumerable<(string MediaId, string Field)> targets, bool force, string operation) =>
        Hash(JsonSerializer.Serialize(new { kind, settingsRevision, force, operation,
            targets = targets.Select(x => new { x.MediaId, x.Field }).OrderBy(x => x.MediaId, StringComparer.Ordinal).ThenBy(x => x.Field, StringComparer.Ordinal) }));
    public BatchReceipt? TryReplay(string commandId, string kind, long settingsRevision, (string MediaId, string Field)[] targets, bool force = false, string operation = "sync")
    {
        using var db = database.Open(); var id = Hash("batch\0" + commandId);
        var fingerprint = Scalar<string>(db, null, "SELECT request_fingerprint FROM batches WHERE id=$id", ("$id", id));
        if (fingerprint is null) return null;
        if (fingerprint != SubmissionFingerprint(kind, settingsRevision, targets, force, operation)) throw new InvalidOperationException("SubmissionConflict");
        return Batch(db, null, id);
    }
    public BatchReceipt SubmitTerms(string commandId, string kind, long settingsRevision, string[] originals, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        if (kind is not ("dictionary" or "connection_test") || originals.Length is < 1 or > 200
            || originals.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 500 || x.Any(char.IsControl))) throw new ArgumentException("InvalidTerms");
        originals = originals.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = Hash(JsonSerializer.Serialize(new { kind, settingsRevision, originals, force }));
        return database.Write((db, tx) =>
        {
            var id = Hash("batch\0" + commandId);
            var prior = Scalar<string>(db, tx, "SELECT request_fingerprint FROM batches WHERE id=$id", ("$id", id));
            if (prior is not null)
            {
                if (prior != fingerprint) throw new InvalidOperationException("SubmissionConflict");
                return Batch(db, tx, id);
            }
            var settings = SettingsStore.Read(db, tx);
            if (settings.Revision != settingsRevision) throw new InvalidOperationException("RevisionConflict");
            Execute(db, tx, "INSERT INTO batches(id,submission_key,request_fingerprint,kind,language,target_epoch,created_utc) VALUES($id,$key,$fp,$k,$l,$e,$n)",
                ("$id", id), ("$key", commandId), ("$fp", fingerprint), ("$k", kind), ("$l", settings.Language), ("$e", settings.TargetEpoch), ("$n", Stamp()));
            foreach (var original in originals)
            {
                var term = Hash("term\0" + original); var source = Hash("term-source\0" + original);
                Execute(db, tx, "INSERT OR IGNORE INTO source_versions(id,kind,owner_id,content,content_hash,created_utc) VALUES($s,'Term',$t,$o,$h,$n)",
                    ("$s", source), ("$t", term), ("$o", original), ("$h", Hash(original)), ("$n", Stamp()));
                Execute(db, tx, "INSERT OR IGNORE INTO genre_terms(id,original,source_id) VALUES($t,$o,$s)", ("$t", term), ("$o", original), ("$s", source));
                if (force)
                {
                    Execute(db, tx, "UPDATE genre_terms SET control_revision=control_revision+1 WHERE id=$t", ("$t", term));
                    Execute(db, tx, "UPDATE work_items SET state='expired',completion_reason='term_generation_replaced',updated_utc=$n WHERE term_id=$t AND state IN('queued','running','waiting','paused')", ("$t", term), ("$n", Stamp()));
                }
                var control = Scalar<long>(db, tx, "SELECT control_revision FROM genre_terms WHERE id=$t", ("$t", term));
                var generation = force ? "force:" + commandId : "reuse";
                var workId = Scalar<string>(db, tx, """
                    SELECT id FROM work_items WHERE target_kind='term' AND term_id=$t AND source_id=$s AND control_revision=$cr
                    AND language=$l AND target_epoch=$e AND generation_key=$g AND state IN('queued','running','waiting','paused') LIMIT 1
                    """, ("$t", term), ("$s", source), ("$cr", control), ("$l", settings.Language), ("$e", settings.TargetEpoch), ("$g", generation));
                if (workId is null)
                {
                    workId = Id();
                    Execute(db, tx, """
                        INSERT INTO work_items(id,submission_key,target_kind,field,term_id,source_id,source_revision,control_revision,language,target_epoch,
                          generation_key,force_generation,state,step,created_utc,updated_utc)
                        VALUES($id,$key,'term','Term',$t,$s,1,$cr,$l,$e,$g,$force,'queued','source',$n,$n)
                        """, ("$id", workId), ("$key", Hash(id + "\0" + term)), ("$t", term), ("$s", source), ("$cr", control),
                        ("$l", settings.Language), ("$e", settings.TargetEpoch), ("$g", generation), ("$force", force), ("$n", Stamp()));
                }
                Execute(db, tx, "INSERT INTO batch_items VALUES($b,$w,1)", ("$b", id), ("$w", workId));
            }
            return Batch(db, tx, id);
        });
    }
    public string? EnqueueAutomatic(string kind, WorkSeed seed, bool deferUntilBaseline = false, string? observationId = null)
    {
        if (kind is not ("added" or "nfo")) throw new ArgumentException("InvalidAutomaticIntent");
        return database.Write((db, tx) =>
        {
            var s = SettingsStore.Read(db, tx); var f = ContentStore.RequireCurrent(db, tx, seed.Field);
            if (!s.AutoEnabled || !s.BaselineEstablished && !deferUntilBaseline || !InScope(db, tx, f.MediaId, f.Field, s) || f.Availability == "empty") return null;
            var intent = Scalar<long>(db, tx, "SELECT revision FROM display_intents WHERE media_id=$m AND field=$f", ("$m", f.MediaId), ("$f", f.Field));
            var key = Hash(JsonSerializer.Serialize(new { kind, f.MediaId, f.Field, f.SourceRevision, f.ControlRevision, f.RenderRevision, s.TargetEpoch, seed.ExpectedDisplay, intent }));
            var workId = EnqueueMedia(db, tx, f, seed.ExpectedDisplay, s, "reuse", false, true, "sync", key);
            var submission = "automatic/" + (observationId ?? key) + "/" + kind + "/" + s.TargetEpoch;
            var batch = Hash("batch\0" + submission);
            Execute(db, tx, "INSERT OR IGNORE INTO batches(id,submission_key,request_fingerprint,kind,language,target_epoch,created_utc) VALUES($id,$key,$fp,$k,$l,$e,$n)",
                ("$id", batch), ("$key", submission), ("$fp", Hash(submission)), ("$k", kind), ("$l", s.Language), ("$e", s.TargetEpoch), ("$n", Stamp()));
            Execute(db, tx, "INSERT OR IGNORE INTO batch_items VALUES($b,$w,1)", ("$b", batch), ("$w", workId));
            return workId;
        });
    }
    private string EnqueueMedia(SqliteConnection db, SqliteTransaction tx, FieldState f, string expected, UnifiedSettings settings,
        string generation, bool force, bool automatic, string operation, string submission)
    {
        var replay = Scalar<string>(db, tx, "SELECT id FROM work_items WHERE submission_key=$k OR submission_key LIKE ($k||'/resume/%') ORDER BY created_utc DESC,rowid DESC LIMIT 1", ("$k", submission));
        if (replay is not null)
        {
            var reason = Scalar<string>(db, tx, "SELECT completion_reason FROM work_items WHERE id=$id", ("$id", replay));
            if (reason != "history_only") return replay;
            submission += "/resume/" + Id();
        }
        var active = Scalar<string>(db, tx, """
            SELECT id FROM work_items WHERE media_id=$m AND field=$f AND source_id=$s AND source_revision=$sr AND control_revision=$cr AND render_revision=$rr
            AND language=$l AND target_epoch=$e AND generation_key=$g AND state IN('queued','running','waiting','paused')
            """, ("$m", f.MediaId), ("$f", f.Field), ("$s", f.SourceId), ("$sr", f.SourceRevision), ("$cr", f.ControlRevision), ("$rr", f.RenderRevision),
            ("$l", settings.Language), ("$e", settings.TargetEpoch), ("$g", generation));
        if (active is not null)
        {
            if (automatic) Execute(db, tx, "UPDATE work_items SET autonomous_demand=1 WHERE id=$id", ("$id", active));
            return active;
        }
        var id = Id();
        Execute(db, tx, """
            INSERT INTO work_items(id,submission_key,operation,expected_display,target_kind,media_id,field,source_id,source_revision,control_revision,render_revision,
              language,target_epoch,generation_key,force_generation,autonomous_demand,state,step,created_utc,updated_utc)
            VALUES($id,$key,$op,$display,'media',$m,$f,$s,$sr,$cr,$rr,$l,$e,$g,$force,$auto,'queued','source',$n,$n)
            """, ("$id", id), ("$key", submission), ("$op", operation), ("$display", expected), ("$m", f.MediaId), ("$f", f.Field), ("$s", f.SourceId),
            ("$sr", f.SourceRevision), ("$cr", f.ControlRevision), ("$rr", f.RenderRevision), ("$l", settings.Language), ("$e", settings.TargetEpoch),
            ("$g", generation), ("$force", force), ("$auto", automatic), ("$n", Stamp()));
        return id;
    }
    private static BatchReceipt Batch(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        string language; long epoch; bool cancelled;
        using (var cmd = Command(db, tx, "SELECT language,target_epoch,cancelled FROM batches WHERE id=$id", ("$id", id)))
        using (var r = cmd.ExecuteReader()) { if (!r.Read()) throw new InvalidOperationException("BatchMissing"); language = r.GetString(0); epoch = r.GetInt64(1); cancelled = r.GetBoolean(2); }
        using var items = Command(db, tx, "SELECT work_id FROM batch_items WHERE batch_id=$b ORDER BY rowid", ("$b", id)); using var reader = items.ExecuteReader();
        var ids = new List<string>(); while (reader.Read()) ids.Add(reader.GetString(0)); return new(id, language, epoch, [.. ids], cancelled);
    }
    public void CancelBatch(string id) => database.Write((db, tx) =>
    {
        Execute(db, tx, "UPDATE batches SET cancelled=1 WHERE id=$id", ("$id", id));
        Execute(db, tx, "UPDATE batch_items SET active=0 WHERE batch_id=$id", ("$id", id));
        Execute(db, tx, """
            UPDATE work_items SET autonomous_demand=0 WHERE id IN(SELECT bi.work_id FROM batch_items bi JOIN batches b ON b.id=bi.batch_id WHERE b.id=$id AND b.kind IN('added','nfo'))
            AND NOT EXISTS(SELECT 1 FROM batch_items bi JOIN batches b ON b.id=bi.batch_id WHERE bi.work_id=work_items.id AND bi.active=1 AND b.cancelled=0 AND b.kind IN('added','nfo'))
            """, ("$id", id));
        CancelUnused(db, tx); return true;
    });
    internal static bool InScope(SqliteConnection db, SqliteTransaction? tx, string mediaId, string field, UnifiedSettings settings) => settings.Fields.Includes(field) &&
        Scalar<long>(db, tx, "SELECT COUNT(*) FROM media_items m JOIN managed_libraries l ON l.library_id=m.library_id WHERE m.id=$m AND m.present=1", ("$m", mediaId)) == 1;
    // The queue and issue read model share this authority test. Only media parents
    // supply inherited term demand; an independent term keeps its own batch demand.
    internal const string CurrentParentDemandSql = """
        p.target_kind='media' AND (p.state IN('queued','running','waiting','paused')
          OR p.state='failed' AND p.error_code IN('dependency_failed','dependency_unknown'))
        AND p.source_id=f.source_id AND p.source_revision=f.source_revision
        AND p.control_revision=f.control_revision AND p.render_revision=f.render_revision
        AND p.target_epoch=cfg.target_epoch AND p.language=cfg.target_language AND m.present=1
        AND ((p.field='Name' AND cfg.title_enabled=1) OR (p.field='Overview' AND cfg.overview_enabled=1) OR (p.field='Genres' AND cfg.genres_enabled=1))
        AND (p.autonomous_demand=1 AND $automatic=1 OR EXISTS(
          SELECT 1 FROM batch_items bi JOIN batches b ON b.id=bi.batch_id
          WHERE bi.work_id=p.id AND bi.active=1 AND b.cancelled=0 AND b.kind NOT IN('added','nfo')))
        """;
    internal const string TermDemandSql = """
        (w.autonomous_demand=1 AND $automatic=1 OR EXISTS(
          SELECT 1 FROM batch_items bi JOIN batches b ON b.id=bi.batch_id
          WHERE bi.work_id=w.id AND bi.active=1 AND b.cancelled=0 AND b.kind NOT IN('added','nfo'))
        OR w.target_kind='term' AND EXISTS(
          SELECT 1 FROM work_dependencies dep JOIN work_items p ON p.id=dep.parent_id
          JOIN media_fields f ON f.media_id=p.media_id AND f.field=p.field
          JOIN media_items m ON m.id=p.media_id JOIN managed_libraries l ON l.library_id=m.library_id
          JOIN settings cfg ON cfg.singleton=1 WHERE dep.child_id=w.id AND
        """ + " " + CurrentParentDemandSql + "))";
    internal static bool HasDemand(SqliteConnection db, SqliteTransaction? tx, UnifiedWork work, bool automaticEnabled, int depth = 0)
    {
        if (depth > 2) throw new InvalidOperationException("DependencyCycle");
        return Scalar<long>(db, tx, "SELECT COUNT(*) FROM work_items w WHERE w.id=$w AND " + TermDemandSql,
            ("$w", work.Id), ("$automatic", automaticEnabled)) == 1;
    }
    internal static bool Valid(SqliteConnection db, SqliteTransaction? tx, UnifiedWork w, bool requireActive = true, bool allowUnreadable = false)
    {
        var s = SettingsStore.Read(db, tx);
        if (requireActive && w.State is not ("queued" or "running" or "waiting") || w.TargetEpoch != s.TargetEpoch || w.Language != s.Language || !HasDemand(db, tx, w, s.AutoEnabled && s.BaselineEstablished)) return false;
        if (w.TargetKind == "term") return Scalar<long>(db, tx, "SELECT COUNT(*) FROM genre_terms WHERE id=$t AND source_id=$s AND control_revision=$cr", ("$t", w.TermId), ("$s", w.SourceId), ("$cr", w.ControlRevision)) == 1;
        var field = ContentStore.ReadField(db, tx, w.MediaId!, w.Field);
        return field is not null && (allowUnreadable || field.Availability != "unreadable") && InScope(db, tx, w.MediaId!, w.Field, s) && field.SourceId == w.SourceId && field.SourceRevision == w.SourceRevision
            && field.ControlRevision == w.ControlRevision && field.RenderRevision == w.RenderRevision;
    }
    private void CancelUnused(SqliteConnection db, SqliteTransaction tx)
    {
        var ids = new List<string>();
        using (var cmd = Command(db, tx, "SELECT id FROM work_items WHERE state IN('queued','waiting','running','paused') ORDER BY CASE target_kind WHEN 'media' THEN 0 ELSE 1 END"))
        using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        var auto = SettingsStore.Read(db, tx).AutoEnabled;
        foreach (var id in ids)
            if (!HasDemand(db, tx, Find(db, tx, id)!, auto)) State(db, tx, id, "cancelled", "source", completion: "demand_cancelled");
    }
    // Obsolete controls remain history. Current selected terms resolve reuse work;
    // EnsureGenreDependencies attaches a replacement request before checking blockers.
    private const string UnresolvedDependency = """
        NOT (w.target_kind='media' AND w.field='Genres' AND EXISTS(
          SELECT 1 FROM genre_terms t WHERE t.id=c.term_id AND t.source_id=c.source_id
          AND (t.control_revision<>c.control_revision OR w.force_generation=0 AND EXISTS(
            SELECT 1 FROM translation_selections selected WHERE selected.source_id=t.source_id AND selected.language=w.language))))
        """;
    public UnifiedWork? Claim(string owner) => database.Write((db, tx) =>
    {
        for (var n = 0; n < 1000; n++)
        {
            var id = Scalar<string>(db, tx, $"""
                SELECT id FROM work_items w WHERE state IN('queued','waiting') AND (next_run_utc IS NULL OR next_run_utc<=$now)
                  AND (lease_until_utc IS NULL OR lease_until_utc<=$now)
                  AND NOT EXISTS(SELECT 1 FROM work_dependencies d JOIN work_items c ON c.id=d.child_id
                    WHERE d.parent_id=w.id AND c.state IN('queued','running','waiting') AND {UnresolvedDependency})
                ORDER BY created_utc,rowid LIMIT 1
                """, ("$now", Stamp()));
            if (id is null) return null;
            var work = Find(db, tx, id)!;
            var settings = SettingsStore.Read(db, tx);
            if (!settings.BaselineEstablished && HasDemand(db, tx, work, settings.AutoEnabled) && !HasDemand(db, tx, work, false))
            { State(db, tx, id, "waiting", "source", "source", error: "baseline_pending", next: UtcNow.AddSeconds(1)); continue; }
            if (!Valid(db, tx, work) && Valid(db, tx, work, allowUnreadable: true))
            { State(db, tx, id, "waiting", "source", "source", "source", "source_unavailable", next: UtcNow.AddSeconds(3)); continue; }
            if (!Valid(db, tx, work)) { State(db, tx, id, "expired", work.Step, completion: "intent_expired"); continue; }
            Execute(db, tx, "UPDATE work_items SET state='running',lease_owner=$o,lease_until_utc=$until,updated_utc=$n WHERE id=$id",
                ("$o", owner), ("$until", UtcNow.AddMinutes(5).ToString("O")), ("$n", Stamp()), ("$id", id));
            return Find(db, tx, id);
        }
        return null;
    });
    public TimeSpan NextWakeDelay(TimeSpan ceiling)
    {
        if (ceiling < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ceiling));
        using var db = database.Open();
        var next = Scalar<string>(db, null, $"""
            SELECT MIN(CASE WHEN next_run_utc IS NULL THEN COALESCE(lease_until_utc,$now)
              WHEN lease_until_utc IS NULL OR next_run_utc>=lease_until_utc THEN next_run_utc ELSE lease_until_utc END)
            FROM work_items w WHERE state IN('queued','waiting')
              AND NOT EXISTS(SELECT 1 FROM work_dependencies d JOIN work_items c ON c.id=d.child_id
                WHERE d.parent_id=w.id AND c.state IN('queued','running','waiting') AND {UnresolvedDependency})
            """, ("$now", Stamp()));
        if (next is null) return ceiling;
        if (!DateTimeOffset.TryParse(next, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var due))
            return TimeSpan.FromMilliseconds(Math.Min(25, ceiling.TotalMilliseconds));
        return TimeSpan.FromMilliseconds(Math.Clamp((due - UtcNow).TotalMilliseconds, 0, ceiling.TotalMilliseconds));
    }
    internal void State(SqliteConnection db, SqliteTransaction tx, string id, string state, string step, string? wait = null, string? issue = null,
        string? error = null, string? completion = null, DateTimeOffset? next = null) => Execute(db, tx, """
        UPDATE work_items SET state=$state,step=$step,wait_reason=$wait,issue_kind=$issue,error_code=$error,completion_reason=$completion,
          next_run_utc=$next,lease_owner=NULL,lease_until_utc=NULL,updated_utc=$now WHERE id=$id
        """, ("$state", state), ("$step", step), ("$wait", wait), ("$issue", issue), ("$error", error), ("$completion", completion),
        ("$next", next?.ToUniversalTime().ToString("O")), ("$now", Stamp()), ("$id", id));
    internal void SetState(string id, string state, string step, string? wait = null, string? issue = null, string? error = null, string? completion = null, DateTimeOffset? next = null) =>
        database.Write((db, tx) => { State(db, tx, id, state, step, wait, issue, error, completion, next); return true; });
    internal T MeasureSave<T>(string id, Func<T> save)
    {
        var timer = Stopwatch.StartNew();
        var result = save(); // Includes the successful business transaction commit.
        timer.Stop();
        if (result is not null && !Equals(result, false)) RecordSaveTime(id, timer.Elapsed);
        return result;
    }
    internal void RecordSaveTime(string id, TimeSpan elapsed) => database.Write((db, tx) =>
        Execute(db, tx, "UPDATE work_items SET save_ms=save_ms+$ms WHERE id=$id", ("$ms", Math.Max(1L, (long)Math.Ceiling(elapsed.TotalMilliseconds))), ("$id", id)), wakeExecutor: false);
    public string Retry(string id, bool acceptUnknown = false, string? expectedVersion = null) => database.Write((db, tx) =>
    {
        if (expectedVersion is not null && Scalar<string>(db, tx,
            "SELECT updated_utc||'|'||state||'|'||attempt_count FROM work_items WHERE id=$id", ("$id", id)) != expectedVersion)
            throw new InvalidOperationException("RevisionConflict");
        return Retry(db, tx, id, acceptUnknown);
    });
    private string Retry(SqliteConnection db, SqliteTransaction tx, string id, bool acceptUnknown)
    {
        var w = Find(db, tx, id) ?? throw new InvalidOperationException("WorkMissing");
        if (w.State is not ("failed" or "paused") || !Valid(db, tx, w, requireActive: false, allowUnreadable: true)) throw new InvalidOperationException("RetryIntentExpired");
        var replacement = Scalar<string>(db, tx, """
            SELECT id FROM work_items WHERE id<>$id AND target_kind=$kind AND media_id IS $m AND term_id IS $t AND field=$f
              AND source_id=$s AND source_revision=$sr AND control_revision=$cr AND render_revision=$rr
              AND language=$l AND target_epoch=$e AND generation_key=$g AND state IN('queued','running','waiting','paused')
            """, ("$id", id), ("$kind", w.TargetKind), ("$m", w.MediaId), ("$t", w.TermId), ("$f", w.Field), ("$s", w.SourceId),
            ("$sr", w.SourceRevision), ("$cr", w.ControlRevision), ("$rr", w.RenderRevision), ("$l", w.Language), ("$e", w.TargetEpoch), ("$g", w.GenerationKey));
        if (replacement is not null) return replacement;
        if (Scalar<long>(db, tx, "SELECT COUNT(*) FROM request_attempts WHERE (work_id=$w OR input_id=$i) AND state='unknown' AND uncertainty_acknowledged=0", ("$w", id), ("$i", w.InputId)) > 0)
        {
            if (!acceptUnknown) throw new InvalidOperationException("UnknownOutcomeConsentRequired");
            Execute(db, tx, "UPDATE request_attempts SET uncertainty_acknowledged=1 WHERE (work_id=$w OR input_id=$i) AND state='unknown' AND uncertainty_acknowledged=0", ("$w", id), ("$i", w.InputId));
        }
        Execute(db, tx, "DELETE FROM connection_blocks WHERE connection_id IN(SELECT connection_id FROM request_attempts WHERE work_id=$w)", ("$w", id));
        State(db, tx, id, "queued", w.TranslationId is not null ? "display" : "source");
        if (w.TargetKind == "media" && w.Field == "Genres" && w.Error is "dependency_failed" or "dependency_unknown")
            EnsureGenreDependencies(db, tx, w);
        var children = new List<string>(); using (var cmd = Command(db, tx, "SELECT c.id FROM work_dependencies d JOIN work_items c ON c.id=d.child_id WHERE d.parent_id=$id AND c.state IN('failed','paused')", ("$id", id)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) children.Add(r.GetString(0));
        foreach (var child in children)
        {
            var item = Find(db, tx, child)!;
            if (Valid(db, tx, item, requireActive: false) && (w.ForceGeneration || Scalar<long>(db, tx, "SELECT COUNT(*) FROM translation_selections WHERE source_id=$s AND language=$l", ("$s", item.SourceId), ("$l", item.Language)) == 0))
            {
                var activeChild = Retry(db, tx, child, acceptUnknown);
                if (activeChild != child)
                {
                    Execute(db, tx, "INSERT OR IGNORE INTO work_dependencies VALUES($p,$c)", ("$p", id), ("$c", activeChild));
                    Execute(db, tx, "DELETE FROM work_dependencies WHERE parent_id=$p AND child_id=$c", ("$p", id), ("$c", child));
                }
            }
        }
        return id;
    }
    internal string[] EnsureGenreDependencies(UnifiedWork parent) => database.Write((db, tx) => EnsureGenreDependencies(db, tx, parent));
    private string[] EnsureGenreDependencies(SqliteConnection db, SqliteTransaction tx, UnifiedWork parent)
    {
        if (!Valid(db, tx, Find(db, tx, parent.Id)!)) return [];
        var terms = new List<(string Id, string Source, long Control, bool Replaced, bool Selected)>();
        using (var cmd = Command(db, tx, """
            SELECT t.id,t.source_id,t.control_revision,
              EXISTS(SELECT 1 FROM work_dependencies d JOIN work_items previous ON previous.id=d.child_id
                WHERE d.parent_id=$parent AND previous.term_id=t.id AND previous.control_revision<>t.control_revision),
              EXISTS(SELECT 1 FROM translation_selections x WHERE x.source_id=t.source_id AND x.language=$l)
            FROM genre_members m JOIN genre_terms t ON t.id=m.term_id
            WHERE m.source_id=$s AND ($force=1 OR NOT EXISTS(SELECT 1 FROM translation_selections x WHERE x.source_id=t.source_id AND x.language=$l))
            ORDER BY m.ordinal
            """, ("$s", parent.SourceId), ("$l", parent.Language), ("$force", parent.ForceGeneration), ("$parent", parent.Id)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) terms.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetBoolean(3), r.GetBoolean(4)));
        var children = new List<string>();
        var confirmedFill = Scalar<long>(db, tx, "SELECT COUNT(*) FROM batch_items bi JOIN batches b ON b.id=bi.batch_id WHERE bi.work_id=$w AND bi.active=1 AND b.cancelled=0 AND b.kind='fill'", ("$w", parent.Id)) > 0;
        foreach (var t in terms)
        {
            var generation = parent.ForceGeneration ? parent.GenerationKey : "reuse";
            string? child = null;
            if (t.Replaced)
            {
                child = Scalar<string>(db, tx, """
                    SELECT id FROM work_items WHERE target_kind='term' AND term_id=$t AND source_id=$s AND control_revision=$cr
                    AND language=$l AND target_epoch=$e AND state NOT IN('expired','cancelled')
                    AND (state<>'completed' OR completion_reason IS NOT 'history_only') ORDER BY created_utc DESC,rowid DESC LIMIT 1
                    """, ("$t", t.Id), ("$s", t.Source), ("$cr", t.Control), ("$l", parent.Language), ("$e", parent.TargetEpoch));
                // A newer manual selection supersedes the old generation without a send.
                if (child is null && t.Selected) continue;
            }
            child ??= Scalar<string>(db, tx, """
                SELECT id FROM work_items WHERE target_kind='term' AND term_id=$t AND source_id=$s AND control_revision=$cr
                AND language=$l AND target_epoch=$e AND generation_key=$g AND state NOT IN('expired','cancelled')
                AND (state<>'completed' OR completion_reason IS NOT 'history_only') ORDER BY created_utc DESC,rowid DESC LIMIT 1
                """, ("$t", t.Id), ("$s", t.Source), ("$cr", t.Control), ("$l", parent.Language), ("$e", parent.TargetEpoch), ("$g", generation));
            if (child is null)
            {
                child = Id();
                Execute(db, tx, """
                    INSERT INTO work_items(id,submission_key,target_kind,field,term_id,source_id,source_revision,control_revision,language,target_epoch,generation_key,force_generation,state,step,created_utc,updated_utc)
                    VALUES($id,$key,'term','Term',$t,$s,0,$cr,$l,$e,$g,$force,'queued','reuse',$n,$n)
                    """, ("$id", child), ("$key", "term:" + child), ("$t", t.Id), ("$s", t.Source), ("$cr", t.Control), ("$l", parent.Language), ("$e", parent.TargetEpoch),
                    ("$g", generation), ("$force", parent.ForceGeneration), ("$n", Stamp()));
            }
            var newDependency = Execute(db, tx, "INSERT OR IGNORE INTO work_dependencies VALUES($p,$c)", ("$p", parent.Id), ("$c", child)) == 1;
            var previous = Find(db, tx, child)!;
            if (newDependency && confirmedFill && previous.State == "failed" && Valid(db, tx, previous, requireActive: false)
                && Scalar<long>(db, tx, """
                    SELECT COUNT(*) FROM work_dependencies d JOIN batch_items peer ON peer.work_id=d.parent_id
                    JOIN batch_items current ON current.batch_id=peer.batch_id AND current.work_id=$p JOIN batches b ON b.id=current.batch_id
                    WHERE d.child_id=$c AND d.parent_id<>$p AND b.kind='fill' AND b.cancelled=0 AND current.active=1
                    """, ("$p", parent.Id), ("$c", child)) == 0
                && Scalar<long>(db, tx, "SELECT COUNT(*) FROM request_attempts WHERE (work_id=$w OR input_id=$i) AND state='unknown' AND uncertainty_acknowledged=0", ("$w", child), ("$i", previous.InputId)) == 0)
                Retry(db, tx, child, acceptUnknown: false);
            children.Add(child);
        }
        return children.ToArray();
    }
    public UnifiedWork[] Dependencies(string parent)
    {
        using var db = database.Open(); using var cmd = Command(db, null, "SELECT " + string.Join(',', Columns.Split(',').Select(x => "w." + x)) + " FROM work_dependencies d JOIN work_items w ON w.id=d.child_id WHERE parent_id=$p ORDER BY w.created_utc,w.rowid", ("$p", parent));
        using var r = cmd.ExecuteReader(); var values = new List<UnifiedWork>(); while (r.Read()) values.Add(Read(r)); return [.. values];
    }
    internal UnifiedWork[] BlockingDependencies(string parent)
    {
        using var db = database.Open(); using var cmd = Command(db, null,
            "SELECT " + string.Join(',', Columns.Split(',').Select(x => "c." + x)) + $"""
             FROM work_dependencies d JOIN work_items c ON c.id=d.child_id JOIN work_items w ON w.id=d.parent_id
             WHERE d.parent_id=$p AND {UnresolvedDependency} ORDER BY c.created_utc,c.rowid
            """, ("$p", parent));
        using var r = cmd.ExecuteReader(); var values = new List<UnifiedWork>(); while (r.Read()) values.Add(Read(r)); return [.. values];
    }
    internal void WakeParents(string child) => database.Write((db, tx) =>
    {
        var parents = new List<string>(); using (var cmd = Command(db, tx, "SELECT parent_id FROM work_dependencies WHERE child_id=$c", ("$c", child)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) parents.Add(r.GetString(0));
        foreach (var id in parents)
        {
            var p = Find(db, tx, id)!;
            if (p.State is "waiting" or "failed" && p.Error is null or "dependency_failed" or "dependency_unknown" && Valid(db, tx, p, requireActive: false))
            {
                var replacement = Scalar<long>(db, tx, """
                    SELECT COUNT(*) FROM work_items WHERE id<>$id AND media_id=$m AND field=$f AND source_id=$s
                      AND source_revision=$sr AND control_revision=$cr AND render_revision=$rr AND language=$l AND target_epoch=$e AND generation_key=$g
                      AND state IN('queued','running','waiting','paused')
                    """, ("$id", id), ("$m", p.MediaId), ("$f", p.Field), ("$s", p.SourceId), ("$sr", p.SourceRevision), ("$cr", p.ControlRevision),
                    ("$rr", p.RenderRevision), ("$l", p.Language), ("$e", p.TargetEpoch), ("$g", p.GenerationKey));
                if (replacement == 0) State(db, tx, id, "queued", "reuse");
            }
        }
        return true;
    });
    public void AdmitDisplayIntents()
    {
        var items = new List<(string Id, long Revision, string Media, string Field, string Expected, bool Original, bool Keep)>();
        using (var db = database.Open())
        using (var cmd = Command(db, null, """
            SELECT i.id,i.revision,i.media_id,i.field,i.expected_value,i.translation_id IS NULL,i.keep_original FROM display_intents i
            JOIN media_fields f ON f.media_id=i.media_id AND f.field=i.field JOIN settings s ON s.singleton=1
            WHERE i.state='pending' AND i.source_id=f.source_id AND i.source_revision=f.source_revision AND i.control_revision=f.control_revision
              AND i.render_revision=f.render_revision AND i.target_epoch=s.target_epoch
              AND NOT EXISTS(SELECT 1 FROM work_items w WHERE w.media_id=i.media_id AND w.field=i.field AND w.source_id=i.source_id
                AND w.source_revision=i.source_revision AND w.control_revision=i.control_revision AND w.target_epoch=i.target_epoch AND w.state IN('queued','running','waiting','paused'))
            LIMIT 100
            """))
        using (var r = cmd.ExecuteReader()) while (r.Read()) items.Add((r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6)));
        foreach (var i in items)
        {
            var command = "display-intent:" + i.Id + ":" + i.Revision;
            using (var db = database.Open()) if (Scalar<long>(db, null, "SELECT COUNT(*) FROM batches WHERE submission_key=$k", ("$k", command)) > 0) continue;
            var field = new ContentStore(database).ReadField(i.Media, i.Field); if (field is null) continue;
            var s = new SettingsStore(database).Read();
            try { Submit(command, "manual", s.Revision, [new(field, i.Expected)], operation: i.Original && !i.Keep ? "restore" : "display"); }
            catch (InvalidOperationException ex) when (ex.Message is "RevisionConflict" or "TargetLanguageChanged") { /* The next loop rechecks the current intent. */ }
        }
    }
}
