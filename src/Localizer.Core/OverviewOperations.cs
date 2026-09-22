using System.Text.Json;

namespace Localizer.Core;

public sealed record OverviewTargetSnapshot(string Text, string SourceHash, bool Writable);
public sealed record OverviewWrite(MediaKey Key, string Expected, string Proposed, string SourceHash);
public interface IOverviewTarget
{
    ValueTask<OverviewTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token);
    ValueTask<TargetWriteResult> WriteAsync(OverviewWrite request, CancellationToken token);
}
public sealed record OverviewOperation(string Id, string Fingerprint, MediaKey Key, string State, string Before,
    string Proposed, string SourceHash, string? ParentId, string? CandidateId);

/// <summary>Durable single-item intent. An interrupted write requires observation before another write.</summary>
public sealed class OverviewOperations(string root, OverviewStore store, IOverviewTarget target)
{
    private string DirectoryPath => Path.Combine(root, "overview-operations");
    private FileStream Lease()
    {
        Directory.CreateDirectory(DirectoryPath);
        try { return new FileStream(Path.Combine(DirectoryPath, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new OperationBusyException(); }
    }
    public OverviewOperation[] List(MediaKey key) => !Directory.Exists(DirectoryPath) ? [] :
        Directory.EnumerateFiles(DirectoryPath, "*.json").Select(path => JsonSerializer.Deserialize<OverviewOperation>(File.ReadAllText(path))!)
            .Where(x => x.Key == key).ToArray();
    public OverviewOperation Read(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException();
        return JsonSerializer.Deserialize<OverviewOperation>(File.ReadAllText(Path.Combine(DirectoryPath, id.ToString("N") + ".json")))!;
    }
    private void Save(OverviewOperation op)
    {
        var path = Path.Combine(DirectoryPath, op.Id + ".json"); var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, op); stream.Flush(true); }
        File.Move(temp, path, true);
    }
    private OverviewOperation? Existing(Guid id, string fingerprint)
    {
        if (id == Guid.Empty) throw new ArgumentException();
        if (!File.Exists(Path.Combine(DirectoryPath, id.ToString("N") + ".json"))) return null;
        var op = Read(id);
        if (op.Fingerprint != fingerprint) throw new IdempotencyConflictException();
        return op;
    }
    private void NotBusy(MediaKey key)
    {
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            var op = JsonSerializer.Deserialize<OverviewOperation>(File.ReadAllText(path))!;
            if (op.Key == key && op.State is "Prepared" or "Writing") throw new OperationBusyException();
        }
    }
    public async Task<OverviewOperation> ApplyAsync(Guid id, MediaKey key, long revision, string candidateId,
        string expectedDisplay, CancellationToken token = default)
    {
        using var lease = Lease();
        var fingerprint = Identity.Hash(JsonSerializer.Serialize(new { key, revision, candidateId, expectedDisplay, Kind = "apply" }));
        if (Existing(id, fingerprint) is { } existing) return existing;
        NotBusy(key);
        var state = store.Read(key);
        if (state.Revision != revision) throw new RevisionConflictException();
        var candidate = state.Candidates.SingleOrDefault(x => x.Id == candidateId) ?? throw new KeyNotFoundException();
        if (candidate.SourceVersion != state.Source?.Version || state.Selected(candidate.Language)?.Id != candidateId)
            throw new SourceChangedException();
        return await Start(new(id.ToString("N"), fingerprint, key, "Prepared", expectedDisplay, candidate.Text,
            state.Source.Hash, null, candidateId), token);
    }
    public async Task<OverviewOperation> ApplyOriginalAsync(Guid id, MediaKey key, long revision,
        string expectedDisplay, CancellationToken token = default)
    {
        key.Validate();
        using var lease = Lease();
        var fingerprint = Identity.Hash(JsonSerializer.Serialize(new { key, revision, expectedDisplay, Kind = "original" }));
        if (Existing(id, fingerprint) is { } existing) return existing;
        NotBusy(key);
        var state = store.Read(key);
        if (state.Revision != revision) throw new RevisionConflictException();
        var source = state.Source ?? throw new SourceChangedException();
        if (string.IsNullOrWhiteSpace(source.Text)) throw new OperationValidationException("overview_empty");
        return await Start(new(id.ToString("N"), fingerprint, key, "Prepared", expectedDisplay, source.Text,
            source.Hash, null, null), token);
    }
    public async Task<OverviewOperation> RestoreAsync(Guid id, MediaKey key, Guid applicationId,
        bool acceptObserved = false, CancellationToken token = default)
    {
        using var lease = Lease();
        var fingerprint = Identity.Hash(JsonSerializer.Serialize(new { key, applicationId, acceptObserved, Kind = "restore" }));
        if (Existing(id, fingerprint) is { } existing) return existing;
        NotBusy(key);
        var parent = Read(applicationId);
        if (parent.Key != key || parent.ParentId is not null || parent.State != "Applied"
            && !(acceptObserved && parent.State == "ObservedApplied")) throw new OperationValidationException("overview_restore_unavailable");
        if (Directory.EnumerateFiles(DirectoryPath, "*.json").Select(path =>
            JsonSerializer.Deserialize<OverviewOperation>(File.ReadAllText(path))!).Any(x => x.ParentId == parent.Id
                && x.State is "Applied" or "ObservedApplied" or "NoChange"))
            throw new OperationValidationException("overview_restore_consumed");
        return await Start(new(id.ToString("N"), fingerprint, key, "Prepared", parent.Proposed, parent.Before,
            parent.SourceHash, parent.Id, null), token);
    }
    private async Task<OverviewOperation> Start(OverviewOperation op, CancellationToken token)
    {
        var live = await target.ReadAsync(op.Key, token);
        if (live is null || !live.Writable) op = op with { State = "Blocked" };
        else if (live.SourceHash != op.SourceHash || live.Text != op.Before) op = op with { State = "Conflict" };
        else if (op.Before == op.Proposed) op = op with { State = "NoChange" };
        Save(op);
        if (op.State != "Prepared") return op;
        token.ThrowIfCancellationRequested();
        op = op with { State = "Writing" }; Save(op);
        // Any exception leaves Writing durable: do not guess whether Jellyfin persisted the update.
        var result = await target.WriteAsync(new(op.Key, op.Before, op.Proposed, op.SourceHash), token);
        op = op with { State = result switch { TargetWriteResult.Written => "Applied",
            TargetWriteResult.AlreadyMatches => "ObservedApplied", TargetWriteResult.Blocked => "Blocked", _ => "Conflict" } };
        Save(op); return op;
    }
    public async Task<OverviewOperation> RecoverAsync(Guid id, MediaKey key, CancellationToken token = default)
    {
        using var lease = Lease(); var op = Read(id);
        if (op.Key != key) throw new KeyNotFoundException();
        if (op.State is not ("Prepared" or "Writing")) return op;
        var live = await target.ReadAsync(key, token);
        if (live is null) return op; // Cannot observe: retain the unresolved write lock.
        op = op with { State = live.SourceHash != op.SourceHash ? "Conflict" : live.Text == op.Proposed
            ? "ObservedApplied" : live.Text == op.Before ? "NotApplied" : "Conflict" };
        Save(op); return op;
    }
}
