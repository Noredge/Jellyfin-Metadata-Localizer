using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

internal sealed class RequestStore(WorkStore work, Action<string, UnifiedWork>? checkpoint = null)
{
    private UnifiedDatabase Database => work.Database;
    internal RequestAttempt? Prepare(UnifiedWork expected, string promptVersion, string[]? protectedNames)
    {
        return Database.Write<RequestAttempt?>((db, tx) =>
        {
            var w = WorkStore.Find(db, tx, expected.Id)!;
            if (!WorkStore.Valid(db, tx, w))
            {
                if (WorkStore.Valid(db, tx, w, allowUnreadable: true)) work.State(db, tx, w.Id, "waiting", "source", "source", "source", "source_unavailable", next: work.UtcNow.AddSeconds(3));
                else work.State(db, tx, w.Id, "expired", w.Step, completion: "intent_expired");
                return null;
            }
            var settings = SettingsStore.Read(db, tx); var connection = SettingsStore.Connection(db, tx, settings.ConnectionId);
            if (connection is null) { work.State(db, tx, w.Id, "paused", "request", issue: "translation", error: "connection_required"); return null; }
            using (var blocked = Command(db, tx, """
                SELECT b.reason,b.until_utc FROM connection_blocks b JOIN connection_versions c ON c.id=b.connection_id
                WHERE c.endpoint=$e AND (c.credential_reference IS $c OR b.reason='rate_limit') AND (b.until_utc IS NULL OR b.until_utc>$now)
                ORDER BY b.until_utc IS NULL DESC,b.until_utc DESC LIMIT 1
                """, ("$e", connection.Endpoint), ("$c", connection.CredentialReference), ("$now", work.Stamp())))
            using (var r = blocked.ExecuteReader())
                if (r.Read())
                {
                    var reason = r.GetString(0); var until = r.IsDBNull(1) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
                    r.Close(); work.State(db, tx, w.Id, until is null ? "paused" : "waiting", "request", until is null ? null : "rate_limit", "translation", reason, next: until); return null;
                }
            using (var limiter = Command(db, tx, """
                SELECT COUNT(*),MIN(r.send_started_utc) FROM request_attempts r JOIN connection_versions c ON c.id=r.connection_id
                WHERE c.endpoint=$e AND r.send_started_utc>$since
                """, ("$e", connection.Endpoint), ("$since", work.UtcNow.AddMinutes(-1).ToString("O"))))
            using (var r = limiter.ExecuteReader())
                if (r.Read() && r.GetInt64(0) >= connection.RequestsPerMinute)
                {
                    var until = DateTimeOffset.Parse(r.GetString(1), System.Globalization.CultureInfo.InvariantCulture).AddMinutes(1);
                    r.Close(); work.State(db, tx, w.Id, "waiting", "request", "rate_limit", next: until); return null;
                }
            var text = Scalar<string>(db, tx, "SELECT content FROM source_versions WHERE id=$s", ("$s", w.SourceId));
            var names = new List<object>();
            using (var cmd = Command(db, tx, "SELECT original,zh_name,en_name,aliases_json,revision FROM person_name_rules ORDER BY original"))
            using (var r = cmd.ExecuteReader()) while (r.Read())
            {
                var aliases = JsonSerializer.Deserialize<string[]>(r.GetString(3))!;
                if (!text.Contains(r.GetString(0), StringComparison.Ordinal) && !aliases.Any(a => text.Contains(a, StringComparison.Ordinal))) continue;
                names.Add(new { original = r.GetString(0), replacement = r.IsDBNull(w.Language == "en" ? 2 : 1) ? null : r.GetString(w.Language == "en" ? 2 : 1), aliases, revision = r.GetInt64(4) });
            }
            var namesJson = JsonSerializer.Serialize(names);
            var contextJson = JsonSerializer.Serialize(new { protected_names = (protectedNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n) && text.Contains(n, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal) });
            var fp = Hash(JsonSerializer.Serialize(new[] { w.SourceId, w.Language, promptVersion, namesJson, contextJson }));
            var inputId = Scalar<string>(db, tx, "SELECT id FROM generation_inputs WHERE fingerprint=$fp", ("$fp", fp));
            if (inputId is null)
            {
                inputId = Id();
                Execute(db, tx, "INSERT INTO generation_inputs VALUES($id,$s,$l,$p,$names,$ctx,$fp,$n)", ("$id", inputId), ("$s", w.SourceId), ("$l", w.Language),
                    ("$p", promptVersion), ("$names", namesJson), ("$ctx", contextJson), ("$fp", fp), ("$n", work.Stamp()));
            }
            Execute(db, tx, "UPDATE work_items SET input_id=$i WHERE id=$w", ("$i", inputId), ("$w", w.Id));
            var input = new GenerationInput(inputId, w.SourceId, w.Field, w.Language, text, promptVersion, namesJson, contextJson);
            string? activeId = null; string? activeWork = null; string? activeState = null; string? activeConnection = null; int sequence = 0;
            using (var cmd = Command(db, tx, "SELECT id,work_id,state,connection_id,sequence FROM request_attempts WHERE input_id=$i AND (state IN('prepared','sending') OR state='unknown' AND uncertainty_acknowledged=0)", ("$i", inputId)))
            using (var r = cmd.ExecuteReader()) if (r.Read()) { activeId = r.GetString(0); activeWork = r.GetString(1); activeState = r.GetString(2); activeConnection = r.GetString(3); sequence = r.GetInt32(4); }
            if (activeId is not null)
            {
                if (activeWork == w.Id && activeState == "prepared")
                    return new(activeId, w.Id, sequence, input, SettingsStore.Connection(db, tx, activeConnection)!, "prepared");
                work.State(db, tx, w.Id, activeState == "unknown" ? "paused" : "waiting", "request", activeState == "unknown" ? null : "rate_limit", "translation",
                    activeState == "unknown" ? "input_outcome_unknown" : "input_request_pending", next: activeState == "unknown" ? null : work.UtcNow.AddSeconds(1));
                return null;
            }
            sequence = checked((int)Scalar<long>(db, tx, "SELECT COALESCE(MAX(sequence),0)+1 FROM request_attempts WHERE work_id=$w", ("$w", w.Id)));
            var attemptId = Id();
            Execute(db, tx, "INSERT INTO request_attempts(id,work_id,sequence,input_id,connection_id,state,started_utc) VALUES($id,$w,$n,$i,$c,'prepared',$utc)",
                ("$id", attemptId), ("$w", w.Id), ("$n", sequence), ("$i", inputId), ("$c", connection.Id), ("$utc", work.Stamp()));
            Execute(db, tx, "UPDATE work_items SET step='request',attempt_count=$n,updated_utc=$utc WHERE id=$w", ("$n", sequence), ("$utc", work.Stamp()), ("$w", w.Id));
            return new(attemptId, w.Id, sequence, input, connection, "prepared");
        });
    }
    internal void MarkSending(RequestAttempt attempt) => Database.Write((db, tx) =>
    {
        var w = WorkStore.Find(db, tx, attempt.WorkId)!;
        if (!WorkStore.Valid(db, tx, w))
        {
            if (WorkStore.Valid(db, tx, w, allowUnreadable: true)) throw new SourceTemporarilyUnavailableException();
            throw new ExecutionSupersededException();
        }
        if (Execute(db, tx, "UPDATE request_attempts SET state='sending',send_started_utc=$n WHERE id=$id AND state='prepared'", ("$n", work.Stamp()), ("$id", attempt.Id)) != 1)
            throw new InvalidOperationException("AttemptAlreadySent");
        return true;
    });
    internal void Complete(RequestAttempt a, GenerationReply reply, long elapsedMs, bool interrupted = false)
    {
        var timer = Stopwatch.StartNew();
        var saved = Database.Write((db, tx) =>
        {
            var state = Scalar<string>(db, tx, "SELECT state FROM request_attempts WHERE id=$id", ("$id", a.Id));
            if (state is not ("prepared" or "sending")) return false;
            if (interrupted) reply = new(null, state == "sending" ? "network_uncertain" : "request_timeout_before_send", NotSent: state != "sending", OutcomeUnknown: state == "sending");
            var w = WorkStore.Find(db, tx, a.WorkId)!;
            var unknown = reply.OutcomeUnknown && state == "sending";
            var error = Code(reply.Error);
            if (reply.Text is null && error is null) error = "invalid_output";
            if (string.IsNullOrWhiteSpace(reply.Text) && error is null) error = "empty_output";
            if (state == "prepared") error ??= "transport_did_not_mark_send";
            var successful = error is null && state == "sending" && !reply.NotSent && !unknown;
            var finalState = unknown ? "unknown" : state == "prepared" || reply.NotSent ? "not_sent" : successful ? "succeeded" : "failed";
            var translationId = successful ? SaveTranslation(db, tx, a, reply.Text!) : null;
            if (successful) checkpoint?.Invoke("request.result_transaction", w);
            Execute(db, tx, """
                UPDATE request_attempts SET state=$state,completed_utc=$n,http_status=$http,error_code=$error,diagnostics_json=$diag,usage_json=$usage,elapsed_ms=$ms,translation_id=$t WHERE id=$id
                """, ("$state", finalState), ("$n", work.Stamp()), ("$http", reply.HttpStatus), ("$error", unknown ? "network_uncertain" : error),
                ("$diag", JsonSerializer.Serialize(reply.Diagnostics ?? new())), ("$usage", JsonSerializer.Serialize(reply.Usage ?? new())), ("$ms", Math.Max(0, elapsedMs)), ("$t", translationId), ("$id", a.Id));
            Execute(db, tx, "UPDATE work_items SET request_ms=request_ms+$ms,updated_utc=$n WHERE id=$w", ("$ms", Math.Max(0, elapsedMs)), ("$n", work.Stamp()), ("$w", w.Id));
            if (successful)
            {
                Execute(db, tx, "UPDATE work_items SET translation_id=$t WHERE id=$w", ("$t", translationId), ("$w", w.Id));
                if (WorkStore.Valid(db, tx, w))
                {
                    ContentStore.Select(db, tx, w.SourceId, w.Language, translationId!);
                    work.State(db, tx, w.Id, w.TargetKind == "term" ? "completed" : "queued", w.TargetKind == "term" ? "save" : "display", completion: w.TargetKind == "term" ? "term_saved" : null);
                }
                else work.State(db, tx, w.Id, "completed", "save", completion: "history_only");
            }
            else if (!WorkStore.Valid(db, tx, w, allowUnreadable: true)) work.State(db, tx, w.Id, "expired", "request", completion: "intent_expired");
            else if (error == "source_unavailable") work.State(db, tx, w.Id, "waiting", "source", "source", "source", error, next: work.UtcNow.AddSeconds(3));
            else if (unknown) work.State(db, tx, w.Id, "paused", "request", issue: "translation", error: "network_uncertain");
            else if (error is "authentication" or "permission" or "quota" or "credential_unavailable")
            {
                Block(db, tx, a.Connection.Id, error, null);
                work.State(db, tx, w.Id, "paused", "request", issue: "translation", error: error);
            }
            else if (error == "rate_limit")
            {
                var until = work.UtcNow.AddMilliseconds(Math.Clamp(reply.RetryAfterMs ?? 1000, 100, 3600000)); Block(db, tx, a.Connection.Id, error, until);
                work.State(db, tx, w.Id, w.Attempts < 2 ? "waiting" : "failed", "request", w.Attempts < 2 ? "rate_limit" : null, "translation", error, next: w.Attempts < 2 ? until : null);
            }
            else work.State(db, tx, w.Id, "failed", "request", issue: "translation", error: error ?? "invalid_output");
            return true;
        });
        timer.Stop();
        if (saved) work.RecordSaveTime(a.WorkId, timer.Elapsed);
    }
    private string SaveTranslation(SqliteConnection db, SqliteTransaction tx, RequestAttempt a, string text)
    {
        var id = Hash("request-translation\0" + a.Id);
        Execute(db, tx, """
            INSERT OR IGNORE INTO translation_versions(id,source_id,language,text,text_hash,origin,input_id,request_id,created_utc)
            VALUES($id,$s,$l,$t,$h,'model',$i,$r,$n)
            """, ("$id", id), ("$s", a.Input.SourceId), ("$l", a.Input.Language), ("$t", text), ("$h", Hash(text)), ("$i", a.Input.Id), ("$r", a.Id), ("$n", work.Stamp()));
        return id;
    }
    internal void SaveLate(RequestAttempt a, GenerationReply reply)
    {
        work.MeasureSave(a.WorkId, () => Database.Write((db, tx) =>
        {
            if (Scalar<long>(db, tx, "SELECT COUNT(*) FROM request_attempts WHERE id=$id AND state='unknown' AND late_completed_utc IS NULL", ("$id", a.Id)) != 1) return false;
            var tid = reply.Error is null && !string.IsNullOrWhiteSpace(reply.Text) ? SaveTranslation(db, tx, a, reply.Text) : null;
            Execute(db, tx, "UPDATE request_attempts SET late_completed_utc=$n,late_diagnostics_json=$diag,late_usage_json=$usage,late_translation_id=$t WHERE id=$id",
                ("$n", work.Stamp()), ("$diag", JsonSerializer.Serialize(new { outcome = Code(reply.Error) ?? (tid is null ? "empty_output" : "late_history_saved"), diagnostics = reply.Diagnostics })),
                ("$usage", JsonSerializer.Serialize(reply.Usage ?? new())), ("$t", tid), ("$id", a.Id));
            return true;
        }));
    }
    private void Block(SqliteConnection db, SqliteTransaction tx, string id, string reason, DateTimeOffset? until) => Execute(db, tx,
        "INSERT INTO connection_blocks VALUES($id,$e,$u,$n) ON CONFLICT(connection_id) DO UPDATE SET reason=excluded.reason,until_utc=excluded.until_utc,updated_utc=excluded.updated_utc",
        ("$id", id), ("$e", reason), ("$u", until?.ToString("O")), ("$n", work.Stamp()));
    private static string? Code(string? code) => code is null ? null : code.Length is > 0 and <= 128 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : "invalid_output";
    internal void RecoverInterrupted(string id, string error)
    {
        Database.Write((db, tx) =>
        {
            var w = WorkStore.Find(db, tx, id);
            if (w is null) return false;
            var sending = Scalar<long>(db, tx, "SELECT COUNT(*) FROM request_attempts WHERE work_id=$w AND state='sending'", ("$w", id)) > 0;
            Execute(db, tx, "UPDATE request_attempts SET state='unknown',completed_utc=$n,error_code='interrupted_unknown' WHERE work_id=$w AND state='sending'", ("$w", id), ("$n", work.Stamp()));
            Execute(db, tx, "UPDATE request_attempts SET state='not_sent',completed_utc=$n,error_code='interrupted_before_send' WHERE work_id=$w AND state='prepared'", ("$w", id), ("$n", work.Stamp()));
            if (w.State != "running") return true;
            // Only the executor that has stopped processing this item invokes this recovery.
            // A lease timeout never grants permission to replace an outstanding HTTP send.
            if (!WorkStore.Valid(db, tx, w, allowUnreadable: true))
                work.State(db, tx, id, "expired", w.Step, completion: "intent_expired");
            else if (sending) work.State(db, tx, id, "paused", "request", issue: "translation", error: "interrupted_unknown");
            else work.State(db, tx, id, "waiting", w.Step, "source", error: error, next: work.UtcNow.AddSeconds(1));
            return true;
        });
    }
    internal void Recover()
    {
        Database.Write((db, tx) =>
        {
            var uncertain = new List<string>(); using (var cmd = Command(db, tx, "SELECT work_id FROM request_attempts WHERE state='sending'"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) uncertain.Add(r.GetString(0));
            Execute(db, tx, "UPDATE request_attempts SET state='unknown',completed_utc=$n,error_code='interrupted_unknown' WHERE state='sending'", ("$n", work.Stamp()));
            Execute(db, tx, "UPDATE request_attempts SET state='not_sent',completed_utc=$n,error_code='interrupted_before_send' WHERE state='prepared'", ("$n", work.Stamp()));
            Execute(db, tx, "UPDATE work_items SET state='queued',lease_owner=NULL,lease_until_utc=NULL WHERE state='running'");
            foreach (var id in uncertain)
            {
                var w = WorkStore.Find(db, tx, id)!;
                work.State(db, tx, id, WorkStore.Valid(db, tx, w) ? "paused" : "expired", "request", issue: "translation", error: "interrupted_unknown");
            }
            return true;
        });
    }
}
