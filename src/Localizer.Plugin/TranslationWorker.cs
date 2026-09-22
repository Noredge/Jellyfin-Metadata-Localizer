using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Localizer.Core;
using Localizer.Translation;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Localizer.Plugin;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<TranslationWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<TranslationWorker>());
        services.AddSingleton<BatchWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BatchWorker>());
        services.TryAddSingleton<ICampaignTranslationRunner, CampaignTranslationRunner>();
        services.AddSingleton<CampaignWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CampaignWorker>());
        services.AddSingleton<GenreTranslationWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<GenreTranslationWorker>());
        services.AddSingleton<ChangeDetectionWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ChangeDetectionWorker>());
    }
}
internal static class LiveSourceGuard
{
    internal static string BindingPath(string root, MediaKey key) => Path.Combine(root, "bindings",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(key)))) + ".json");
    internal static bool Valid(ILibraryManager library, IApplicationHost host, string root, SourceSnapshot source)
    {
        if (source.Key.ServerId != host.SystemId || !Guid.TryParse(source.Key.ItemId, out var item)
            || !Guid.TryParse(source.Key.LibraryId, out var folder)) return false;
        if (!library.GetVirtualFolders().Any(x => Guid.TryParse(x.ItemId, out var id) && id == folder
            && string.Equals(x.CollectionType?.ToString(), "movies", StringComparison.OrdinalIgnoreCase))) return false;
        if (library.GetItemList(new InternalItemsQuery { ItemIds = [item], IncludeItemTypes = [BaseItemKind.Movie] }).SingleOrDefault() is not Movie movie) return false;
        var folders = library.GetCollectionFolders(movie).Select(x => x.Id).Distinct().ToArray();
        if (folders.Length != 1 || folders[0] != folder) return false;
        var file = BindingPath(root, source.Key);
        var binding = File.Exists(file) ? JsonSerializer.Deserialize<Binding>(File.ReadAllText(file)) : null;
        return binding is not null && binding.SourceId == source.Id && Path.IsPathFullyQualified(movie.Path)
            && string.Equals(binding.Path, movie.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && binding.ObservedOriginalTitle == (movie.OriginalTitle ?? "");
    }
}
// One explicit task at a time. Page navigation and host startup never start model work.
public sealed class TranslationWorker(IApplicationPaths paths, ILibraryManager library, IApplicationHost host) : IHostedService
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task? running;
    private string? activeId;
    private string Root => Path.Combine(paths.DataPath, "metadata-localizer");
    public bool IsRunning(string id) { lock (gate) return activeId == id && running is { IsCompleted: false }; }
    public void Run(string id, TranslationResume resume)
    {
        lock (gate)
        {
            if (shutdown.IsCancellationRequested) throw new OperationBusyException();
            if (running is { IsCompleted: false }) throw new OperationBusyException();
            activeId = id;
            running = Task.Run(async () =>
            {
                TranslationQueue? queue = null;
                try
                {
                    var store = new CandidateStore(Path.Combine(Root, "candidates.db"));
                    using var provider = new TranslationServiceRouter(_ => ValueTask.FromResult(GroqCredential.Load(Root)), openAiCredential: _ => ValueTask.FromResult(OpenAiCredential.Load(Root)),
                compatibleCredential: (id, endpoint, _) => ValueTask.FromResult(ProviderCredential.Load(Root, id, endpoint)));
                    var guarded = new GuardedProvider(provider, source => LiveSourceGuard.Valid(library, host, Root, source));
                    queue = new TranslationQueue(store, guarded);
                    await queue.RunAsync(id, resume, shutdown.Token);
                }
                catch (Exception)
                {
                    // No exception contents are logged. Persist conservative interrupted state when possible.
                    try { queue?.Reconcile(id); } catch (Exception) { /* Read API also reports an inactive Running job as interrupted. */ }
                }
            });
        }
    }
    private sealed class GuardedProvider(ITitleProvider inner, Func<SourceSnapshot, bool> valid) : ITitleProvider
    {
        public Task<TitleProviderResult> TranslateAsync(TitleProviderRequest request, CancellationToken token)
        {
            if (!valid(request.Source)) throw new TitleProviderException(ProviderError.Configuration);
            return inner.TranslateAsync(request, token);
        }
    }
    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken token)
    {
        Task? task;
        lock (gate) { shutdown.Cancel(); task = running; }
        if (task is not null) { try { await task.WaitAsync(token); } catch (OperationCanceledException) { } }
    }
}
