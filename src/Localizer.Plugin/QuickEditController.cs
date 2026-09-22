using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Localizer.Core;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record WorkbenchSaveApplyRequest(Guid RequestId, [Required] string Language, Guid ItemId,
    long SourceId, string? CandidateId, long Revision, long SelectionRevision,
    [Required, StringLength(64, MinimumLength = 64)] string Observation,
    [Required, StringLength(8000)] string Text, bool Apply = true);

public sealed partial class AdminController
{
    private sealed record QuickEditReceipt(string Fingerprint, LanguagePreviewEntry? Preview);

    // A deliberate administrator edit accepts exactly the submitted revision and text.
    // The saved preview and the normal operation journal retain the original expected display value.
    [HttpPost("libraries/{folder:guid}/workbench/save-apply")]
    public Task<ActionResult> WorkbenchSaveApply(Guid folder, [FromBody] WorkbenchSaveApplyRequest request) => GuardAsync(async () =>
    {
        Nonempty(request.RequestId); Nonempty(request.ItemId);
        var language = Languages.Parse(request.Language);
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 8000) throw new ArgumentException();
        var store = Store(); var key = Key(folder, request.ItemId);
        var id = request.RequestId.ToString("N");
        var fingerprint = Hash(JsonSerializer.Serialize(new { Scope = Scope(folder), Request = request }));
        var path = Path.Combine(Root, "quick-edits", id + ".json");
        LanguagePreviewEntry preview;
        lock (Gate)
        {
            Resolve(folder, request.ItemId);
            if (System.IO.File.Exists(path))
            {
                var receipt = JsonSerializer.Deserialize<QuickEditReceipt>(System.IO.File.ReadAllText(path))!;
                if (receipt.Fingerprint != fingerprint) throw new IdempotencyConflictException();
                // Interruption between editing and freezing the preview requires a fresh visible review.
                preview = receipt.Preview ?? throw new RevisionConflictException();
            }
            else
            {
                if (store.FindOperation(id) is not null) throw new IdempotencyConflictException();
                var movie = Resolve(folder, request.ItemId);
                var source = RequireSource(folder, movie, store);
                var expectedName = movie.Name;
                if (source.Id != request.SourceId || Observation(movie, source) != request.Observation)
                    throw new RevisionConflictException();
                var row = WorkbenchItem(folder, movie, store.ReadWorkbench(Scope(folder), language), WorkbenchSaversDisabled(folder));
                var createManual = request.CandidateId is null;
                if (createManual && (store.CampaignTranslationReservations(Scope(folder), language).ContainsKey(key.ItemId)
                    || Campaigns().List().Any(x => x.Scope == Scope(folder)
                        && x.State is not ("Completed" or "CompletedWithErrors" or "Cancelled")
                        && x.Items.Any(item => item.ItemId == request.ItemId && item.Field == "Name" && (item.Language ?? x.Language) == language.Tag()))))
                    throw new OperationValidationException("manual_correction_task_unsettled");
                if ((createManual ? row.Status != "untranslated" : row.Status is not ("review" or "ready" or "applied"))
                    || !WorkbenchSaversDisabled(folder))
                    throw new OperationValidationException("quick_edit_needs_review");
                var selected = store.GetSelected(source.Id, language);
                if (createManual ? selected is not null || request.Revision != 0 || request.SelectionRevision != 0
                    : selected is null || selected.Candidate.Id != request.CandidateId
                        || selected.Candidate.Revision != request.Revision || selected.Revision != request.SelectionRevision)
                    throw new RevisionConflictException();
                WriteQuickEdit(path, new(fingerprint, null));
                Candidate candidate;
                if (createManual)
                {
                    var spec = new GenerationSpec("manual://local", "human", "manual-quick-v1", "none", "{}",
                        JsonSerializer.Serialize(new { request.RequestId }), "Administrator supplied and accepted this title; no model request.");
                    candidate = store.SaveGenerated(source.Id, language, request.Text, spec);
                    // Mark explicit human authorship, including when the supplied text equals the original.
                    candidate = store.Edit(candidate.Id, candidate.Revision, request.Text);
                    selected = store.GetSelected(source.Id, language);
                    if (selected?.Candidate.Id != candidate.Id) throw new RevisionConflictException();
                }
                else candidate = selected!.Candidate.Text == request.Text ? selected.Candidate :
                    store.Edit(request.CandidateId!, request.Revision, request.Text);
                candidate = store.ApproveWorkbenchSelection(key, language, source.Id, candidate.Id,
                    candidate.Revision, selected!.Revision);
                preview = new(key, language, source.DisplayPrefix + candidate.Text == expectedName ?
                    PreviewStatus.AlreadyMatches : PreviewStatus.ReadyForReview, expectedName,
                    source.DisplayPrefix + candidate.Text, source.Id, candidate.Id, candidate.Revision, selected.Revision);
                WriteQuickEdit(path, new(fingerprint, preview));
            }
        }
        if (!request.Apply)
            return new { Candidate = PublicCandidate(store.GetCandidate(preview.CandidateId!)), Operation = (object?)null };
        var service = new NameOperationService(store, NameTarget(folder, store));
        var existing = store.FindOperation(id);
        var operation = existing is not null && existing.State is OperationState.Prepared or OperationState.Writing or OperationState.Retryable
            ? await service.ResumeAsync(id, Scope(folder), HttpContext.RequestAborted)
            : await service.ApplyAsync(id, Scope(folder), preview, HttpContext.RequestAborted);
        return new { Candidate = PublicCandidate(store.GetCandidate(preview.CandidateId!)), Operation = PublicOperation(operation) };
    });

    private static void WriteQuickEdit(string path, QuickEditReceipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, receipt); stream.Flush(true); }
        System.IO.File.Move(temporary, path, true);
    }
}
