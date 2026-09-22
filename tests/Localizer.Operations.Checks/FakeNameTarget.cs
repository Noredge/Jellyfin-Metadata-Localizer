using System.Text.Json;
using Localizer.Core;

enum WriteMode { Normal, ThrowBefore, ThrowAfter, ReturnWithoutEffect, ThirdValueThenThrow }
sealed record TargetFile(NameTargetSnapshot? Snapshot, int WriteCalls, int Changes, string UnrelatedValue);

// The fixture compares and writes atomically in its own process; this is NOT a Jellyfin capability claim.
sealed class FakeNameTarget : INameTarget
{
    private readonly string path;
    private TargetFile state;
    public WriteMode Mode { get; set; }
    public bool FailReads { get; set; }
    public Action? BeforeWrite { get; set; }
    public int Writes => state.WriteCalls;
    public int Changes => state.Changes;
    public string Unrelated => state.UnrelatedValue;
    public NameTargetSnapshot? Snapshot { get => state.Snapshot; set { state=state with { Snapshot=value }; Save(); } }
    public FakeNameTarget(string path, NameTargetSnapshot? initial=null)
    {
        this.path=path;
        state=File.Exists(path) ? JsonSerializer.Deserialize<TargetFile>(File.ReadAllText(path))! : new(initial,0,0,"genres-order-and-provider-ids-unchanged");
        Save();
    }
    public void SetName(string value) => Snapshot=Snapshot! with { Movie=Snapshot!.Movie with { CurrentName=value } };
    public ValueTask<NameTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (FailReads) throw new IOException("Synthetic read failure.");
        return ValueTask.FromResult(Snapshot);
    }
    public ValueTask<TargetWriteResult> WriteAsync(NameWriteRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        state=state with { WriteCalls=state.WriteCalls+1 }; Save();
        BeforeWrite?.Invoke();
        var live=Snapshot;
        if (live is null || live.Movie.Key!=request.Key || !live.Movie.IsMovie || live.Movie.NameLocked ||
            !live.MetadataSaversExplicitlyDisabled || live.Movie.OriginalTitle!=request.ExpectedOriginalTitle ||
            live.Movie.DisplayPrefix!=request.ExpectedDisplayPrefix) return ValueTask.FromResult(TargetWriteResult.Blocked);
        if (live.Movie.CurrentName==request.ProposedName) return ValueTask.FromResult(TargetWriteResult.AlreadyMatches);
        if (live.Movie.CurrentName!=request.ExpectedName) return ValueTask.FromResult(TargetWriteResult.Conflict);
        if (Mode==WriteMode.ThrowBefore) throw new TimeoutException("Synthetic failure before write.");
        if (Mode==WriteMode.ReturnWithoutEffect) return ValueTask.FromResult(TargetWriteResult.Written);
        state=state with { Changes=state.Changes+1 }; Save();
        SetName(Mode==WriteMode.ThirdValueThenThrow ? "third value" : request.ProposedName);
        if (Mode is WriteMode.ThrowAfter or WriteMode.ThirdValueThenThrow) throw new TimeoutException("Synthetic response lost.");
        return ValueTask.FromResult(TargetWriteResult.Written);
    }
    private void Save() => File.WriteAllText(path,JsonSerializer.Serialize(state));
}

sealed class SimulatedCrashException : Exception;
