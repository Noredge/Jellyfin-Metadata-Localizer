using System.Text.Json;
using Localizer.Core;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;

namespace Localizer.Plugin;

public sealed record DetectionState(bool Enabled, bool Scanning, string? Error, ChangeSnapshot? Snapshot);

public sealed class ChangeDetectionWorker(IApplicationPaths paths, ILibraryManager library, IApplicationHost host,
    TranslationWorker translations, BatchWorker batches) : BackgroundService
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, DetectionState> states = new();
    private readonly Dictionary<Guid, long> revisions = new();
    private readonly Dictionary<Guid, DateTimeOffset> attempts = new();
    private long revision;
    private DateTimeOffset lastEvent = DateTimeOffset.MinValue;
    private bool loaded;
    private string FilePath => Path.Combine(paths.DataPath, "metadata-localizer", "detection-libraries.json");

    private void Load()
    {
        if (loaded) return;
        if (File.Exists(FilePath))
            foreach (var entry in JsonSerializer.Deserialize<Dictionary<Guid, bool>>(File.ReadAllText(FilePath)) ?? [])
                states[entry.Key] = new(entry.Value, false, null, null);
        loaded = true;
    }
    public void Watch(Guid folder, bool enabled, bool onlyIfNew)
    {
        lock (gate)
        {
            Load();
            if (states.TryGetValue(folder, out var previous) && (onlyIfNew || previous.Enabled == enabled)) return;
            var ids = states.ToDictionary(x => x.Key, x => x.Value.Enabled);
            ids[folder] = enabled;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, ids); stream.Flush(true); }
            File.Move(temp, FilePath, true);
            states[folder] = new(enabled, false, null, previous?.Snapshot);
            revisions.Remove(folder);
        }
    }
    public DetectionState Read(Guid folder)
    {
        lock (gate) { Load(); return states.GetValueOrDefault(folder) ?? new(false, false, null, null); }
    }
    private void Changed(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Movie) return;
        lock (gate) { revision++; lastEvent = DateTimeOffset.UtcNow; }
    }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        library.ItemAdded += Changed; library.ItemUpdated += Changed; library.ItemRemoved += Changed;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(token))
            {
                Guid[] folders;
                try { lock (gate) { Load(); folders = states.Where(x => x.Value.Enabled).Select(x => x.Key).ToArray(); } }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
                // Wait for the scan/write burst to settle; never freeze an intermediate scan as current.
                if (library.IsScanRunning) continue;
                foreach (var folder in folders)
                {
                    long observed;
                    lock (gate)
                    {
                        if (!states[folder].Enabled || DateTimeOffset.UtcNow - lastEvent < TimeSpan.FromSeconds(5)) continue;
                        observed = revision;
                        if (states[folder].Error is not null && DateTimeOffset.UtcNow - attempts.GetValueOrDefault(folder)
                            < TimeSpan.FromMinutes(5)) continue;
                        if (revisions.GetValueOrDefault(folder, -1) == observed && states[folder].Snapshot is { } prior
                            && DateTimeOffset.UtcNow - prior.CheckedUtc < TimeSpan.FromMinutes(5)) continue;
                        states[folder] = states[folder] with { Scanning = true };
                        attempts[folder] = DateTimeOffset.UtcNow;
                    }
                    try
                    {
                        var controller = new AdminController(library, host, paths, translations, batches);
                        var snapshot = controller.DetectChanges(folder, token);
                        lock (gate)
                        {
                            if (states[folder].Enabled)
                            {
                                states[folder] = new(true, false, null, snapshot);
                                revisions[folder] = observed;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        var error = ex is KeyNotFoundException ? "library_unavailable" : ex is OperationValidationException
                            ? "detection_library_limit" : "detection_unavailable";
                        lock (gate) states[folder] = states[folder] with { Scanning = false, Error = error };
                    }
                }
            }
        }
        finally { library.ItemAdded -= Changed; library.ItemUpdated -= Changed; library.ItemRemoved -= Changed; }
    }
}
