using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Localizer.Core;
using Localizer.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record SelectionRequest(long Revision, long SelectionRevision);
public sealed record NamePreviewRequest([Required] string Language);
public sealed record NameApplyRequest(Guid OperationId, Guid PreviewId, bool ConfirmApply);
public sealed record NameRestoreRequest(Guid OperationId, Guid ApplicationId, bool ConfirmRestore,
    bool AcceptUncertainAttribution = false);
public sealed record NameRecoveryRequest(bool ConfirmResume = false);
public sealed record SavedNamePreview(Guid Id, DateTimeOffset CreatedUtc, LanguagePreviewEntry Entry);

public sealed partial class AdminController
{
    private LibraryKey Scope(Guid folder) => new(host.SystemId, folder.ToString("N"));
    private INameTarget NameTarget(Guid folder, CandidateStore store) => new PreferenceNameTarget(new JellyfinNameTarget(library, host,
        () => new HashSet<Guid> { folder }, key =>
        {
            var source = store.GetCurrentSource(key);
            var binding = ReadBinding(key);
            return source is null || binding?.SourceId != source.Id ? null :
                new(binding.Path, source.DisplayPrefix, source.OriginalTitle, binding.ObservedOriginalTitle);
        }), DisplayPreferences());
    private static void Nonempty(Guid id) { if (id == Guid.Empty) throw new ArgumentException(); }
    private string PreviewPath(Guid id) => Path.Combine(Root, "name-previews", id.ToString("N") + ".json");
    private SavedNamePreview ReadPreview(Guid id, MediaKey key)
    {
        Nonempty(id);
        var path = PreviewPath(id);
        if (!System.IO.File.Exists(path)) throw new KeyNotFoundException();
        var saved = JsonSerializer.Deserialize<SavedNamePreview>(System.IO.File.ReadAllText(path))!;
        if (saved.Id != id || saved.Entry.Key != key) throw new KeyNotFoundException();
        return saved;
    }
    private static NameOperation OwnedOperation(CandidateStore store, Guid id, MediaKey key)
    {
        Nonempty(id);
        var operation = store.GetOperation(id.ToString("N"));
        if (operation.Key != key) throw new KeyNotFoundException();
        return operation;
    }
    private static object PublicOperation(NameOperation op) => new { op.Id, Kind = op.Kind.ToString(),
        State = op.State.ToString(), Language = op.OriginalDisplay ? null : op.Language.Tag(), op.OriginalDisplay, op.BeforeValue, op.PlannedValue,
        op.ParentApplyId, op.ObservedOnly, op.ErrorCategory, op.CreatedUtc, op.UpdatedUtc };
    private async Task<ActionResult> GuardAsync(Func<Task<object>> work)
    {
        try { return Ok(await work()); }
        catch (OperationValidationException error) { return Conflict(new { Error = error.Category }); }
        catch (KeyNotFoundException) { return NotFound(new { Error = "not_found_in_library" }); }
        catch (ArgumentException) { return BadRequest(new { Error = "invalid_input" }); }
        catch (InvalidOperationException) { return Conflict(new { Error = "changed_reload_required" }); }
        catch (IOException) { return StatusCode(503, new { Error = "storage_unavailable_check_operation" }); }
    }

    [HttpPost("libraries/{folder:guid}/items/{item:guid}/candidates/{id}/select")]
    public ActionResult Select(Guid folder, Guid item, string id, [FromBody] SelectionRequest request) => Guard(() =>
    {
        var store = Store();
        var source = RequireSource(folder, Resolve(folder, item), store);
        var candidate = store.GetCandidate(id);
        if (candidate.SourceId != source.Id) throw new SourceChangedException();
        if (candidate.Revision != request.Revision) throw new RevisionConflictException();
        var selected = store.Select(id, request.SelectionRevision);
        return new { SelectedCandidateId = selected.Candidate.Id, SelectionRevision = selected.Revision };
    });

