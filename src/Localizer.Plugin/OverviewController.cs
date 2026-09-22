using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Localizer.Core;
using Localizer.Jellyfin;
using Localizer.Translation;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record OverviewObserveRequest(long Revision);
public sealed record OverviewEditRequest(long Revision, long SourceVersion, string Language, [Required, StringLength(24000)] string Text);
public sealed record OverviewGenerateRequest(Guid RequestId, long Revision, long SourceVersion, string Language,
    string? ServiceId, bool ConfirmSend, bool PreserveManual = false);
public sealed record OverviewApplyRequest(Guid RequestId, long Revision, string CandidateId, string ExpectedDisplay, bool ConfirmApply);
public sealed record OverviewRestoreRequest(Guid RequestId, Guid ApplicationId, bool ConfirmRestore, bool AcceptObserved = false);
public sealed record OverviewGenerationReceipt(string Fingerprint, MediaKey Key, string State, string? Error, string? CandidateId);

public sealed partial class AdminController
{
    private static readonly SemaphoreSlim OverviewGenerationGate = new(1, 1);
    [NonAction]
    internal bool HasPendingOverviewWrite(Guid folder, MediaKey key, string operationId) =>
        OverviewWrites(folder).List(key).Any(x => x.Id == operationId && x.State is "Prepared" or "Writing");
    private OverviewStore Overviews() => new(Path.Combine(Root, "overviews.db"));
    private OverviewOperations OverviewWrites(Guid folder) => new(Root, Overviews(),
        new PreferenceOverviewTarget(new JellyfinOverviewTarget(library, host, () => new HashSet<Guid> { folder }, key =>
        {
            var movie = Resolve(folder, Guid.ParseExact(key.ItemId, "N"));
            var source = Overviews().Read(key).Source;
            var current = JellyfinNfoOverview.Read(movie);
            return source is not null && current.Error is null && current.Path == source.NfoPath
                && current.Hash == source.Hash ? movie.Path : null;
        }), DisplayPreferences(), Overviews()));
    private void RequireOverviewSource(Guid folder, Guid item, OverviewState state)
    {
        var read = JellyfinNfoOverview.Read(Resolve(folder, item));
        if (state.Source is null || read.Error is not null || read.Hash != state.Source.Hash || read.Path != state.Source.NfoPath)
            throw new SourceChangedException();
    }
    [HttpGet("libraries/{folder:guid}/items/{item:guid}/overview")]
    public ActionResult OverviewRead(Guid folder, Guid item) => Guard(() =>
    {
        var movie = Resolve(folder, item);
        var state = Overviews().Read(Key(folder, item));
        return new { Display = movie.Overview ?? "", Source = JellyfinNfoOverview.Read(movie), State = new {
            state.Key, state.Revision, state.Sources,
            Candidates = state.Candidates.Select(x => new { x.Id, x.SourceVersion, Language = x.Language.Tag(), x.Text,
                x.HumanEdited, x.Provider, x.Model, x.PromptVersion }),
            Failures = state.Failures.Select(x => new { x.SourceVersion, Language = x.Language.Tag(), x.Category }) } };
    });
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/overview/observe")]
    public ActionResult OverviewObserve(Guid folder, Guid item, [FromBody] OverviewObserveRequest request) => Guard(() =>
        Overviews().Observe(Key(folder, item), request.Revision, JellyfinNfoOverview.Read(Resolve(folder, item))));
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/overview/edit")]
    public ActionResult OverviewEdit(Guid folder, Guid item, [FromBody] OverviewEditRequest request) => Guard(() =>
    {
        var store = Overviews(); var key = Key(folder, item); RequireOverviewSource(folder, item, store.Read(key));
        return store.Save(key, request.Revision, request.SourceVersion, Languages.Parse(request.Language), request.Text, true, "human", "manual", "overview-manual-v1");
    });
    private static void SaveOverviewReceipt(string path, OverviewGenerationReceipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, receipt); stream.Flush(true); }
        System.IO.File.Move(temp, path, true);
    }
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/overview/generate")]
    public Task<ActionResult> OverviewGenerate(Guid folder, Guid item, [FromBody] OverviewGenerateRequest request) => GuardAsync(async () =>
    {
        Nonempty(request.RequestId); if (!request.ConfirmSend) throw new ArgumentException();
        if (!await OverviewGenerationGate.WaitAsync(0)) throw new OperationBusyException();
        try
        {
            var key = Key(folder, item); var store = Overviews(); var state = store.Read(key);
            var fingerprint = Hash(JsonSerializer.Serialize(new { key, request }));
            var path = Path.Combine(Root, "overview-generations", request.RequestId.ToString("N") + ".json");
            Resolve(folder, item);
            if (System.IO.File.Exists(path))
            {
                var old = JsonSerializer.Deserialize<OverviewGenerationReceipt>(System.IO.File.ReadAllText(path))!;
                if (old.Fingerprint != fingerprint) throw new IdempotencyConflictException();
                return old.State == "Sending" ? old with { State = "Uncertain", Error = "interrupted" } : old;
            }
            RequireOverviewSource(folder, item, state);
            var language = Languages.Parse(request.Language);
            if (state.Revision != request.Revision || state.Source!.Version != request.SourceVersion) throw new RevisionConflictException();
            if (request.PreserveManual && state.Selected(language)?.HumanEdited == true)
            {
                var preserved = new OverviewGenerationReceipt(fingerprint, key, "Preserved", null, state.Selected(language)!.Id);
                SaveOverviewReceipt(path, preserved); return preserved;
            }
            var service = Services().Select(request.ServiceId);
            // Use already known library actor names; source text and restoration mapping stay separate from logs.
            var names = library.GetPeople(Resolve(folder, item)).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)
                && state.Source.Text.Contains(x, StringComparison.Ordinal));
            var input = OverviewPipeline.Prepare(state.Source.Text, language, names);
            var receipt = new OverviewGenerationReceipt(fingerprint, key, "Sending", null, null); SaveOverviewReceipt(path, receipt);
            var result = await new OverviewProvider(() => ProviderCredential.LoadFor(Root, service)).TranslateAsync(service, input, HttpContext.RequestAborted);
            try
            {
                RequireOverviewSource(folder, item, store.Read(key));
                if (result.Error is not null)
                {
                    store.Fail(key, state.Revision, state.Source.Version, language, result.Error);
                    receipt = receipt with { State = "Failed", Error = result.Error };
                }
                else
                {
                    var saved = store.Save(key, state.Revision, state.Source.Version, language, result.Text!, false,
                        service.Id, service.Model, input.PromptVersion, request.PreserveManual);
                    receipt = receipt with { State = "Completed", CandidateId = saved.Selected(language)!.Id };
                }
            }
            catch (InvalidOperationException) { receipt = receipt with { State = "Conflict", Error = "source_or_revision_changed" }; }
            SaveOverviewReceipt(path, receipt); return receipt;
        }
        finally { OverviewGenerationGate.Release(); }
    });
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/overview/apply")]
    public Task<ActionResult> OverviewApply(Guid folder, Guid item, [FromBody] OverviewApplyRequest request) => GuardAsync(async () =>
    {
        if (!request.ConfirmApply) throw new ArgumentException(); Resolve(folder, item);
        RequireOverviewSource(folder, item, Overviews().Read(Key(folder, item)));
        return await OverviewWrites(folder).ApplyAsync(request.RequestId, Key(folder, item), request.Revision, request.CandidateId,
            request.ExpectedDisplay, HttpContext.RequestAborted);
    });
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/overview/restore")]
    public Task<ActionResult> OverviewRestore(Guid folder, Guid item, [FromBody] OverviewRestoreRequest request) => GuardAsync(async () =>
    {
        if (!request.ConfirmRestore) throw new ArgumentException(); Resolve(folder, item);
        return await OverviewWrites(folder).RestoreAsync(request.RequestId, Key(folder, item), request.ApplicationId,
            request.AcceptObserved, HttpContext.RequestAborted);
    });
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/overview/operations/{id:guid}/recover")]
    public Task<ActionResult> OverviewRecover(Guid folder, Guid item, Guid id) => GuardAsync(async () =>
    { Resolve(folder, item); return await OverviewWrites(folder).RecoverAsync(id, Key(folder, item), HttpContext.RequestAborted); });
    [HttpGet("libraries/{folder:guid}/items/{item:guid}/overview/operations")]
    public ActionResult OverviewOperationList(Guid folder, Guid item) => Guard(() =>
    { Resolve(folder, item); return OverviewWrites(folder).List(Key(folder, item)); });
}
