using Localizer.Core;
using Localizer.Translation;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Localizer.Plugin;

public sealed class GenreTranslationWorker(IApplicationPaths paths, ILogger<GenreTranslationWorker> logger) : IHostedService
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task? active;
    private Guid? activeId;
    private string Root => Path.Combine(paths.DataPath, "metadata-localizer");
    private GenreTranslationStore Store() => new(Path.Combine(Root, "genre-translations.db"));
    public Task StartAsync(CancellationToken token)
    {
        Store().RecoverInterrupted(); // Runs before requests are accepted, never while a worker is active.
        return Task.CompletedTask;
    }
    public bool IsRunning(Guid id) { lock (gate) return activeId == id && active is { IsCompleted: false }; }
    public GenreTranslationTask EndStopped(Guid id, long revision)
    {
        lock (gate)
        {
            if (activeId == id && active is { IsCompleted: false }) throw new OperationBusyException();
            return Store().EndStopped(id, revision);
        }
    }
    public void Start(Guid id)
    {
        lock (gate)
        {
            if (shutdown.IsCancellationRequested) throw new OperationBusyException();
            if (active is { IsCompleted: false })
            {
                if (activeId == id) return;
                throw new OperationBusyException();
            }
            if (Store().Get(id).State != "Ready") throw new OperationBusyException();
            activeId = id;
            active = Task.Run(async () =>
            {
                try
                {
                    var runner = new GenreTranslationRunner(Store(), (profile, batch, token) =>
                        new GenreTranslationProvider(() => ProviderCredential.LoadFor(Root, profile))
                            .TranslateAsync(profile, batch, token));
                    await runner.RunAsync(id, shutdown.Token);
                }
                catch (OperationCanceledException) { /* Stored batch claim is recovered on next startup. */ }
                catch (Exception error)
                {
                    // No provider bodies, terms or credentials in logs. A Sending claim stays fail-closed.
                    logger.LogError("Dictionary worker stopped ({ErrorType}); inspect task status.", error.GetType().Name);
                }
            });
        }
    }
    public async Task StopAsync(CancellationToken token)
    {
        Task? pending;
        lock (gate) { shutdown.Cancel(); pending = active; }
        if (pending is not null) await pending.WaitAsync(token);
    }
}
