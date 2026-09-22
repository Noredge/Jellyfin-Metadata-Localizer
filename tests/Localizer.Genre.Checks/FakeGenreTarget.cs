using Localizer.Core;

public sealed class FakeGenreTarget(GenreTargetSnapshot initial) : IGenreTarget
{
    public GenreTargetSnapshot Current { get; set; } = initial;
    public int Writes { get; private set; }
    public string Mode { get; set; } = "normal";
    public bool ReadFails { get; set; }
    public Action? BeforeWrite { get; set; }
    public ValueTask<GenreTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ReadFails) throw new IOException("synthetic read failure");
        return ValueTask.FromResult<GenreTargetSnapshot?>(Current with { Values = Current.Values.ToArray() });
    }
    public ValueTask<TargetWriteResult> WriteAsync(GenreWriteRequest request, CancellationToken token)
    {
        BeforeWrite?.Invoke();
        if (Current.Locked || !Current.MetadataSaversExplicitlyDisabled || Current.Key != request.Key || !Current.IsMovie)
            return ValueTask.FromResult(TargetWriteResult.Blocked);
        if (!GenreValues.Equal(Current.Values, request.ExpectedValues)) return ValueTask.FromResult(TargetWriteResult.Conflict);
        if (Mode == "throw-before") throw new IOException("synthetic before effect");
        if (Mode != "no-effect") { Current = Current with { Values = request.ProposedValues.ToArray() }; Writes++; }
        if (Mode == "throw-after") throw new IOException("synthetic after effect");
        if (Mode == "third") { Current = Current with { Values = ["external third value"] }; throw new IOException(); }
        if (Mode == "unreadable") ReadFails = true;
        return ValueTask.FromResult(TargetWriteResult.Written);
    }
}

public sealed class ReadOnlyNameFixture(NameTargetSnapshot snapshot) : INameTarget
{
    public ValueTask<NameTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token) => ValueTask.FromResult<NameTargetSnapshot?>(snapshot);
    public ValueTask<TargetWriteResult> WriteAsync(NameWriteRequest request, CancellationToken token) => throw new InvalidOperationException("Fixture must not write.");
}
