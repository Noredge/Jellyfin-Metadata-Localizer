using System.ComponentModel.DataAnnotations;
using Localizer.Core;
using Localizer.Translation;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record GenreTaskCreateRequest(Guid RequestId, string? ServiceId);
public sealed record GenreTaskRunRequest(long Revision, bool ConfirmSend, bool ContinuePending = false);
public sealed record GenreTaskEditRequest(long Revision, [Required, StringLength(500)] string Original,
    [Required] string Language, [Required, StringLength(500)] string Text);
public sealed record GenreTaskSaveRequest(Guid RequestId, long Revision, int[] Items, bool ConfirmSave);
public sealed record GenreTaskEndRequest(long Revision, bool ConfirmEnd);

public sealed partial class AdminController
{
    private GenreTranslationStore GenreTasks() => new(Path.Combine(Root, "genre-translations.db"));
    private GenreTranslationTask OwnedGenreTask(Guid folder, Guid id)
    {
        Nonempty(id);
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var task = GenreTasks().Get(id);
        if (task.Server != host.SystemId || task.Library != folder.ToString("N")) throw new KeyNotFoundException();
        return task;
    }

    [HttpGet("libraries/{folder:guid}/genres/translations")]
    public ActionResult ListGenreTranslations(Guid folder) => CampaignGuard(() =>
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        return GenreTasks().List(host.SystemId, folder.ToString("N"));
    });

    [HttpGet("libraries/{folder:guid}/genres/translations/{id:guid}")]
    public ActionResult ReadGenreTranslation(Guid folder, Guid id, [FromServices] GenreTranslationWorker worker,
        [FromQuery] int start = 0) => CampaignGuard(() =>
    {
        if (start is < 0 or > 10000) throw new ArgumentException();
        var task = OwnedGenreTask(folder, id); var store = Store();
        var mappings = task.Items.Skip(start).Take(50).Select((item, n) =>
        {
            var current = store.GetGenreDictionaryEntry(item.Source.Language, item.Source.Original);
            return new { Index = start + n, Current = current.Effective,
                Matches = item.Text is not null && item.Text == current.Effective,
                CanSave = current.Revision == item.Source.MappingRevision && current.Effective == item.Source.ExpectedMapping };
        }).ToArray();
        return new { Task = task, Running = worker.IsRunning(id), Mappings = mappings };
    });

    // Preparation only. Sending is a separate explicit operation on the frozen scope.
    [HttpPost("libraries/{folder:guid}/genres/translations")]
    public ActionResult CreateGenreTranslation(Guid folder, [FromBody] GenreTaskCreateRequest request) => CampaignGuard(() =>
    {
        Nonempty(request.RequestId);
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var tasks = GenreTasks();
        try
        {
            var existing = OwnedGenreTask(folder, request.RequestId);
            if (request.ServiceId is not null && existing.ServiceId != request.ServiceId) throw new IdempotencyConflictException();
            return existing;
        }
        catch (KeyNotFoundException)
        {
            // Create below still enforces the globally unique request ID and frozen identity.
        }
        var store = Store(); var originals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in store.ListCurrentGenreSources(host.SystemId).Where(x => x.Key.LibraryId == folder.ToString("N")))
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            try
            {
                if (Guid.TryParse(source.Key.ItemId, out var item) && GenreConfirmed(Resolve(folder, item), source, ReadGenreBinding(source.Key)))
                    originals.UnionWith(source.OriginalValues);
            }
            catch (KeyNotFoundException) { /* Stale identities are excluded, never sent. */ }
        }
        var plan = GenreTranslationPipeline.Missing(originals, store.GetGenreDictionaryEntry);
        if (plan.Length == 0) return new { State = "NoMissingMappings", Items = Array.Empty<object>() };
        var seeds = plan.SelectMany(t => t.Languages.Select(language =>
        {
            var entry = store.GetGenreDictionaryEntry(language, t.Original);
            if (!entry.Unknown) throw new RevisionConflictException();
            return new GenreTranslationSeed(t.Original, language, entry.Revision, entry.Effective);
        })).ToArray();
        var profile = Services().Select(request.ServiceId);
        // Independent dictionary budget, frozen with the task; title settings are unchanged.
        return tasks.Create(request.RequestId, host.SystemId, folder.ToString("N"), profile.Id, profile.Model,
            GenreTranslationPipeline.Version, seeds, new(profile.Kind, profile.CanonicalEndpoint(), 4000, profile.UseJsonResponseFormat));
    });

    [HttpPost("libraries/{folder:guid}/genres/translations/{id:guid}/run")]
    public ActionResult RunGenreTranslation(Guid folder, Guid id, [FromBody] GenreTaskRunRequest request,
        [FromServices] GenreTranslationWorker worker) => CampaignGuard(() =>
    {
        if (!request.ConfirmSend) throw new ArgumentException();
        var task = OwnedGenreTask(folder, id);
        if (worker.IsRunning(id)) return new { Task = task, Running = true };
        if (task.Revision != request.Revision) throw new RevisionConflictException();
        if (request.ContinuePending) task = GenreTasks().ContinuePending(id, task.Revision);
        worker.Start(id);
        return new { Task = GenreTasks().Get(id), Running = worker.IsRunning(id) };
    });

    [HttpPost("libraries/{folder:guid}/genres/translations/{id:guid}/edit")]
    public ActionResult EditGenreTranslation(Guid folder, Guid id, [FromBody] GenreTaskEditRequest request) => CampaignGuard(() =>
    {
        _ = OwnedGenreTask(folder, id);
        return GenreTasks().Edit(id, request.Revision, request.Original, Languages.Parse(request.Language), request.Text);
    });

    [HttpPost("libraries/{folder:guid}/genres/translations/{id:guid}/save")]
    public ActionResult SaveGenreTranslation(Guid folder, Guid id, [FromBody] GenreTaskSaveRequest request) => CampaignGuard(() =>
    {
        Nonempty(request.RequestId);
        if (!request.ConfirmSave || request.Items is null || request.Items.Length is < 1 or > 100
            || request.Items.Distinct().Count() != request.Items.Length) throw new ArgumentException();
        var task = OwnedGenreTask(folder, id);
        var fingerprint = Hash(System.Text.Json.JsonSerializer.Serialize(new { task.Server, task.Library, id,
            request.Revision, Items = request.Items.Order().ToArray() }));
        var store = Store();
        var old = store.ReadGenreSaveReceipt(request.RequestId, fingerprint);
        if (old is not null) return new { State = "Saved", Mappings = old };
        if (task.Revision != request.Revision) throw new RevisionConflictException();
        if (task.State is not ("Completed" or "Ended")) throw new OperationBusyException();
        if (request.Items.Any(i => i < 0 || i >= task.Items.Length)) throw new ArgumentException();
        var selected = request.Items.Select(i => task.Items[i]).ToArray();
        if (selected.Any(x => x.State is not ("Generated" or "Edited") || x.Error is not null || string.IsNullOrWhiteSpace(x.Text)))
            throw new ArgumentException();
        var saved = store.SaveConfirmedGenres(request.RequestId, fingerprint,
            selected.Select(x => new GenreConfirmedMapping(x.Source, x.Text!)).ToArray());
        return new { State = "Saved", Mappings = saved };
    });

    [HttpPost("libraries/{folder:guid}/genres/translations/{id:guid}/end")]
    public ActionResult EndGenreTranslation(Guid folder, Guid id, [FromBody] GenreTaskEndRequest request,
        [FromServices] GenreTranslationWorker worker) => CampaignGuard(() =>
    {
        if (!request.ConfirmEnd) throw new ArgumentException();
        var task = OwnedGenreTask(folder, id);
        if (task.State == "Ended") return task;
        return worker.EndStopped(id, request.Revision);
    });
}