    [HttpPost("libraries/{folder:guid}/items/{item:guid}/name/preview")]
    public Task<ActionResult> PreviewName(Guid folder, Guid item, [FromBody] NamePreviewRequest request) => GuardAsync(async () =>
    {
        Resolve(folder, item);
        var store = Store();
        var target = NameTarget(folder, store);
        var current = await target.ReadAsync(Key(folder, item), HttpContext.RequestAborted);
        if (current is null) throw new SourceChangedException();
        var preview = new LanguagePreview(store).Build(Scope(folder), Languages.Parse(request.Language), [current.Movie]).Single();
        var saved = new SavedNamePreview(Guid.NewGuid(), DateTimeOffset.UtcNow, preview);
        Directory.CreateDirectory(Path.GetDirectoryName(PreviewPath(saved.Id))!);
        // The client submits only this opaque ID; it cannot replace the displayed diff or source revisions.
        using (var stream = new FileStream(PreviewPath(saved.Id), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, saved); stream.Flush(true); }
        return new { PreviewId = saved.Id, Status = preview.Status.ToString(),
            preview.ExpectedCurrentName, preview.ProposedName, Language = preview.Language.Tag(),
            current.MetadataSaversExplicitlyDisabled,
            CanApply = current.MetadataSaversExplicitlyDisabled && preview.Status == PreviewStatus.ReadyForReview };
    });

    [HttpPost("libraries/{folder:guid}/items/{item:guid}/name/apply")]
    public Task<ActionResult> ApplyName(Guid folder, Guid item, [FromBody] NameApplyRequest request) => GuardAsync(async () =>
    {
        Resolve(folder, item); Nonempty(request.OperationId);
        if (!request.ConfirmApply) throw new ArgumentException();
        var store = Store();
        var saved = ReadPreview(request.PreviewId, Key(folder, item));
        var existing = store.FindOperation(request.OperationId.ToString("N"));
        if (existing is null && saved.CreatedUtc < DateTimeOffset.UtcNow.AddHours(-24)) throw new RevisionConflictException();
        return PublicOperation(await new NameOperationService(store, NameTarget(folder, store))
            .ApplyAsync(request.OperationId.ToString("N"), Scope(folder), saved.Entry, HttpContext.RequestAborted));
    });

    [HttpGet("libraries/{folder:guid}/items/{item:guid}/name/state")]
    public ActionResult NameState(Guid folder, Guid item) => Guard(() =>
    {
        var movie = Resolve(folder, item);
        var store = Store(); var key = Key(folder, item);
        var point = store.GetRestorePoint(key);
        return new { CurrentName = movie.Name,
            Recent = store.ListNameOperations(key).Select(PublicOperation).ToArray(),
            Pending = store.ListUnresolvedOperations().Where(x => x.Key == key).Select(PublicOperation).ToArray(),
            RestorePoint = point is null ? null : new { Application = PublicOperation(point.Application), point.Consumed } };
    });

    [HttpGet("libraries/{folder:guid}/items/{item:guid}/name/operations/{id:guid}")]
    public ActionResult NameOperationStatus(Guid folder, Guid item, Guid id) => Guard(() =>
    {
        Resolve(folder, item);
        return PublicOperation(OwnedOperation(Store(), id, Key(folder, item)));
    });

    [HttpPost("libraries/{folder:guid}/items/{item:guid}/name/restore")]
    public Task<ActionResult> RestoreName(Guid folder, Guid item, [FromBody] NameRestoreRequest request) => GuardAsync(async () =>
    {
        Resolve(folder, item); Nonempty(request.OperationId);
        if (!request.ConfirmRestore) throw new ArgumentException();
        var store = Store();
        var parent = OwnedOperation(store, request.ApplicationId, Key(folder, item));
        return PublicOperation(await new NameOperationService(store, NameTarget(folder, store))
            .RestoreAsync(request.OperationId.ToString("N"), Scope(folder), parent.Id,
                request.AcceptUncertainAttribution, HttpContext.RequestAborted));
    });

    [HttpPost("libraries/{folder:guid}/items/{item:guid}/name/operations/{id:guid}/reconcile")]
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/name/operations/{id:guid}/resume")]
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/name/operations/{id:guid}/cancel")]
    public Task<ActionResult> RecoverName(Guid folder, Guid item, Guid id, [FromBody] NameRecoveryRequest request) => GuardAsync(async () =>
    {
        Resolve(folder, item);
        var store = Store(); var op = OwnedOperation(store, id, Key(folder, item));
        var service = new NameOperationService(store, NameTarget(folder, store));
        // Reconciliation takes the same file lease as writes and cannot race another plugin write request.
        var action = HttpContext.Request.Path.Value!.Split('/').Last();
        var result = action switch
        {
            "reconcile" => await service.ReconcileAsync(op.Id, HttpContext.RequestAborted),
            "cancel" => await service.CancelPendingAsync(op.Id, HttpContext.RequestAborted),
            "resume" when request.ConfirmResume => await service.ResumeAsync(op.Id, Scope(folder), HttpContext.RequestAborted),
            _ => throw new ArgumentException()
        };
        return PublicOperation(result);
    });
}
