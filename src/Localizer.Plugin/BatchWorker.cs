using System.Text.Json;
using Localizer.Core;
using Localizer.Jellyfin;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;

namespace Localizer.Plugin;

internal static class ProductTargets
{
    internal static string GenreBindingPath(string root, MediaKey key) => Path.Combine(root, "genre-bindings",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(key)))) + ".json");
    internal static JellyfinNameTarget Names(string root, Guid folder, CandidateStore store, ILibraryManager library, IApplicationHost host) =>
        new(library, host, () => new HashSet<Guid> { folder }, key =>
        {
            var source = store.GetCurrentSource(key); var file = LiveSourceGuard.BindingPath(root, key);
            var binding = File.Exists(file) ? JsonSerializer.Deserialize<Binding>(File.ReadAllText(file)) : null;
            return source is null || binding?.SourceId != source.Id ? null : new(binding.Path, source.DisplayPrefix, source.OriginalTitle, binding.ObservedOriginalTitle);
        });
    internal static JellyfinGenreTarget Genres(string root, Guid folder, CandidateStore store, ILibraryManager library, IApplicationHost host) =>
        new(library, host, () => new HashSet<Guid> { folder }, key =>
        {
            var source = store.GetCurrentGenreSource(key); var file = GenreBindingPath(root, key);
            var binding = File.Exists(file) ? JsonSerializer.Deserialize<GenreBinding>(File.ReadAllText(file)) : null;
            return source is null || binding?.SourceId != source.Id ? null : binding.Path;
        });
}

public sealed class BatchWorker(IApplicationPaths paths, ILibraryManager library, IApplicationHost host) : IHostedService
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task? running; private string? activeId;
    private string Root => Path.Combine(paths.DataPath, "metadata-localizer");
    public bool IsRunning(string id) { lock (gate) return activeId == id && running is { IsCompleted: false }; }
    public void Run(string id, Guid folder, bool acceptUnknown, bool observeOnly)
    {
        lock (gate)
        {
            if (shutdown.IsCancellationRequested || running is { IsCompleted: false }) throw new OperationBusyException();
            var batches = new WriteBatchStore(Path.Combine(Root, "batches"));
            batches.Change(id, x => x with { State = BatchState.Running, CancelRequested = observeOnly ? x.CancelRequested : false });
            activeId = id;
            running = Task.Run(async () =>
            {
                try
                {
                    var candidates = new CandidateStore(Path.Combine(Root, "candidates.db"));
                    await new WriteBatchQueue(batches, candidates, ProductTargets.Names(Root, folder, candidates, library, host),
                        ProductTargets.Genres(Root, folder, candidates, library, host)).RunAsync(id, acceptUnknown, observeOnly, shutdown.Token);
                }
                catch (Exception) { /* Inactive Running is shown as interrupted; explicit reconciliation uses field journals. */ }
            });
        }
    }
    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken token)
    {
        Task? task; lock (gate) { shutdown.Cancel(); task = running; }
        if (task is not null) { try { await task.WaitAsync(token); } catch (OperationCanceledException) { } }
    }
}
