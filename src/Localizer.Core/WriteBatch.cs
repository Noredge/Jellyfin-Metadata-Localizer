using System.Text.Json;

namespace Localizer.Core;

public enum BatchField { Name, Genres }
public enum BatchState { Ready, Running, Paused, Cancelled, Completed }
public enum BatchRowState { Pending, Working, Applied, ObservedExpected, NoChange, Skipped, Conflict, Blocked, NeedsReview }
public sealed record BatchRow(int Ordinal, MediaKey Key, string DisplayName, BatchField Field, string OperationId,
    LanguagePreviewEntry? Name, GenrePreviewEntry? Genres, BatchRowState State, string? ErrorCategory);
public sealed record WriteBatch(string Id, string RequestFingerprint, LibraryKey Scope, TargetLanguage Language,
    DateTimeOffset CreatedUtc, BatchState State, bool CancelRequested, BatchRow[] Rows);

// Small immutable previews plus mutable dispatch progress, separate from authoritative field journals.
public sealed class WriteBatchStore(string directory)
{
    private static readonly object Gate = new();
    private string FilePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var parsed) || parsed == Guid.Empty) throw new ArgumentException("Invalid batch id.");
        return Path.Combine(directory, id + ".json");
    }
    public WriteBatch? Find(string id)
    {
        lock (Gate) { var path = FilePath(id); return File.Exists(path) ? JsonSerializer.Deserialize<WriteBatch>(File.ReadAllText(path))! : null; }
    }
    public WriteBatch Get(string id) => Find(id) ?? throw new KeyNotFoundException();
    public WriteBatch Create(WriteBatch batch)
    {
        lock (Gate)
        {
            var old = Find(batch.Id);
            if (old is not null) return old.RequestFingerprint == batch.RequestFingerprint ? old : throw new IdempotencyConflictException();
            if (batch.Rows.Length is < 1 or > 40 || batch.Rows.Select(x => (x.Key, x.Field)).Distinct().Count() != batch.Rows.Length
                || batch.Rows.Any(x => x.Key.ServerId != batch.Scope.ServerId || x.Key.LibraryId != batch.Scope.LibraryId)) throw new ArgumentException();
            Save(batch); return batch;
        }
    }
    public WriteBatch Change(string id, Func<WriteBatch, WriteBatch> update)
    {
        lock (Gate) { var result = update(Get(id)); Save(result); return result; }
    }
    private void Save(WriteBatch value)
    {
        Directory.CreateDirectory(directory); var path = FilePath(value.Id);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    public IReadOnlyList<WriteBatch> List(LibraryKey scope)
    {
        lock (Gate)
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(directory, "*.json").Select(path => JsonSerializer.Deserialize<WriteBatch>(File.ReadAllText(path))!)
                .Where(x => x.Scope == scope).OrderByDescending(x => x.CreatedUtc).Take(20).ToArray();
        }
    }
    internal IDisposable Lease()
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "dispatch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new OperationBusyException(); }
    }
    public WriteBatch Cancel(string id) => Change(id, x => x.State == BatchState.Completed ? x :
        x with { CancelRequested = true, State = x.State == BatchState.Running ? x.State : BatchState.Cancelled });
}

public sealed class WriteBatchQueue(WriteBatchStore batches, CandidateStore candidates, INameTarget names, IGenreTarget genres)
{
    private static bool Done(BatchRow x) => x.State is not (BatchRowState.Pending or BatchRowState.Working or BatchRowState.NeedsReview);
    private void Row(string id, int ordinal, BatchRowState state, string? error) => batches.Change(id, x => x with
        { Rows = x.Rows.Select(r => r.Ordinal == ordinal ? r with { State = state, ErrorCategory = error } : r).ToArray() });
    public async Task RunAsync(string id, bool acceptUnknown, bool observeOnly = false, CancellationToken token = default)
    {
        using var lease = batches.Lease();
        var batch = batches.Get(id);
        if (!observeOnly && !acceptUnknown && batch.Rows.Any(x => !Done(x) && x.Genres?.UnknownValues.Length > 0))
            throw new OperationValidationException("UnknownGenresNeedReview");
        batches.Change(id, x => x with { State = BatchState.Running });
        try
        {
            foreach (var original in batch.Rows)
            {
                token.ThrowIfCancellationRequested();
                if (batches.Get(id).CancelRequested && !observeOnly) break;
                var row = batches.Get(id).Rows.Single(x => x.Ordinal == original.Ordinal);
                if (Done(row)) continue;
                var existing = row.Field == BatchField.Name ? candidates.FindOperation(row.OperationId)?.State : candidates.FindGenreOperation(row.OperationId)?.State;
                if (observeOnly && existing is null) continue;
                if (!observeOnly && existing is null && batch.CreatedUtc < DateTimeOffset.UtcNow.AddHours(-24))
                { Row(id, row.Ordinal, BatchRowState.Skipped, "PreviewExpired"); continue; }
                Row(id, row.Ordinal, BatchRowState.Working, null);
                try
                {
                    OperationState state; string? error;
                    if (row.Field == BatchField.Name)
                    {
                        var service = new NameOperationService(candidates, names);
                        var op = existing is null ? await service.ApplyAsync(row.OperationId, batch.Scope, row.Name!, token)
                            : observeOnly ? await service.ReconcileAsync(row.OperationId, token)
                            : await service.ResumeAsync(row.OperationId, batch.Scope, token);
                        state = op.State; error = op.ErrorCategory;
                    }
                    else
                    {
                        var service = new GenreOperationService(candidates, genres);
                        var op = existing is null ? await service.ApplyAsync(row.OperationId, batch.Scope, row.Genres!, token)
                            : observeOnly ? await service.ReconcileAsync(row.OperationId, token)
                            : await service.ResumeAsync(row.OperationId, batch.Scope, token);
                        state = op.State; error = op.ErrorCategory;
                    }
                    var mapped = state switch
                    {
                        OperationState.Applied => BatchRowState.Applied, OperationState.ObservedExpected => BatchRowState.ObservedExpected,
                        OperationState.NoChange => BatchRowState.NoChange, OperationState.Conflict => BatchRowState.Conflict,
                        OperationState.Blocked => BatchRowState.Blocked, OperationState.Cancelled => BatchRowState.Skipped,
                        _ => BatchRowState.NeedsReview
                    };
                    Row(id, row.Ordinal, mapped, error);
                    if (mapped == BatchRowState.NeedsReview && !observeOnly) break;
                }
                catch (OperationBusyException) { Row(id, row.Ordinal, existing is null ? BatchRowState.Pending : BatchRowState.NeedsReview, "WriterBusy"); break; }
                catch (OperationValidationException error) { Row(id, row.Ordinal, BatchRowState.Blocked, error.Category); }
                catch (Exception) when (!token.IsCancellationRequested)
                { Row(id, row.Ordinal, BatchRowState.NeedsReview, "CheckFieldJournal"); break; }
            }
        }
        finally
        {
            // A crash can leave Working; the field operation ID is durable before any target call.
            batches.Change(id, x => x with { State = x.Rows.All(Done) ? BatchState.Completed : x.CancelRequested ? BatchState.Cancelled : BatchState.Paused });
        }
    }
}
