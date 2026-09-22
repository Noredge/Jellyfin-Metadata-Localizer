using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Localizer.Core;
using Localizer.Translation;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record PrepareWorkbenchTranslationRequest(Guid RequestId, [Required] string Language,
    [Required, MinLength(1), MaxLength(20)] WorkbenchConfirmItem[] Items, string? ServiceId = null);

public sealed partial class AdminController
{
    private sealed record PreparedWorkbenchInput(SourceSnapshot Source, GenerationSpec Spec);
    private sealed record WorkbenchPreparationReceipt(string Fingerprint, LibraryKey Scope, string Language,
        string State, WorkbenchMutationResult[]? Items = null, PreparedWorkbenchInput[]? Prepared = null,
        TranslationServiceProfile? Service = null, PersonNameSnapshot? Names = null);

    private string PreparationPath(Guid requestId) => Path.Combine(Root, "workbench-preparations", requestId.ToString("N") + ".json");

    private static void SavePreparation(string file, WorkbenchPreparationReceipt receipt, bool create = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, receipt); stream.Flush(true);
        }
        System.IO.File.Move(temporary, file, !create);
    }

    private static TranslationJob? ExistingPreparationJob(CandidateStore store, string id)
    {
        try { return store.GetTranslationJob(id); }
        catch (KeyNotFoundException) { return null; }
    }

    [HttpPost("libraries/{folder:guid}/workbench/prepare-translation"), RequestSizeLimit(32768)]
    public ActionResult PrepareWorkbenchTranslation(Guid folder, [FromBody] PrepareWorkbenchTranslationRequest request) => Guard(() =>
    {
        var language = Languages.Parse(request.Language);
        if (request.RequestId == Guid.Empty || request.Items is null || request.Items.Length is < 1 or > 20
            || request.Items.Any(x => x is null || x.ItemId == Guid.Empty || x.Observation is null || x.Observation.Length != 64)
            || request.Items.Select(x => x.ItemId).Distinct().Count() != request.Items.Length) throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var scope = Scope(folder); var id = request.RequestId.ToString("N");
        var fingerprint = Hash(JsonSerializer.Serialize(request.ServiceId is null ? (object)new { Scope = scope, request.Language, request.Items }
            : new { Scope = scope, request.Language, request.Items, request.ServiceId }));
        var file = PreparationPath(request.RequestId); var store = Store();
        var existingJob = ExistingPreparationJob(store, id);
        if (System.IO.File.Exists(file))
        {
            var receipt = JsonSerializer.Deserialize<WorkbenchPreparationReceipt>(System.IO.File.ReadAllText(file))
                ?? throw new InvalidOperationException("PreparationReceiptInvalid");
            if (receipt.Fingerprint != fingerprint || receipt.Scope != scope || receipt.Language != request.Language)
                throw new IdempotencyConflictException();
            if (existingJob is not null)
            {
                if (existingJob.Scope != scope || existingJob.Language != language || receipt.Items is null || receipt.Prepared is null)
                    throw new IdempotencyConflictException();
                var accepted = receipt.Items.Where(x => x.Success).Select(x => x.ItemId.ToString("N")).ToArray();
                var storedItems = store.GetTranslationItems(id);
                if (!accepted.SequenceEqual(storedItems.Select(x => x.Source.Key.ItemId), StringComparer.Ordinal)
                    || !receipt.Prepared.SequenceEqual(storedItems.Select(x => new PreparedWorkbenchInput(x.Source, x.Spec))))
                    throw new IdempotencyConflictException();
                return new { Task = JobView(existingJob, store), Items = receipt.Items };
            }
            if (receipt.State == "Completed" && receipt.Items is not null && receipt.Items.All(x => !x.Success))
                return new { Task = (object?)null, Items = receipt.Items };
            // An interrupted preparation can have confirmed sources already. Never guess or repeat those writes.
            throw new InvalidOperationException("PreparationInterruptedRefreshRequired");
        }
        if (existingJob is not null) throw new IdempotencyConflictException();
        var selectedService = Services().Select(request.ServiceId); var people = DiscoverPersonNames(folder);
        var preparing = new WorkbenchPreparationReceipt(fingerprint, scope, request.Language, "Preparing", Service: selectedService, Names: people);
        SavePreparation(file, preparing, create: true);
        using var ruleStream = typeof(Plugin).Assembly.GetManifestResourceStream("Localizer.Rules." + language.Tag() + ".json")!;
        var rules = JsonSerializer.Deserialize<TitleRules>(ruleStream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var results = new List<WorkbenchMutationResult>(); var inputs = new List<TitleTranslationInput>();
        var prepared = new List<PreparedWorkbenchInput>();
        var data = store.ReadWorkbench(scope, language); var saversDisabled = WorkbenchSaversDisabled(folder);
        foreach (var input in request.Items)
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            results.Add(PrepareWorkbenchMutation(input.ItemId, () =>
            {
                var movie = Resolve(folder, input.ItemId); var key = Key(folder, movie.Id);
                var currentSource = store.GetCurrentSource(key);
                var observedPath = movie.Path;
                var observedOriginal = movie.OriginalTitle ?? "";
                if (Observation(movie, currentSource) != input.Observation) throw new RevisionConflictException();
                var row = WorkbenchItem(folder, movie, data, saversDisabled);
                if (row.Observation != input.Observation || row.OriginalTitle != observedOriginal || movie.Path != observedPath)
                    throw new RevisionConflictException();
                if (!row.CanConfirm && !row.CanTranslate) throw new OperationValidationException(row.Reason);
                if (currentSource is not null && store.ListCandidates(currentSource.Id, language).Count != 0)
                    throw new OperationValidationException("translation_already_exists");
                var source = currentSource ?? new SourceSnapshot(0, key, 0, row.SuggestedOriginal, row.SuggestedPrefix, "");
                // These names are automatically matched host metadata, not a human-reviewed completeness claim.
                var names = library.GetPeople(movie).Select(x => x.Name)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (names.Length > 2000 || names.Any(x => x.Length > 200)) throw new ArgumentException("InvalidProtectedNames");
                var spec = PrepareConfiguredTitle(source, language, names, rules, selectedService, people);
                if (row.CanConfirm)
                {
                    // Confirm only the reliable OriginalTitle suffix represented by the client's observation.
                    source = store.ObserveSource(key, row.SuggestedOriginal, row.SuggestedPrefix);
                    WriteBinding(key, new(source.Id, observedPath, observedOriginal, row.Name));
                }
                else source = RequireSource(folder, movie, store);
                inputs.Add(new(source.Key, spec));
                prepared.Add(new(source, spec));
            }));
        }
        var receiptResults = results.ToArray();
        SavePreparation(file, preparing with { State = "Prepared", Items = receiptResults, Prepared = prepared.ToArray() });
        // Only create a durable local queue. Sending remains the existing explicitly confirmed run endpoint.
        var job = inputs.Count == 0 ? null : store.CreateTranslationJob(id, scope, language, inputs, selectedService.Policy());
        SavePreparation(file, preparing with { State = "Completed", Items = receiptResults, Prepared = prepared.ToArray() });
        return new { Task = job is null ? null : JobView(job, store), Items = receiptResults };
    });

    private static WorkbenchMutationResult PrepareWorkbenchMutation(Guid item, Action action)
    {
        try { action(); return new(item, true, null); }
        catch (OperationValidationException ex) { return new(item, false, ex.Category); }
        catch (KeyNotFoundException) { return new(item, false, "not_found_in_library"); }
        catch (ArgumentException) { return new(item, false, "invalid_translation_input"); }
        catch (InvalidOperationException) { return new(item, false, "changed_reload_required"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(item, false, "local_state_needs_review"); }
    }
}
