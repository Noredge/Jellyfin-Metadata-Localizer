using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Localizer.Core;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record BatchSelection(Guid ItemId, [Required, MinLength(1), MaxLength(2)] string[] Fields);
public sealed record BatchCreateRequest(Guid RequestId, [Required] string Language,
    [Required, MinLength(1), MaxLength(20)] BatchSelection[] Items);
public sealed record BatchRunRequest(bool ConfirmApply, bool AcceptUnknownValues = false);

public sealed partial class AdminController
{
    private WriteBatchStore Batches() => new(Path.Combine(Root, "batches"));
    private WriteBatch OwnedBatch(Guid folder, Guid id)
    {
        Nonempty(id);
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var batch = Batches().Get(id.ToString("N"));
        if (batch.Scope != Scope(folder)) throw new KeyNotFoundException();
        return batch;
    }
    private object BatchView(WriteBatch batch)
    {
        var running = batchWorker.IsRunning(batch.Id);
        return new { batch.Id, Language = batch.Language.Tag(), batch.CreatedUtc, batch.CancelRequested, WorkerRunning = running,
            State = !running && batch.State == BatchState.Running ? "Interrupted" : batch.State.ToString(),
            Summary = batch.Rows.GroupBy(x => x.State.ToString()).ToDictionary(x => x.Key, x => x.Count()),
            Rows = batch.Rows.Select(x => new { x.Ordinal, ItemId = x.Key.ItemId, x.DisplayName, Field = x.Field.ToString(), x.OperationId,
                State = x.State.ToString(), x.ErrorCategory,
                Before = x.Name is not null ? [x.Name.ExpectedCurrentName] : x.Genres?.ExpectedValues ?? [],
                After = x.Name is not null ? x.Name.ProposedName is null ? [] : new[] { x.Name.ProposedName } : x.Genres?.ProposedValues ?? [],
                UnknownValues = x.Genres?.UnknownValues ?? [] }).ToArray() };
    }
    [HttpGet("libraries/{folder:guid}/batches")]
    public ActionResult BatchList(Guid folder) => Guard(() =>
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        return Batches().List(Scope(folder)).Select(BatchView).ToArray();
    });
    [HttpGet("libraries/{folder:guid}/batches/{id:guid}")]
    public ActionResult BatchStatus(Guid folder, Guid id) => Guard(() => BatchView(OwnedBatch(folder, id)));
    [HttpPost("libraries/{folder:guid}/batches")]
    public Task<ActionResult> CreateBatch(Guid folder, [FromBody] BatchCreateRequest request) => GuardAsync(async () =>
    {
        Nonempty(request.RequestId); var language = Languages.Parse(request.Language);
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        if (request.Items.Any(x => x is null || x.ItemId == Guid.Empty || x.Fields is null || x.Fields.Length is < 1 or > 2
            || x.Fields.Distinct().Count() != x.Fields.Length || x.Fields.Any(f => f is not ("Name" or "Genres")))
            || request.Items.Select(x => x.ItemId).Distinct().Count() != request.Items.Length) throw new ArgumentException();
        var fingerprint = Hash(JsonSerializer.Serialize(new { Scope = Scope(folder), request.Language, request.Items }));
        var old = Batches().Find(request.RequestId.ToString("N"));
        if (old is not null)
        {
            if (old.Scope != Scope(folder)) throw new KeyNotFoundException();
            if (old.RequestFingerprint != fingerprint) throw new IdempotencyConflictException();
            return BatchView(old);
        }
        // Validate the complete requested scope before saving any preview; missing sources are per-field skips.
        var movies = request.Items.Select(x => Resolve(folder, x.ItemId)).ToArray();
        var store = Store(); var rows = new List<BatchRow>();
        for (var index = 0; index < request.Items.Length; index++)
        {
            var selection = request.Items[index]; var movie = movies[index]; var key = Key(folder, selection.ItemId);
            foreach (var field in selection.Fields.OrderBy(x => x == "Name" ? 0 : 1))
            {
                LanguagePreviewEntry? name = null; GenrePreviewEntry? genres = null; string? reason;
                if (field == "Name")
                {
                    var snapshot = await NameTarget(folder, store).ReadAsync(key, HttpContext.RequestAborted);
                    name = snapshot is null ? null : new LanguagePreview(store).Build(Scope(folder), language, [snapshot.Movie]).Single();
                    reason = snapshot is null ? "SourceUnconfirmed" : !snapshot.MetadataSaversExplicitlyDisabled ? "UnsupportedSaverConfiguration"
                        : name!.Status is PreviewStatus.ReadyForReview or PreviewStatus.AlreadyMatches ? null : name.Status.ToString();
                }
                else
                {
                    var snapshot = await GenreTarget(folder, store).ReadAsync(key, HttpContext.RequestAborted);
                    genres = snapshot is null ? null : store.BuildGenrePreview(Scope(folder), language, snapshot);
                    reason = snapshot is null ? "SourceUnconfirmed" : !snapshot.MetadataSaversExplicitlyDisabled ? "UnsupportedSaverConfiguration"
                        : genres!.Status is PreviewStatus.ReadyForReview or PreviewStatus.AlreadyMatches ? null : genres.Status.ToString();
                }
                rows.Add(new(rows.Count, key, movie.Name, field == "Name" ? BatchField.Name : BatchField.Genres, Guid.NewGuid().ToString("N"),
                    name, genres, reason is null ? BatchRowState.Pending : BatchRowState.Skipped, reason));
            }
        }
        return BatchView(Batches().Create(new(request.RequestId.ToString("N"), fingerprint, Scope(folder), language,
            DateTimeOffset.UtcNow, BatchState.Ready, false, rows.ToArray())));
    });
    [HttpPost("libraries/{folder:guid}/batches/{id:guid}/run")]
    public ActionResult RunBatch(Guid folder, Guid id, [FromBody] BatchRunRequest request) => Guard(() =>
    {
        var batch = OwnedBatch(folder, id);
        if (!request.ConfirmApply) throw new ArgumentException();
        if (!request.AcceptUnknownValues && batch.Rows.Any(x => x.State is BatchRowState.Pending or BatchRowState.Working or BatchRowState.NeedsReview
            && x.Genres?.UnknownValues.Length > 0)) throw new ArgumentException();
        batchWorker.Run(batch.Id, folder, request.AcceptUnknownValues, false);
        return BatchView(Batches().Get(batch.Id));
    });
    [HttpPost("libraries/{folder:guid}/batches/{id:guid}/reconcile")]
    public ActionResult ReconcileBatch(Guid folder, Guid id) => Guard(() =>
    {
        var batch = OwnedBatch(folder, id); batchWorker.Run(batch.Id, folder, false, true);
        return BatchView(Batches().Get(batch.Id));
    });
    [HttpPost("libraries/{folder:guid}/batches/{id:guid}/cancel")]
    public ActionResult CancelBatch(Guid folder, Guid id) => Guard(() => BatchView(Batches().Cancel(OwnedBatch(folder, id).Id)));
}
