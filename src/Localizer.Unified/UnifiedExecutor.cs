using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

/// <summary>One runtime executor owns sending, saving and host writes. Source observers never acquire this gate.</summary>
public sealed class UnifiedExecutor : IDisposable
{
    private readonly WorkStore work;
    private readonly ContentStore content;
    private readonly IUnifiedHost host;
    private readonly IUnifiedTransport transport;
    private readonly RequestStore requests;
    private readonly DisplayExecutor display;
    private readonly Action<string, UnifiedWork>? checkpoint;
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly FileStream ownership;
    private readonly string owner = Id();
    private Task? pendingSend;
    private (string Id, string Error)? interrupted;
    private int disposed;
    private string? backgroundError;
    private string currentStage = "claim";
    public ExecutionDiagnostic? LastFailure { get; private set; }
    public string? BackgroundError => Volatile.Read(ref backgroundError);
    public bool HasOutstandingSend => pendingSend is { IsCompleted: false };
    public UnifiedExecutor(WorkStore work, IUnifiedHost host, IUnifiedTransport transport, Action<string, UnifiedWork>? checkpoint = null)
    {
        this.work = work; content = new(work.Database); this.host = host; this.transport = transport; this.checkpoint = checkpoint;
        ownership = new FileStream(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(work.Database.Path)!, "metadata-localizer.worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        requests = new(work, checkpoint); display = new(work, host, checkpoint);
        try { requests.Recover(); work.AdmitDisplayIntents(); } catch { ownership.Dispose(); throw; }
    }
    public Task<bool> RunNextAsync(CancellationToken token = default) => RunNextAsync(token, null);
    public async Task<bool> RunNextAsync(CancellationToken token, Action<ExecutionDiagnostic>? reportFailure)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!await operation.WaitAsync(0, token)) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        UnifiedWork? w = null;
        try
        {
            if (interrupted is { } recovery)
            {
                currentStage = "recovery";
                requests.RecoverInterrupted(recovery.Id, recovery.Error);
                interrupted = null;
            }
            currentStage = "claim";
            work.AdmitDisplayIntents(); w = work.Claim(owner); if (w is null) return false;
            await ProcessAsync(w, linked.Token); return true;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (w is not null && work.Find(w.Id)?.State == "running") work.SetState(w.Id, "queued", w.Step);
            throw;
        }
        catch (Exception error)
        {
            var failure = ExecutionDiagnostic.Capture(currentStage, w?.Id ?? interrupted?.Id, error);
            LastFailure = failure;
            reportFailure?.Invoke(failure);
            if (w is not null)
            {
                interrupted = (w.Id, error is SqliteException ? "executor_database_error" : error is IOException ? "executor_io_error" : "executor_interrupted");
                try { requests.RecoverInterrupted(interrupted.Value.Id, interrupted.Value.Error); interrupted = null; }
                catch (Exception recoveryError) when (recoveryError is SqliteException or IOException or UnauthorizedAccessException)
                { /* Retain ownership and retry persistence before claiming another item. */ }
            }
            throw;
        }
        finally { operation.Release(); }
    }
    private async Task ProcessAsync(UnifiedWork w, CancellationToken token)
    {
        if (w.TargetKind == "term")
        {
            var cached = w.TranslationId ?? (!w.ForceGeneration ? Cached(w) : null);
            if (cached is not null)
            {
                currentStage = "cache_save";
                SelectCached(w, cached); work.SetState(w.Id, "completed", "reuse", completion: "cache_reused"); work.WakeParents(w.Id); return;
            }
            await GenerateAsync(w, null, token); if (work.Find(w.Id)?.State == "completed") work.WakeParents(w.Id); return;
        }
        currentStage = "source_read";
        var identity = Identity(w.MediaId!);
        HostFieldObservation observed;
        try { observed = await host.ReadAsync(identity.Key, w.Field, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not SqliteException)
        { work.SetState(w.Id, "waiting", "source", "source", "source", "source_unavailable", next: work.UtcNow.AddSeconds(3)); return; }
        if (!observed.Present) { content.MarkRemoved(w.MediaId!); return; }
        if (observed.Key != identity.Key || !string.Equals(observed.MediaPath, identity.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        { work.SetState(w.Id, "failed", "source", issue: "source", error: "source_binding_changed"); return; }
        if (observed.Original is null || observed.SourceError is not null)
        {
            content.ObserveUnavailable(w.MediaId!, w.Field, observed.SourceError ?? "source_unavailable");
            work.SetState(w.Id, "waiting", "source", "source", "source", observed.SourceError ?? "source_unavailable", next: work.UtcNow.AddSeconds(3)); return;
        }
        var field = content.Observe(w.MediaId!, w.Field, observed.Original, observed.EvidenceJson, observed.Prefix, observed.Display);
        if (!Current(w.Id)) { work.SetState(w.Id, "expired", "source", completion: "source_changed"); return; }
        if (w.Operation == "sync" && field.Availability == "empty")
        { work.SetState(w.Id, "completed", "source", completion: "empty_skipped"); return; }
        string? tid = null; string? target = null; bool keep = false;
        using (var db = work.Database.Open())
        {
            keep = Scalar<long>(db, null, "SELECT keep_original FROM display_intents WHERE media_id=$m AND field=$f", ("$m", w.MediaId), ("$f", w.Field)) == 1;
            if (w.Operation is "display" or "restore")
            {
                using var cmd = Command(db, null, "SELECT desired_value,translation_id FROM display_intents WHERE media_id=$m AND field=$f AND control_revision=$cr AND source_revision=$sr AND target_epoch=$e", ("$m", w.MediaId), ("$f", w.Field), ("$cr", w.ControlRevision), ("$sr", w.SourceRevision), ("$e", w.TargetEpoch));
                using var r = cmd.ExecuteReader(); if (r.Read()) { target = r.IsDBNull(0) ? null : r.GetString(0); tid = r.IsDBNull(1) ? null : r.GetString(1); }
                if (target is null) { work.SetState(w.Id, "expired", "display", completion: "display_intent_expired"); return; }
            }
        }
        if (target is null && keep) target = OriginalValue(field);
        if (target is null && w.Field == "Genres")
        {
            var selected = content.Selected(field.SourceId, w.Language);
            if (!w.ForceGeneration && selected?.Origin == "manual") { tid = selected.Id; target = selected.Text; }
            else
            {
                work.EnsureGenreDependencies(w);
                var dependencies = work.BlockingDependencies(w.Id);
                if (dependencies.Any(x => x.State is "queued" or "running" or "waiting"))
                { work.SetState(w.Id, "waiting", "reuse", "dependency"); return; }
                if (dependencies.Any(x => x.State is "failed" or "paused"))
                { work.SetState(w.Id, "failed", "reuse", issue: "translation", error: dependencies.Any(x => x.State == "paused") ? "dependency_unknown" : "dependency_failed"); return; }
                currentStage = "genre_save";
                var rendered = RenderGenres(w, field);
                if (rendered is null) { work.SetState(w.Id, "expired", "reuse", completion: "dictionary_control_changed"); return; }
                (tid, target) = rendered.Value;
            }
        }
        if (target is null)
        {
            tid = w.TranslationId ?? (!w.ForceGeneration ? Cached(w) : null);
            if (tid is null) { await GenerateAsync(w, observed.PersonNames, token); return; }
            var text = TranslationText(tid, w.SourceId, w.Language);
            if (text is null) { work.SetState(w.Id, "failed", "reuse", issue: "translation", error: "translation_reference_invalid"); return; }
            currentStage = "cache_save";
            SelectCached(w, tid);
            using var db = work.Database.Open(); text = PersonRenderer.Render(db, null, tid, w.Language);
            target = w.Field == "Name" ? field.Prefix + text : text;
        }
        currentStage = "display";
        await display.ApplyAsync(work.Find(w.Id)!, field, observed, target, tid, keep, token);
    }
    internal (MediaIdentity Key, string Path) Identity(string mediaId)
    {
        using var db = work.Database.Open(); using var cmd = Command(db, null, "SELECT server_id,library_id,item_id,media_path FROM media_items WHERE id=$m", ("$m", mediaId));
        using var r = cmd.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("MediaMissing"); return (new(r.GetString(0), r.GetString(1), r.GetString(2)), r.GetString(3));
    }
    public static string OriginalValue(FieldState field) => field.Field == "Name" ? field.Prefix + field.Content : field.Field == "Genres" ? field.RenderJson : field.Content;
    public static bool SameOriginal(string field, string a, string b) => field == "Genres"
        ? ContentStore.GenreFingerprint(JsonSerializer.Deserialize<string[]>(a)!.Where(x => !string.IsNullOrWhiteSpace(x))) == ContentStore.GenreFingerprint(JsonSerializer.Deserialize<string[]>(b)!.Where(x => !string.IsNullOrWhiteSpace(x)))
        : (string.IsNullOrWhiteSpace(a) ? "" : a.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')) == (string.IsNullOrWhiteSpace(b) ? "" : b.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
    private string? TranslationText(string id, string source, string language)
    { using var db = work.Database.Open(); return Scalar<string>(db, null, "SELECT text FROM translation_versions WHERE id=$id AND source_id=$s AND language=$l", ("$id", id), ("$s", source), ("$l", language)); }
    private string? Cached(UnifiedWork w)
    {
        using var db = work.Database.Open();
        return Scalar<string>(db, null, "SELECT translation_id FROM translation_selections WHERE source_id=$s AND language=$l", ("$s", w.SourceId), ("$l", w.Language))
            ?? Scalar<string>(db, null, """
                SELECT t.id FROM translation_versions t JOIN request_attempts r ON r.id=t.request_id JOIN work_items old ON old.id=r.work_id
                WHERE t.source_id=$s AND t.language=$l AND r.state='succeeded' AND old.control_revision=$cr ORDER BY t.created_utc DESC LIMIT 1
                """, ("$s", w.SourceId), ("$l", w.Language), ("$cr", w.ControlRevision));
    }
    private void SelectCached(UnifiedWork w, string id) => work.MeasureSave(w.Id, () => work.Database.Write((db, tx) =>
    {
        if (!WorkStore.Valid(db, tx, WorkStore.Find(db, tx, w.Id)!)) return false;
        if (Scalar<string>(db, tx, "SELECT translation_id FROM translation_selections WHERE source_id=$s AND language=$l", ("$s", w.SourceId), ("$l", w.Language)) != id)
            ContentStore.Select(db, tx, w.SourceId, w.Language, id);
        Execute(db, tx, "UPDATE work_items SET translation_id=$t,step='display' WHERE id=$id", ("$t", id), ("$id", w.Id)); return true;
    }));
    private (string Id, string Text)? RenderGenres(UnifiedWork w, FieldState field) => work.MeasureSave(w.Id, () => work.Database.Write<(string, string)?>((db, tx) =>
    {
        if (!WorkStore.Valid(db, tx, WorkStore.Find(db, tx, w.Id)!)) return null;
        var values = new List<string>(); var ids = new List<string>();
        foreach (var original in JsonSerializer.Deserialize<string[]>(field.RenderJson)!)
        {
            using var cmd = Command(db, tx, "SELECT v.id,v.text FROM genre_terms t JOIN translation_selections s ON s.source_id=t.source_id JOIN translation_versions v ON v.id=s.translation_id WHERE t.original=$o AND s.language=$l", ("$o", original), ("$l", w.Language));
            string version;
            using (var r = cmd.ExecuteReader()) { if (!r.Read()) return null; version = r.GetString(0); }
            ids.Add(version); values.Add(PersonRenderer.Render(db, tx, version, w.Language));
        }
        var text = JsonSerializer.Serialize(values); var id = Hash(JsonSerializer.Serialize(new { w.SourceId, w.Language, versions = ids, text }));
        // A dictionary reconstruction with identical text must not replace an imported selection.
        if (!w.ForceGeneration)
        {
            using var prior = Command(db, tx, "SELECT v.id,v.text FROM translation_selections s JOIN translation_versions v ON v.id=s.translation_id WHERE s.source_id=$s AND s.language=$l", ("$s", w.SourceId), ("$l", w.Language));
            using var r = prior.ExecuteReader(); if (r.Read() && JsonSerializer.Deserialize<string[]>(r.GetString(1))!.SequenceEqual(values, StringComparer.Ordinal)) { id = r.GetString(0); text = r.GetString(1); }
        }
        Execute(db, tx, "INSERT OR IGNORE INTO translation_versions(id,source_id,language,text,text_hash,origin,provenance_json,created_utc) VALUES($id,$s,$l,$t,$h,'dictionary',$p,$n)",
            ("$id", id), ("$s", w.SourceId), ("$l", w.Language), ("$t", text), ("$h", Hash(text)), ("$p", JsonSerializer.Serialize(new { term_versions = ids })), ("$n", work.Stamp()));
        if (Scalar<string>(db, tx, "SELECT translation_id FROM translation_selections WHERE source_id=$s AND language=$l", ("$s", w.SourceId), ("$l", w.Language)) != id)
            ContentStore.Select(db, tx, w.SourceId, w.Language, id);
        Execute(db, tx, "UPDATE work_items SET translation_id=$t WHERE id=$id", ("$t", id), ("$id", w.Id)); return (id, text);
    }));
    private bool Current(string id) { using var db = work.Database.Open(); return WorkStore.Find(db, null, id) is { } w && WorkStore.Valid(db, null, w); }
    private async Task GenerateAsync(UnifiedWork w, string[]? names, CancellationToken token)
    {
        if (HasOutstandingSend) { work.SetState(w.Id, "waiting", "request", "rate_limit", next: work.UtcNow.AddMilliseconds(200)); return; }
        currentStage = "request_prepare";
        var attempt = requests.Prepare(w, transport.PromptVersion(w.Field, w.Language), names); if (attempt is null) return;
        checkpoint?.Invoke("request.prepared", w);
        var timer = Stopwatch.StartNew(); var sent = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(attempt.Connection.TimeoutMs);
        async Task<GenerationReply> Send()
        {
            try
            {
                return await transport.GenerateAsync(attempt.Input, attempt.Connection, () =>
                {
                    currentStage = "request_send";
                    requests.MarkSending(attempt); sent = true; checkpoint?.Invoke("request.sending", w);
                }, deadline.Token);
            }
            catch (ExecutionSupersededException) { return new(null, "intent_expired_before_send", NotSent: true); }
            catch (SourceTemporarilyUnavailableException) { return new(null, "source_unavailable", NotSent: true); }
            catch (OperationCanceledException) { return new(null, sent ? "network_uncertain" : "request_timeout_before_send", NotSent: !sent, OutcomeUnknown: sent); }
            catch (Exception) { return new(null, sent ? "network_uncertain" : "request_failed_before_send", NotSent: !sent, OutcomeUnknown: sent); }
        }
        var task = Send(); pendingSend = task;
        try
        {
            var reply = await task.WaitAsync(TimeSpan.FromMilliseconds(attempt.Connection.TimeoutMs), token);
            checkpoint?.Invoke("request.received", w);
            currentStage = "response_save";
            requests.Complete(attempt, reply, timer.ElapsedMilliseconds);
            checkpoint?.Invoke("request.committed", w);
        }
        catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException && token.IsCancellationRequested)
        {
            deadline.Cancel();
            requests.Complete(attempt, new(null), timer.ElapsedMilliseconds, interrupted: true);
            pendingSend = SaveLateAsync(task, attempt);
            if (token.IsCancellationRequested) throw;
        }
    }
    private async Task SaveLateAsync(Task<GenerationReply> task, RequestAttempt attempt)
    {
        try { requests.SaveLate(attempt, await task); }
        catch (Exception error) { LastFailure = ExecutionDiagnostic.Capture("late_response_save", attempt.WorkId, error); Volatile.Write(ref backgroundError, "late_history_persistence_failed"); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        if (operation.CurrentCount == 1 && !HasOutstandingSend) { ownership.Dispose(); shutdown.Dispose(); }
        else _ = ReleaseOwnershipAsync();
    }
    private async Task ReleaseOwnershipAsync()
    {
        await operation.WaitAsync();
        try { if (pendingSend is { } task) await task; }
        finally { ownership.Dispose(); shutdown.Dispose(); operation.Release(); }
    }
}
