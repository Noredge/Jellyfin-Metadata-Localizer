using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Localizer.Core;
using Localizer.Translation;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record TaskItemRequest(Guid ItemId, long SourceId, [Required] string[] Names);
public sealed record TaskCreateRequest(Guid RequestId, [Required] string Language,
    [Required, MinLength(1), MaxLength(20)] TaskItemRequest[] Items, bool NamesReviewed, string? ServiceId = null);
public sealed record TaskRunRequest(bool ConfirmSend, bool RetryFailed = false, bool AcceptUncertainRequest = false);

public sealed partial class AdminController
{
    private sealed record TaskPreparationReceipt(string Fingerprint, PreparedWorkbenchInput[] Inputs, TranslationPolicy Policy);
    [HttpGet("credential-status")]
    public ActionResult CredentialStatus() => Guard(() => new { Provider = "Groq", Model = TitlePipeline.QwenModel,
        PlatformSupported = OperatingSystem.IsWindows(), CredentialFilePresent = System.IO.File.Exists(GroqCredential.FilePath(Root)),
        AuthenticationChecked = false }); // File presence is not an authentication check.

    private TranslationJob ScopedJob(Guid folder, string id, CandidateStore store)
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var job = store.GetTranslationJob(id);
        if (job.Scope != new LibraryKey(host.SystemId, folder.ToString("N"))) throw new KeyNotFoundException();
        return job;
    }
    private object JobView(TranslationJob job, CandidateStore store) => new
    {
        job.Id, Language = job.Language.Tag(), State = !worker.IsRunning(job.Id) && job.State == TranslationJobState.Running
            ? "Interrupted" : job.State.ToString(), WorkerRunning = worker.IsRunning(job.Id), job.ErrorCategory,
        job.NextRequestUtc, job.CreatedUtc, job.Policy,
        Items = store.GetTranslationItems(job.Id).Select(x => new { ItemId = x.Source.Key.ItemId, x.Ordinal, x.Source.Id,
            State = x.State.ToString(), x.Attempts, x.CandidateId, x.ErrorCategory, Diagnostics = x.Result?.Diagnostics,
            Warnings = x.Result?.Warnings ?? [], MaskedSource = TitlePipeline.ValidateContext(new(x.Source, job.Language, x.Spec)).MaskedSource,
            x.Spec.Model, x.Spec.PromptVersion, x.Spec.RulesVersion,
            ServiceId = System.Text.Json.Nodes.JsonNode.Parse(x.Spec.ContextJson)?["ServiceId"]?.GetValue<string>() ?? "groq" }).ToArray()
    };
    [HttpGet("libraries/{folder:guid}/tasks")]
    public ActionResult Tasks(Guid folder) => Guard(() =>
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var store = Store();
        return store.ListTranslationJobs(new(host.SystemId, folder.ToString("N"))).Select(x => JobView(x, store)).ToArray();
    });
    [HttpGet("libraries/{folder:guid}/tasks/{id:guid}")]
    public ActionResult TaskStatus(Guid folder, Guid id) => Guard(() =>
    { var store = Store(); return JobView(ScopedJob(folder, id.ToString("N"), store), store); });

    [HttpPost("libraries/{folder:guid}/tasks")]
    public ActionResult CreateTask(Guid folder, [FromBody] TaskCreateRequest request) => Guard(() =>
    {
        if (request.RequestId == Guid.Empty || !request.NamesReviewed || request.Items is null || request.Items.Length is < 1 or > 20
            || request.Items.Any(x => x is null) || request.Items.Select(x => x.ItemId).Distinct().Count() != request.Items.Length) throw new ArgumentException();
        var language = Languages.Parse(request.Language);
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Localizer.Rules." + language.Tag() + ".json")!;
        var rules = JsonSerializer.Deserialize<TitleRules>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var store = Store();
        var id = request.RequestId.ToString("N"); var scope = Scope(folder);
        var fingerprint = Hash(JsonSerializer.Serialize(new { scope, Request = request }));
        var file = Path.Combine(Root, "task-preparations", id + ".json");
        var existing = ExistingPreparationJob(store, id);
        if (System.IO.File.Exists(file))
        {
            var receipt = JsonSerializer.Deserialize<TaskPreparationReceipt>(System.IO.File.ReadAllText(file)) ?? throw new InvalidOperationException();
            if (receipt.Fingerprint != fingerprint) throw new IdempotencyConflictException();
            if (existing is null)
            {
                if (receipt.Inputs.Any(x => store.GetCurrentSource(x.Source.Key) != x.Source)) throw new SourceChangedException();
                existing = store.CreateTranslationJob(id, scope, language, receipt.Inputs.Select(x => new TitleTranslationInput(x.Source.Key, x.Spec)), receipt.Policy);
            }
            return JobView(ScopedJob(folder, id, store), store);
        }
        if (existing is not null)
        {
            // Legacy task IDs already have a durable request. Do not rebuild them with new defaults.
            var stored = store.GetTranslationItems(id);
            if (existing.Scope != scope || existing.Language != language || request.ServiceId is not (null or "groq") || stored.Count != request.Items.Length)
                throw new IdempotencyConflictException();
            for (var n = 0; n < stored.Count; n++)
            {
                var input = request.Items[n]; var old = stored[n];
                if (input.ItemId.ToString("N") != old.Source.Key.ItemId || input.SourceId != old.Source.Id || input.Names is null
                    || input.Names.Length > 40 || input.Names.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200)) throw new IdempotencyConflictException();
                var expected = TitlePipeline.Protect(old.Source.OriginalTitle, input.Names);
                var actual = TitlePipeline.ValidateContext(new(old.Source, language, old.Spec));
                if (expected.MaskedSource != actual.MaskedSource || !expected.Mapping.OrderBy(x => x.Key).SequenceEqual(actual.Mapping.OrderBy(x => x.Key)))
                    throw new IdempotencyConflictException();
            }
            return JobView(existing, store);
        }
        var service = Services().Select(request.ServiceId); var people = DiscoverPersonNames(folder);
        var prepared = request.Items.Select(input =>
        {
            var source = RequireSource(folder, Resolve(folder, input.ItemId), store);
            if (source.Id != input.SourceId) throw new SourceChangedException();
            if (input.Names is null || input.Names.Length > 40 || input.Names.Any(x => string.IsNullOrWhiteSpace(x)
                || x.Length > 200 || x.Trim() != x || (!source.OriginalTitle.Contains(x, StringComparison.Ordinal)
                    && !(people.Resolve(x)?.Aliases.Prepend(people.Resolve(x)!.OriginalName)
                        .Any(alias => source.OriginalTitle.Contains(alias, StringComparison.Ordinal)) ?? false)))) throw new ArgumentException();
            return new PreparedWorkbenchInput(source, PrepareConfiguredTitle(source, language, input.Names, rules, service, people));
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(output, new TaskPreparationReceipt(fingerprint, prepared, service.Policy())); output.Flush(true); }
        System.IO.File.Move(temporary, file, false);
        var job = store.CreateTranslationJob(id, scope, language, prepared.Select(x => new TitleTranslationInput(x.Source.Key, x.Spec)), service.Policy());
        return JobView(job, store);
    });

    [HttpPost("libraries/{folder:guid}/tasks/{id:guid}/run")]
    public ActionResult RunTask(Guid folder, Guid id, [FromBody] TaskRunRequest request) => Guard(() =>
    {
        if (!request.ConfirmSend) throw new ArgumentException();
        var store = Store(); var job = ScopedJob(folder, id.ToString("N"), store);
        foreach (var item in store.GetTranslationItems(job.Id).Where(x => job.State != TranslationJobState.CancelRequested
            && x.State is TranslationItemState.Pending or TranslationItemState.Failed or TranslationItemState.Uncertain))
        {
            if (store.GetCurrentSource(item.Source.Key)?.Id != item.Source.Id || !LiveSourceGuard.Valid(library, host, Root, item.Source))
                throw new SourceChangedException();
        }
        worker.Run(job.Id, new(request.RetryFailed, request.AcceptUncertainRequest));
        return new { job.Id, Accepted = true };
    });
    [HttpPost("libraries/{folder:guid}/tasks/{id:guid}/cancel")]
    public ActionResult CancelTask(Guid folder, Guid id) => Guard(() =>
    {
        var store = Store(); var job = ScopedJob(folder, id.ToString("N"), store);
        store.RequestTranslationCancel(job.Id);
        return JobView(store.GetTranslationJob(job.Id), store);
    });
}
