using System.Text.Json;
using System.Collections.Concurrent;
using Localizer.Jellyfin;
using Localizer.Translation;
using Localizer.Unified;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Plugin;

public sealed record UnifiedHealth(string State, string? Error, DateTimeOffset UpdatedUtc);

/// <summary>Composition and event notification only. Business state belongs to the unified database.</summary>
public sealed class UnifiedRuntime : IDisposable
{
    private readonly object gate = new();
    private readonly ILibraryManager library;
    private readonly IApplicationHost application;
    private readonly IUnifiedTransport transport;
    private UnifiedHealth health = new("Starting", null, DateTimeOffset.UtcNow);
    public UnifiedHealth Health => Volatile.Read(ref health);
    public string Root { get; }
    public bool LegacyDataPresent => DataLayout.HasOlderData(Path.GetDirectoryName(Root)!);
    public UnifiedDatabase Database { get; private set; } = null!;
    public WorkStore Work { get; private set; } = null!;
    public JellyfinUnifiedHost Host { get; private set; } = null!;
    public SourceObserver Observer { get; private set; } = null!;
    public UnifiedExecutor Executor { get; private set; } = null!;
    public QueueWakeup WorkAvailable { get; } = new();
    public QueueIdleBackoff ExecutionIdle { get; } = new();
    public bool Ready { get; private set; }
    private long dirty = 1;
    private readonly ConcurrentDictionary<Guid, byte> changed = new();
    private readonly SemaphoreSlim browsing = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> browsedLibraries = new(StringComparer.Ordinal);
    public bool ScanRunning => library.IsScanRunning;
    public object[] Libraries() => library.GetVirtualFolders().Select(x => (object)new { Id = x.ItemId, x.Name, SaversDisabled = x.LibraryOptions?.MetadataSavers is { Length: 0 } }).ToArray();
    public void ValidateLibraries(string[] ids)
    {
        var available = library.GetVirtualFolders().Where(x => Guid.TryParse(x.ItemId, out _)).Select(x => Guid.Parse(x.ItemId)).ToHashSet();
        if (ids.Any(x => !Guid.TryParse(x, out var id) || !available.Contains(id))) throw new ArgumentException("LibraryUnavailable");
    }
    public UnifiedRuntime(IApplicationPaths paths, ILibraryManager library, IApplicationHost application, IUnifiedTransport transport)
    {
        Root = DataLayout.Root(paths.DataPath); this.library = library; this.application = application; this.transport = transport;
        library.ItemAdded += Changed; library.ItemUpdated += Changed; library.ItemRemoved += Changed;
    }
    private void Changed(object? sender, ItemChangeEventArgs args)
    {
        if (args.Item is not Movie movie) return;
        browsedLibraries.Clear();
        if (changed.Count < 8192) changed.TryAdd(movie.Id, 0); else Interlocked.Increment(ref dirty);
    }
    internal void SetHealth(string state, string? error = null) => Volatile.Write(ref health, new(state, error, DateTimeOffset.UtcNow));
    public void Open(bool create = false)
    {
        lock (gate)
        {
            if (Ready) return;
            var database = new UnifiedDatabase(Root, create, WorkAvailable);
            var work = new WorkStore(database); var host = new JellyfinUnifiedHost(library, application, database);
            var executor = new UnifiedExecutor(work, host, transport);
            try { new Localizer.Unified.ConfigurationStore(database).SeedBuiltin(); }
            catch { executor.Dispose(); throw; }
            Database = database; Work = work; Host = host; Observer = new(work, host, host); Executor = executor;
            Ready = true; SetHealth("Ready"); WorkAvailable.Notify();
        }
    }
    public void RequireReady() { if (!Ready) throw new InvalidOperationException(Health.Error ?? "UnifiedDatabaseRequired"); }
    public void Notify() => Interlocked.Increment(ref dirty);
    public Task<string[]> ListModelsAsync(CancellationToken token)
    {
        RequireReady();
        var connection = new SettingsStore(Database).Connection() ?? throw new InvalidOperationException("connection_required");
        return ListModelsAsync(connection.Endpoint, null, token);
    }
    public Task<string[]> ListModelsAsync(string endpoint, string? apiKey, CancellationToken token)
    {
        RequireReady();
        endpoint = PublicCloudEndpoint.Canonicalize(endpoint);
        string? reference = null;
        if (string.IsNullOrEmpty(apiKey))
        {
            var saved = new SettingsStore(Database).Connection();
            if (saved?.Endpoint == endpoint) reference = saved.CredentialReference;
            apiKey = null;
            if (reference is null) throw new InvalidOperationException("credential_unavailable");
        }
        else ServerCredentialStore.ValidateKey(apiKey);
        return transport is IUnifiedModelCatalog catalog ? catalog.ListModelsAsync(new(endpoint, apiKey, reference), token)
            : throw new InvalidOperationException("model_catalog_unavailable");
    }
    public async Task ObserveBrowseLibraryAsync(string libraryId, CancellationToken token)
    {
        ValidateLibraries([libraryId]);
        if (new SettingsStore(Database).Read().ManagedLibraries.Contains(libraryId, StringComparer.Ordinal)) return;
        await browsing.WaitAsync(token);
        try
        {
            if (browsedLibraries.ContainsKey(libraryId)) return;
            foreach (var item in await Host.ListAsync([libraryId], token)) await Observer.ObserveMediaAsync(item, token);
            browsedLibraries.TryAdd(libraryId, 0);
        }
        finally { browsing.Release(); }
    }
    internal async Task AdmitLanguageAsync(CancellationToken token)
    {
        string? pending;
        using (var db = Database.Open()) pending = Scalar<string>(db, null, "SELECT value FROM runtime_state WHERE key='language_pending'");
        if (pending is null) return;
        var settings = new SettingsStore(Database).Read();
        if (pending != settings.TargetEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)) return;
        var seeds = await SeedsAsync(null, null, token);
        Work.Submit("language/" + pending + "/" + settings.Revision, "language", settings.Revision, seeds);
        Database.Write((db, tx) => Execute(db, tx, "DELETE FROM runtime_state WHERE key='language_pending' AND value=$e AND (SELECT revision FROM settings)=$r", ("$e", pending), ("$r", settings.Revision)));
    }
    internal long Dirty => Interlocked.Read(ref dirty);
    internal async Task ObserveChangesAsync(CancellationToken token)
    {
        var group = Id();
        foreach (var id in changed.Keys)
        {
            if (!changed.TryRemove(id, out _)) continue;
            try
            {
                if (Host.FindMedia(id) is { } item) await Observer.ObserveMediaAsync(item, token, group);
                else
                {
                    if (Host.FindMedia(id, includeUnmanaged: true) is { } outside)
                    {
                        using var outsideDb = Database.Open();
                        if (Scalar<long>(outsideDb, null, "SELECT COUNT(*) FROM media_items WHERE item_id=$i", ("$i", id.ToString("N"))) != 0)
                            await Observer.ObserveMediaAsync(outside, token, group);
                        continue;
                    }
                    var missing = new List<string>();
                    using (var db = Database.Open())
                    using (var cmd = Command(db, null, "SELECT id FROM media_items WHERE item_id=$i AND present=1", ("$i", id.ToString("N"))))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) missing.Add(r.GetString(0));
                    foreach (var media in missing) new ContentStore(Database).MarkRemoved(media);
                }
            }
            catch { changed.TryAdd(id, 0); throw; }
        }
    }
    public async Task<WorkSeed[]> SeedsAsync(string[]? mediaIds, string[]? fields, CancellationToken token, UnifiedSettings? proposed = null)
    {
        RequireReady();
        var settings = proposed ?? new SettingsStore(Database).Read(); var content = new ContentStore(Database);
        var identities = new List<(string Id, MediaIdentity Key)>();
        using (var db = Database.Open())
        using (var cmd = Command(db, null, "SELECT id,server_id,library_id,item_id FROM media_items WHERE present=1 ORDER BY id"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            if (settings.ManagedLibraries.Contains(r.GetString(2), StringComparer.Ordinal)
                && (mediaIds is null || mediaIds.Contains(r.GetString(0), StringComparer.Ordinal)))
                identities.Add((r.GetString(0), new(r.GetString(1), r.GetString(2), r.GetString(3))));
        }
        var seeds = new List<WorkSeed>();
        foreach (var item in identities)
            foreach (var field in new[] { "Name", "Overview", "Genres" }.Where(f => settings.Fields.Includes(f) && (fields is null || fields.Contains(f, StringComparer.Ordinal))))
            {
                var state = content.ReadField(item.Id, field); if (state is null || state.Availability != "available") continue;
                var actual = await Host.ReadAsync(item.Key, field, token);
                if (!actual.Present || actual.SourceError is not null) continue;
                // Executor re-observes and rejects any change after this preview.
                seeds.Add(new(state, actual.Display));
            }
        return [.. seeds];
    }
    public async Task<(WorkSeed[] Seeds, long Skipped, int Changed)> MissingSeedsAsync(string[]? mediaIds, string[]? fields, CancellationToken token)
    {
        var plan = new FillPlanner(Database).Read(mediaIds, fields); var seeds = new List<WorkSeed>(); var changed = 0;
        foreach (var candidate in plan.Candidates)
        {
            token.ThrowIfCancellationRequested(); var f = candidate.Field;
            var actual = await Host.ReadAsync(candidate.Key, f.Field, token);
            if (!actual.Present || actual.SourceError is not null || actual.Original is null
                || !UnifiedExecutor.SameOriginal(f.Field, actual.Original, f.Content) || actual.Prefix != f.Prefix
                || f.Field == "Genres" && !JsonSerializer.Deserialize<string[]>(actual.Original)!.SequenceEqual(JsonSerializer.Deserialize<string[]>(f.RenderJson)!))
            { changed++; continue; }
            seeds.Add(new(f, actual.Display));
        }
        return ([.. seeds], plan.Skipped, changed);
    }
    public void Dispose()
    {
        library.ItemAdded -= Changed; library.ItemUpdated -= Changed; library.ItemRemoved -= Changed;
        lock (gate) { if (Ready) { Executor.Dispose(); Database.ReleaseReadPool(); } }
    }
    public static TitleRules Rules(string language)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Localizer.Rules." + language + ".json") ?? throw new ArgumentException("UnsupportedLanguage");
        return JsonSerializer.Deserialize<TitleRules>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}

