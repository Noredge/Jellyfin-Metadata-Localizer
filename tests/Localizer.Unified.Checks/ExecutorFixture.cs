using System.Text.Json;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal sealed class ExecutorFixture : IDisposable
{
    internal string Root { get; }
    internal UnifiedDatabase Database { get; }
    internal ContentStore Content { get; }
    internal SettingsStore Settings { get; }
    internal WorkStore Work { get; }
    internal SourceObserver Observer { get; }
    internal FakeUnifiedHost Host { get; } = new();
    internal FakeUnifiedTransport Transport { get; } = new();
    internal UnifiedExecutor Executor { get; private set; }
    internal DateTimeOffset Clock = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    internal string Library { get; } = Guid.NewGuid().ToString("N");
    internal ExecutorFixture(string root, int timeout = 5000, int rpm = 1000)
    {
        Root = root; Database = new(root, create: true); Content = new(Database); Settings = new(Database);
        Settings.Save(1, "zh-Hans", new(true, true, true), [Library], true);
        Settings.SaveConnection(0, "https://example.invalid/v1", "fake-model", timeout, rpm, Id(), "{}"); Settings.EstablishBaseline();
        Work = new(Database, () => Clock); Observer = new(Work, Host, Host); Executor = new(Work, Host, Transport);
    }
    internal string Add(string alias, string overview = "山を歩く。", params string[] genres)
    {
        var item = AddHostOnly(alias, overview, genres); var key = item.Key; var path = item.MediaPath;
        var media = Content.RegisterMedia(key, path, "{}");
        foreach (var field in new[] { "Name", "Overview", "Genres" }) Observe(media, field);
        return media;
    }
    internal HostMedia AddHostOnly(string alias, string overview = "山を歩く。", params string[] genres)
    {
        var key = new MediaIdentity("synthetic-server", Library, Id()); var path = "C:\\fixture\\" + alias + ".mp4";
        Host.Add(new(key, path, "Name", "山の旅 " + alias, "LAB · 山の旅 " + alias, "LAB · "));
        Host.Add(new(key, path, "Overview", overview, overview));
        var values = JsonSerializer.Serialize(genres); Host.Add(new(key, path, "Genres", values, values));
        return new(key, path);
    }
    internal FieldState Observe(string media, string field)
    {
        var item = Host.Get(Key(media), field); return Content.Observe(media, field, item.Original!, item.EvidenceJson, item.Prefix, item.Display);
    }
    internal MediaIdentity Key(string media)
    {
        using var db = Database.Open(); using var cmd = Command(db, null, "SELECT server_id,library_id,item_id FROM media_items WHERE id=$m", ("$m", media));
        using var r = cmd.ExecuteReader(); r.Read(); return new(r.GetString(0), r.GetString(1), r.GetString(2));
    }
    internal WorkSeed Seed(string media, string field) => new(Content.ReadField(media, field)!, Host.Get(Key(media), field).Display);
    internal BatchReceipt Submit(string[] media, string[]? fields = null, bool force = false, string operation = "sync") =>
        // General executor fixtures exercise synchronization; missing-only fill has its own admission checks.
        Work.Submit(Id(), force ? "retranslate" : "manual", Settings.Read().Revision,
            media.SelectMany(m => (fields ?? ["Name", "Overview", "Genres"]).Select(f => Seed(m, f))).ToArray(), force, operation);
    internal async Task Drain(int limit = 300)
    {
        for (var i = 0; i < limit; i++) if (!await Executor.RunNextAsync()) return;
        throw new InvalidOperationException("Executor did not become idle");
    }
    internal long Count(string sql) { using var db = Database.Open(); return Scalar<long>(db, null, sql); }
    internal string? Text(string sql) { using var db = Database.Open(); return Scalar<string>(db, null, sql); }
    internal void Restart() { Executor.Dispose(); Executor = new(Work, Host, Transport); }
    public void Dispose() { Executor.Dispose(); Database.ReleaseReadPool(); }
}

