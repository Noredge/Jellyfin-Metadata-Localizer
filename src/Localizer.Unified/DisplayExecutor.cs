using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

internal sealed class DisplayExecutor(WorkStore work, IUnifiedHost host, Action<string, UnifiedWork>? checkpoint)
{
    private UnifiedDatabase Database => work.Database;
    internal static bool Equal(string field, string a, string b) => field == "Genres"
        ? JsonSerializer.Deserialize<string[]>(a)!.ToHashSet(StringComparer.Ordinal).SetEquals(JsonSerializer.Deserialize<string[]>(b)!)
        : field == "Overview" ? a.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') == b.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : a == b;
    internal async Task ApplyAsync(UnifiedWork w, FieldState field, HostFieldObservation observed, string target, string? translationId, bool keepOriginal, CancellationToken token)
    {
        if (!Current(w.Id)) { work.SetState(w.Id, "expired", "display", completion: "intent_expired"); return; }
        string? intentId = null; long revision = 0; string? writeId = null; string? priorState = null;
        var conflict = false;
        Database.Write((db, tx) =>
        {
            var currentWork = WorkStore.Find(db, tx, w.Id)!;
            if (!WorkStore.Valid(db, tx, currentWork)) { work.State(db, tx, w.Id, "expired", "display", completion: "intent_expired"); return false; }
            if (!Equal(w.Field, observed.Display, target) && !Equal(w.Field, observed.Display, w.ExpectedDisplay)) conflict = true;
            string? existingId = null; string? existingTarget = null; string? existingTranslation = null; string? mode = null; long sourceRevision = -1, control = -1, render = -1, epoch = -1; bool oldKeep = false;
            using (var cmd = Command(db, tx, "SELECT id,revision,desired_value,expected_value,mode,source_revision,control_revision,render_revision,target_epoch,keep_original,translation_id FROM display_intents WHERE media_id=$m AND field=$f", ("$m", w.MediaId), ("$f", w.Field)))
            using (var r = cmd.ExecuteReader()) if (r.Read())
            {
                existingId = r.GetString(0); revision = r.GetInt64(1); existingTarget = r.IsDBNull(2) ? null : r.GetString(2);
                mode = r.GetString(4); sourceRevision = r.GetInt64(5); control = r.GetInt64(6); render = r.GetInt64(7); epoch = r.GetInt64(8); oldKeep = r.GetBoolean(9); existingTranslation = r.IsDBNull(10) ? null : r.GetString(10);
            }
            if (mode == "external" && sourceRevision == w.SourceRevision && control == w.ControlRevision && !Equal(w.Field, observed.Display, target)) conflict = true;
            if (conflict)
            {
                work.State(db, tx, w.Id, "failed", "display", issue: "display", error: "display_conflict");
                if (existingId is not null) Execute(db, tx, "UPDATE display_intents SET state='conflict',error_code='display_conflict' WHERE id=$id AND control_revision=$cr", ("$id", existingId), ("$cr", w.ControlRevision));
                return false;
            }
            var desiredMode = translationId is null ? "original" : "translated";
            if (existingId is null || sourceRevision != w.SourceRevision || control != w.ControlRevision || render != w.RenderRevision || epoch != w.TargetEpoch
                || mode != desiredMode || oldKeep != keepOriginal || existingTranslation != translationId || existingTarget is null || !Equal(w.Field, existingTarget, target))
            {
                // ContentStore adds a title prefix to translation text; this layer already has the rendered target.
                var body = translationId is not null && w.Field == "Name" ? target[field.Prefix.Length..] : target;
                ContentStore.SetIntent(db, tx, field, w.Language, translationId, desiredMode, body, w.ExpectedDisplay, keepOriginal);
                using var cmd = Command(db, tx, "SELECT id,revision FROM display_intents WHERE media_id=$m AND field=$f", ("$m", w.MediaId), ("$f", w.Field));
                using var r = cmd.ExecuteReader(); r.Read(); intentId = r.GetString(0); revision = r.GetInt64(1);
            }
            else intentId = existingId;
            if (translationId is not null) Execute(db, tx, "UPDATE work_items SET translation_id=$t WHERE id=$w", ("$t", translationId), ("$w", w.Id));
            var action = w.Operation == "restore" || translationId is null ? "restore" : "apply";
            using (var cmd = Command(db, tx, "SELECT id,state FROM display_writes WHERE intent_id=$i AND intent_revision=$r AND action=$a", ("$i", intentId), ("$r", revision), ("$a", action)))
            using (var r = cmd.ExecuteReader()) if (r.Read()) { writeId = r.GetString(0); priorState = r.GetString(1); }
            if (Equal(w.Field, observed.Display, target))
            {
                if (writeId is not null) Verify(db, tx, w, intentId!, revision, writeId);
                else
                {
                    Execute(db, tx, "UPDATE display_intents SET state='applied',error_code=NULL,updated_utc=$n WHERE id=$i AND revision=$r", ("$n", work.Stamp()), ("$i", intentId), ("$r", revision));
                    work.State(db, tx, w.Id, "completed", "verify", completion: keepOriginal ? "original_preserved" : "already_displayed");
                }
                return false;
            }
            if (!observed.Writable)
            {
                work.State(db, tx, w.Id, "failed", "display", issue: "display", error: observed.DisplayError ?? "display_blocked");
                Execute(db, tx, "UPDATE display_intents SET state='blocked',error_code=$e WHERE id=$id AND revision=$r", ("$e", observed.DisplayError ?? "display_blocked"), ("$id", intentId), ("$r", revision));
                return false;
            }
            if (writeId is null)
            {
                writeId = Hash(JsonSerializer.Serialize(new { intentId, revision, action }));
                string? parent = null;
                if (action == "restore")
                {
                    using var prior = Command(db, tx, """
                    SELECT id,before_value,target_value FROM display_writes p WHERE intent_id=$i AND state='verified' AND action='apply'
                    AND source_revision=$sr AND NOT EXISTS(SELECT 1 FROM display_writes r WHERE r.restores_write_id=p.id AND r.state='verified') ORDER BY verified_utc DESC LIMIT 1
                    """, ("$i", intentId), ("$sr", w.SourceRevision));
                    using var p = prior.ExecuteReader(); if (p.Read() && Equal(w.Field, p.GetString(1), target) && Equal(w.Field, p.GetString(2), observed.Display)) parent = p.GetString(0);
                }
                Execute(db, tx, """
                    INSERT INTO display_writes(id,intent_id,intent_revision,action,state,source_revision,control_revision,target_epoch,before_value,before_hash,target_value,target_hash,restores_write_id,created_utc)
                    VALUES($id,$i,$r,$a,'prepared',$sr,$cr,$e,$b,$bh,$t,$th,$parent,$n)
                    """, ("$id", writeId), ("$i", intentId), ("$r", revision), ("$a", action), ("$sr", w.SourceRevision), ("$cr", w.ControlRevision), ("$e", w.TargetEpoch),
                    ("$b", observed.Display), ("$bh", Hash(observed.Display)), ("$t", target), ("$th", Hash(target)), ("$parent", parent), ("$n", work.Stamp()));
            }
            else if (priorState is "conflict" or "expired")
            {
                work.State(db, tx, w.Id, "failed", "display", issue: "display", error: "display_conflict"); return false;
            }
            Execute(db, tx, "UPDATE work_items SET step='display',updated_utc=$n WHERE id=$id", ("$n", work.Stamp()), ("$id", w.Id));
            checkpoint?.Invoke("display.intent_transaction", w);
            return true;
        });
        if (conflict || work.Find(w.Id)!.State != "running" || writeId is null || intentId is null) return;
        checkpoint?.Invoke("display.prepared", w);
        if (!Current(w.Id)) { work.SetState(w.Id, "expired", "display", completion: "intent_expired"); return; }
        Database.Write((db, tx) =>
        {
            Execute(db, tx, "UPDATE display_writes SET state='called',called_utc=COALESCE(called_utc,$n),error_code=NULL WHERE id=$id", ("$n", work.Stamp()), ("$id", writeId));
            Execute(db, tx, "UPDATE work_items SET step='verify' WHERE id=$id", ("$id", w.Id)); return true;
        });
        checkpoint?.Invoke("display.marked", w);
        var timer = Stopwatch.StartNew();
        HostWriteOutcome result;
        try
        {
            result = await host.WriteAsync(new(observed.Key, observed.MediaPath, w.Field, field.Content, field.Prefix,
                w.ExpectedDisplay, target, () => Current(w.Id)), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        { work.SetState(w.Id, "waiting", "verify", "display", "display", "display_outcome_unknown", next: work.UtcNow.AddSeconds(2)); return; }
        finally
        {
            Database.Write((db, tx) => { Execute(db, tx, "UPDATE work_items SET display_ms=display_ms+$ms WHERE id=$id", ("$ms", timer.ElapsedMilliseconds), ("$id", w.Id)); return true; });
        }
        checkpoint?.Invoke("display.returned", w);
        var readback = await host.ReadAsync(observed.Key, w.Field, token);
        if (!readback.Present) new ContentStore(Database).MarkRemoved(w.MediaId!);
        else if (readback.Original is not null && readback.SourceError is null)
            new ContentStore(Database).Observe(w.MediaId!, w.Field, readback.Original, readback.EvidenceJson, readback.Prefix, readback.Display);
        else new ContentStore(Database).ObserveUnavailable(w.MediaId!, w.Field, readback.SourceError ?? "source_unavailable");
        Database.Write((db, tx) =>
        {
            if (Equal(w.Field, readback.Display, target)) Verify(db, tx, w, intentId, revision, writeId);
            else
            {
                if (!WorkStore.Valid(db, tx, WorkStore.Find(db, tx, w.Id)!))
                {
                    Execute(db, tx, "UPDATE display_writes SET state='expired',error_code='intent_expired' WHERE id=$id", ("$id", writeId));
                    Execute(db, tx, "UPDATE display_intents SET state='expired',error_code='intent_expired' WHERE id=$id AND revision=$r", ("$id", intentId), ("$r", revision));
                    work.State(db, tx, w.Id, "expired", "verify", completion: "intent_expired"); return true;
                }
                var error = result == HostWriteOutcome.Blocked ? readback.DisplayError ?? "display_blocked" : "display_conflict";
                Execute(db, tx, "UPDATE display_writes SET state=$state,error_code=$e WHERE id=$id", ("$state", result == HostWriteOutcome.Blocked ? "blocked" : "conflict"), ("$e", error), ("$id", writeId));
                Execute(db, tx, "UPDATE display_intents SET state=$state,error_code=$e WHERE id=$id AND revision=$r", ("$state", result == HostWriteOutcome.Blocked ? "blocked" : "conflict"), ("$e", error), ("$id", intentId), ("$r", revision));
                work.State(db, tx, w.Id, "failed", "verify", issue: "display", error: error);
            }
            return true;
        });
        checkpoint?.Invoke("display.verified", w);
    }
    private bool Current(string id)
    { using var db = Database.Open(); return WorkStore.Find(db, null, id) is { } w && WorkStore.Valid(db, null, w); }
    private void Verify(SqliteConnection db, SqliteTransaction tx, UnifiedWork w, string intentId, long revision, string writeId)
    {
        Execute(db, tx, "UPDATE display_writes SET state='verified',verified_utc=COALESCE(verified_utc,$n),error_code=NULL WHERE id=$id", ("$n", work.Stamp()), ("$id", writeId));
        var current = WorkStore.Find(db, tx, w.Id)!;
        if (WorkStore.Valid(db, tx, current))
        {
            Execute(db, tx, "UPDATE display_intents SET state='applied',error_code=NULL,updated_utc=$n WHERE id=$id AND revision=$r", ("$n", work.Stamp()), ("$id", intentId), ("$r", revision));
            work.State(db, tx, w.Id, "completed", "verify", completion: w.Operation == "restore" ? "original_restored" : w.TranslationId is not null ? "translation_displayed" : "displayed");
        }
        else work.State(db, tx, w.Id, "completed", "verify", completion: "history_only");
    }
}
