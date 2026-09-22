using System.Text.Json;
using Localizer.Core;

var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
var results = new List<object>(); var failed = 0;
void Expect(bool ok) { if (!ok) throw new InvalidOperationException("Assertion failed"); }
async Task Throws<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
async Task Check(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + " " + e); }
}
Fixture New() => new(Path.Combine(root, Guid.NewGuid().ToString("N")));
await Check("frozen_batch_reopens_without_writes_and_rejects_id_reuse", async () => {
    var f = New(); var b = f.Batch();
    Expect(f.Batches.Get(b.Id).Rows.Length == 4 && f.Names.Writes + f.Genres.Writes == 0);
    Expect(f.Batches.Create(b).Id == b.Id);
    await Throws<IdempotencyConflictException>(() => Task.FromResult(f.Batches.Create(b with { RequestFingerprint = "different" })));
    Expect(f.Batches.List(f.Scope).Count == 1 && f.Batches.List(new("other", "scope")).Count == 0);
});
await Check("serial_fields_and_repeated_run_do_not_duplicate_writes", async () => {
    var f = New(); var b = f.Batch(); await f.Queue.RunAsync(b.Id, true);
    Expect(f.Batches.Get(b.Id).Rows.All(x => x.State == BatchRowState.Applied) && f.Names.Writes == 2 && f.Genres.Writes == 2);
    await f.Queue.RunAsync(b.Id, true); Expect(f.Names.Writes == 2 && f.Genres.Writes == 2);
});
await Check("candidate_change_blocks_only_affected_field", async () => {
    var f = New(); var b = f.Batch(); var candidate = f.Store.GetCandidate(b.Rows[0].Name!.CandidateId!);
    f.Store.Edit(candidate.Id, candidate.Revision, "edited after preview");
    await f.Queue.RunAsync(b.Id, true); var done = f.Batches.Get(b.Id);
    Expect(done.State == BatchState.Completed && done.Rows[0].State == BatchRowState.Blocked && done.Rows.Skip(1).All(x => x.State == BatchRowState.Applied));
});
await Check("external_conflict_keeps_manual_value_and_continues_other_fields", async () => {
    var f = New(); var b = f.Batch(); f.Names.Values[f.Keys[0]] = "external";
    await f.Queue.RunAsync(b.Id, true);
    Expect(f.Batches.Get(b.Id).Rows[0].State == BatchRowState.Conflict && f.Names.Values[f.Keys[0]] == "external" && f.Genres.Writes == 2);
});
await Check("cancel_inflight_finishes_current_field_then_stops_and_resumes", async () => {
    var f = New(); var b = f.Batch(); f.Names.Hold = true;
    var run = f.Queue.RunAsync(b.Id, true); await f.Names.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    f.Batches.Cancel(b.Id); f.Names.Release.TrySetResult(); await run;
    Expect(f.Batches.Get(b.Id).State == BatchState.Cancelled && f.Names.Writes == 1 && f.Genres.Writes == 0);
    f.Batches.Change(b.Id, x => x with { CancelRequested = false }); await f.Queue.RunAsync(b.Id, true);
    Expect(f.Names.Writes == 2 && f.Genres.Writes == 2 && f.Batches.Get(b.Id).State == BatchState.Completed);
});
await Check("cancel_between_dispatch_and_start_is_not_lost", async () => {
    var f = New(); var b = f.Batch(); f.Batches.Change(b.Id, x => x with { State = BatchState.Running });
    f.Batches.Cancel(b.Id); await f.Queue.RunAsync(b.Id, true);
    Expect(f.Batches.Get(b.Id).State == BatchState.Cancelled && f.Names.Writes + f.Genres.Writes == 0);
});
await Check("dispatcher_lease_prevents_parallel_batch_writers", async () => {
    var f = New(); var b = f.Batch(); var second = f.Batch(); f.Names.Hold = true;
    var run = f.Queue.RunAsync(b.Id, true); await f.Names.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Throws<OperationBusyException>(() => f.Queue.RunAsync(second.Id, true));
    f.Names.Release.TrySetResult(); await run; Expect(f.Names.Writes == 2);
});
await Check("write_before_batch_bookkeeping_reconciles_without_rewrite", async () => {
    var f = New(); var b = f.Batch(); var row = b.Rows[0];
    f.Batches.Change(b.Id, x => x with { State = BatchState.Running, Rows = x.Rows.Select((r,i) => i == 0 ? r with { State = BatchRowState.Working } : r).ToArray() });
    var service = new NameOperationService(f.Store, f.Names, (stage,_) => { if (stage == OperationCheckpoint.BeforeCompletion) throw new InvalidOperationException("simulated interruption"); });
    await Throws<InvalidOperationException>(() => service.ApplyAsync(row.OperationId, f.Scope, row.Name!));
    var reopened = new WriteBatchStore(f.Directory); var queue = new WriteBatchQueue(reopened, f.Store, f.Names, f.Genres);
    await queue.RunAsync(b.Id, false, true);
    Expect(reopened.Get(b.Id).Rows[0].State == BatchRowState.ObservedExpected && f.Names.Writes == 1 && f.Genres.Writes == 0);
    await queue.RunAsync(b.Id, true); Expect(f.Names.Writes == 2 && f.Genres.Writes == 2);
});
await Check("expiry_skips_unstarted_fields_without_mutating_metadata", async () => {
    var f = New(); var b = f.Batch(); f.Batches.Change(b.Id, x => x with { CreatedUtc = DateTimeOffset.UtcNow.AddDays(-2) });
    await f.Queue.RunAsync(b.Id, true);
    Expect(f.Batches.Get(b.Id).Rows.All(x => x.State == BatchRowState.Skipped && x.ErrorCategory == "PreviewExpired") && f.Names.Writes + f.Genres.Writes == 0);
});
await Check("unknown_genres_require_acceptance_before_any_field_write", async () => {
    var f = New(); f.Store.ObserveGenreSource(f.Keys[0], ["unknown"]); var b = f.Batch();
    await Throws<OperationValidationException>(() => f.Queue.RunAsync(b.Id, false)); Expect(f.Names.Writes + f.Genres.Writes == 0);
    await f.Queue.RunAsync(b.Id, true); Expect(f.Genres.Values[f.Keys[0]].Contains("unknown"));
});
await Check("busy_field_writer_pauses_without_skipping_or_retry_loop", async () => {
    var f = New(); var b = f.Batch();
    using (var held = new FileStream(f.Database + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    { await f.Queue.RunAsync(b.Id, true); Expect(f.Batches.Get(b.Id).State == BatchState.Paused && f.Batches.Get(b.Id).Rows[0].State == BatchRowState.Pending && f.Names.Writes == 0); }
    await f.Queue.RunAsync(b.Id, true); Expect(f.Names.Writes == 2);
});
await Check("host_cancellation_preserves_inflight_journal_for_explicit_recovery", async () => {
    var f = New(); var b = f.Batch(); f.Names.Hold = true; using var cancel = new CancellationTokenSource();
    var run = f.Queue.RunAsync(b.Id, true, false, cancel.Token); await f.Names.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
    await run; Expect(f.Names.Writes == 0 && f.Batches.Get(b.Id).Rows[0].State == BatchRowState.NeedsReview);
    f.Names.Release.TrySetResult(); await f.Queue.RunAsync(b.Id, true); Expect(f.Names.Writes == 2 && f.Genres.Writes == 2);
});
File.WriteAllText(args[1], JsonSerializer.Serialize(new { total = results.Count, passed = results.Count - failed, failed, checks = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failed}/{results.Count} batch checks passed."); return failed == 0 ? 0 : 1;

sealed class Fixture
{
    public string Directory { get; } public string Database { get; }
    public CandidateStore Store { get; } public WriteBatchStore Batches { get; }
    public FakeNames Names { get; } = new(); public FakeGenres Genres { get; } = new();
    public LibraryKey Scope { get; } = new("server", "library");
    public MediaKey[] Keys { get; } = [new("server", "library", "one"), new("server", "library", "two")];
    public WriteBatchQueue Queue => new(Batches, Store, Names, Genres);
    public Fixture(string directory)
    {
        Directory = Path.Combine(directory, "batches"); Database = Path.Combine(directory, "candidates.db");
        Store = new(Database); Batches = new(Directory);
        foreach (var key in Keys)
        {
            Names.Values[key] = "original"; Genres.Values[key] = ["ドラマ"];
            var source = Store.ObserveSource(key, "original", "");
            var candidate = Store.SaveGenerated(source.Id, TargetLanguage.SimplifiedChinese, "译文", new("manual", "human", "v1", "none", "{}", "{}", "manual"));
            Store.Approve(candidate.Id, candidate.Revision); Store.ObserveGenreSource(key, ["ドラマ"]);
        }
    }
    public WriteBatch Batch()
    {
        var rows = new List<BatchRow>();
        foreach (var key in Keys)
        {
            var name = new LanguagePreview(Store).Build(Scope, TargetLanguage.SimplifiedChinese, [Names.Snapshot(key).Movie]).Single();
            rows.Add(new(rows.Count, key, "synthetic", BatchField.Name, Guid.NewGuid().ToString("N"), name, null, BatchRowState.Pending, null));
            var genre = Store.BuildGenrePreview(Scope, TargetLanguage.SimplifiedChinese, Genres.Snapshot(key));
            rows.Add(new(rows.Count, key, "synthetic", BatchField.Genres, Guid.NewGuid().ToString("N"), null, genre, BatchRowState.Pending, null));
        }
        return Batches.Create(new(Guid.NewGuid().ToString("N"), "request", Scope, TargetLanguage.SimplifiedChinese, DateTimeOffset.UtcNow, BatchState.Ready, false, rows.ToArray()));
    }
}
sealed class FakeNames : INameTarget
{
    public Dictionary<MediaKey, string> Values { get; } = new(); public int Writes; public bool Hold;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public NameTargetSnapshot Snapshot(MediaKey key) => new(new(key, true, false, "original", "", Values[key]), true);
    public ValueTask<NameTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token) => ValueTask.FromResult<NameTargetSnapshot?>(Snapshot(key));
    public async ValueTask<TargetWriteResult> WriteAsync(NameWriteRequest request, CancellationToken token)
    {
        if (Hold) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
        if (Values[request.Key] != request.ExpectedName) return TargetWriteResult.Conflict;
        Values[request.Key] = request.ProposedName; Writes++; return TargetWriteResult.Written;
    }
}
sealed class FakeGenres : IGenreTarget
{
    public Dictionary<MediaKey, string[]> Values { get; } = new(); public int Writes;
    public GenreTargetSnapshot Snapshot(MediaKey key) => new(key, true, false, true, Values[key]);
    public ValueTask<GenreTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token) => ValueTask.FromResult<GenreTargetSnapshot?>(Snapshot(key));
    public ValueTask<TargetWriteResult> WriteAsync(GenreWriteRequest request, CancellationToken token)
    {
        if (!GenreValues.Equal(Values[request.Key], request.ExpectedValues)) return ValueTask.FromResult(TargetWriteResult.Conflict);
        Values[request.Key] = request.ProposedValues; Writes++; return ValueTask.FromResult(TargetWriteResult.Written);
    }
}