internal sealed class FakeUnifiedHost : IUnifiedHost, IUnifiedInventory, IUnifiedObservationHints
{
    private readonly Dictionary<(string, string), HostFieldObservation> rows = [];
    internal int Writes;
    internal bool HintsEnabled;
    internal int FieldReads;
    public ValueTask<string?> ObservationFingerprintAsync(HostMedia item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(HintsEnabled ? Hash(JsonSerializer.Serialize(new[] { "Name", "Overview", "Genres" }.Select(x => Get(item.Key, x)))) : null);
    }
    internal Func<HostWriteCommand, Task>? BeforeWrite { get; set; }
    internal Action<HostWriteCommand>? AfterWrite { get; set; }
    internal Func<HostFieldObservation, Task>? BeforeReadReturn { get; set; }
    internal void Add(HostFieldObservation field) => rows[(field.Key.ItemId, field.Field)] = field;
    internal HostFieldObservation Get(MediaIdentity key, string field) => rows[(key.ItemId, field)];
    internal void Change(MediaIdentity key, string field, Func<HostFieldObservation, HostFieldObservation> change) => Add(change(Get(key, field)));
    public async Task<HostFieldObservation> ReadAsync(MediaIdentity key, string field, CancellationToken token)
    { token.ThrowIfCancellationRequested(); FieldReads++; var snapshot = Get(key, field); if (BeforeReadReturn is not null) await BeforeReadReturn(snapshot); return snapshot; }
    public Task<HostMedia[]> ListAsync(string[] libraryIds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(rows.Values.Where(x => x.Field == "Name" && x.Present && libraryIds.Contains(x.Key.LibraryId, StringComparer.Ordinal))
            .Select(x => new HostMedia(x.Key, x.MediaPath, x.EvidenceJson)).ToArray());
    }
    public async Task<HostWriteOutcome> WriteAsync(HostWriteCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (BeforeWrite is not null) await BeforeWrite(command);
        var current = Get(command.Key, command.Field);
        if (!current.Present || !current.Writable || current.SourceError is not null || !command.AuthorityStillValid()) return HostWriteOutcome.Blocked;
        if (!UnifiedExecutor.SameOriginal(command.Field, current.Original!, command.Original) || current.Prefix != command.Prefix || current.MediaPath != command.MediaPath) return HostWriteOutcome.Conflict;
        if (Equal(command.Field, current.Display, command.TargetDisplay)) return HostWriteOutcome.AlreadyMatches;
        if (!Equal(command.Field, current.Display, command.ExpectedDisplay)) return HostWriteOutcome.Conflict;
        Add(current with { Display = command.TargetDisplay }); Writes++; AfterWrite?.Invoke(command); return HostWriteOutcome.Written;
    }
    internal static bool Equal(string field, string a, string b) => field == "Genres" ? JsonSerializer.Deserialize<string[]>(a)!.SequenceEqual(JsonSerializer.Deserialize<string[]>(b)!) : a == b;
}

internal sealed class FakeUnifiedTransport : IUnifiedTransport
{
    internal string Version = "fake-v1";
    internal int Calls, Active, MaximumActive;
    internal readonly List<GenerationInput> Inputs = [];
    internal Func<GenerationInput, Action, CancellationToken, Task<GenerationReply>>? Handler { get; set; }
    public string PromptVersion(string field, string language) => Version;
    public async Task<GenerationReply> GenerateAsync(GenerationInput input, ConnectionVersion connection, Action markSending, CancellationToken token)
    {
        void Send() { markSending(); Calls++; Inputs.Add(input); Active++; MaximumActive = Math.Max(MaximumActive, Active); }
        try
        {
            if (Handler is not null) return await Handler(input, Send, token);
            Send(); return Reply(input);
        }
        finally { if (Active > 0) Active--; }
    }
    internal static GenerationReply Reply(GenerationInput input) => new((input.Language == "en" ? "English " : "中文 ") + input.Text,
        HttpStatus: 200, Diagnostics: new("content", "accepted", 20, Hash(input.Text), "stop"), Usage: new(2, 3, 5));
}