public sealed class UnifiedWorker(UnifiedRuntime runtime, ILogger<UnifiedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        try { runtime.Open(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { runtime.SetHealth("Unavailable", error is InvalidOperationException invalid && invalid.Message.StartsWith("UnsupportedUnified", StringComparison.Ordinal) ? "UnsupportedUnifiedSchema" : "UnifiedDatabaseRequired"); }
        // Observer is read-only with respect to Jellyfin; the only executor serializes all HTTP and display operations.
        await Task.WhenAll(Observe(token), Execute(token));
    }
    private async Task Observe(CancellationToken token)
    {
        long observed = -1; var next = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            if (runtime.Ready && !runtime.ScanRunning && (runtime.Dirty != observed || DateTimeOffset.UtcNow >= next))
            {
                var version = runtime.Dirty;
                try
                {
                    await runtime.Observer.ObserveAllAsync(token); await runtime.AdmitLanguageAsync(token); observed = version; next = DateTimeOffset.UtcNow.AddMinutes(1);
                    runtime.SetHealth(runtime.Executor.BackgroundError is null ? "Ready" : "Degraded", runtime.Executor.BackgroundError);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error) { LogFailure("source_observation", error); runtime.SetHealth("Degraded", "source_observation_failed"); next = DateTimeOffset.UtcNow.AddSeconds(5); observed = version; }
            }
            else if (runtime.Ready && !runtime.ScanRunning)
            {
                try { await runtime.ObserveChangesAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error) { LogFailure("source_event", error); runtime.SetHealth("Degraded", "source_event_failed"); }
            }
            await Task.Delay(500, token);
        }
    }
    private void LogFailure(string stage, Exception error, ExecutionDiagnostic? detail = null)
    {
        var diagnostic = detail ?? ExecutionDiagnostic.Capture(stage, null, error);
        logger.LogError("Metadata Localizer failed at {Stage}; work {WorkId}; type {ErrorType}; code {ErrorCode}; SQLite extended {SqliteExtendedCode}.",
            diagnostic.Stage, diagnostic.WorkId, diagnostic.ErrorType, diagnostic.ErrorCode, diagnostic.SqliteExtendedCode);
    }
    private async Task Execute(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ExecutionDiagnostic? failure = null;
            var stage = "executor";
            try
            {
                var observed = runtime.WorkAvailable.Version;
                if (runtime.Ready && await runtime.Executor.RunNextAsync(token, detail => failure = detail))
                { runtime.ExecutionIdle.Reset(); continue; }
                runtime.ExecutionIdle.RecordEmptyCheck();
                stage = "queue_deadline";
                var delay = runtime.ExecutionIdle.Delay;
                if (runtime.Ready) delay = runtime.Work.NextWakeDelay(delay);
                // A concurrent reader of the execution gate must not turn a due row into a hot loop.
                if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(25);
                stage = "queue_wait";
                if (await runtime.WorkAvailable.WaitAsync(observed, delay, token)) runtime.ExecutionIdle.Reset();
                else runtime.ExecutionIdle.Advance();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                runtime.ExecutionIdle.Reset();
                runtime.SetHealth("Degraded", "executor_failed"); LogFailure(stage, error, failure);
                await Task.Delay(1000, token);
            }
        }
    }
}
