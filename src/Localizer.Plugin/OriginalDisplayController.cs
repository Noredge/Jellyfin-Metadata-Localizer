using Localizer.Jellyfin;
using Localizer.Core;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Localizer.Plugin;

public sealed record OriginalDisplayRequest(Guid RequestId, string Field, string Mode, long PreferenceRevision,
    long SourceRevision, string ExpectedDisplay, bool Confirm);
public sealed record OriginalDisplayReceipt(MediaKey Key, OriginalDisplayRequest Request, string State, string? Outcome = null);

public sealed partial class AdminController
{
    private DisplayPreferenceStore DisplayPreferences() => new(Path.Combine(Root, "display-preferences.db"));
    private static readonly SemaphoreSlim OriginalDisplayGate = new(1, 1);
    private string OriginalReceiptPath(Guid id) => Path.Combine(Root, "original-display-requests", id.ToString("N") + ".json");
    private OriginalDisplayReceipt[] OriginalReceipts(MediaKey key) => !Directory.Exists(Path.Combine(Root, "original-display-requests")) ? [] :
        Directory.EnumerateFiles(Path.Combine(Root, "original-display-requests"), "*.json")
            .Select(x => JsonSerializer.Deserialize<OriginalDisplayReceipt>(System.IO.File.ReadAllText(x))!)
            .Where(x => x.Key == key).ToArray();
    private void SaveOriginalReceipt(OriginalDisplayReceipt receipt)
    {
        var path = OriginalReceiptPath(receipt.Request.RequestId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, receipt); stream.Flush(true); }
        System.IO.File.Move(path + ".tmp", path, true);
    }

    [HttpPost("libraries/{folder:guid}/items/{item:guid}/original-display")]
    public Task<ActionResult> SetOriginalDisplay(Guid folder, Guid item, [FromBody] OriginalDisplayRequest request) => GuardAsync(async () =>
    {
        Nonempty(request.RequestId);
        if (!request.Confirm || request.Field is not ("Name" or "Overview") || request.Mode is not ("Original" or "FollowLibrary")
            || request.PreferenceRevision < 0 || request.SourceRevision < 0 || request.ExpectedDisplay is null) throw new ArgumentException();
        await OriginalDisplayGate.WaitAsync(HttpContext.RequestAborted);
        try
        {
            var movie = Resolve(folder, item); var key = Key(folder, item); var prefs = DisplayPreferences();
            var field = request.Field == "Name" ? DisplayField.Name : DisplayField.Overview;
            var mode = request.Mode == "Original" ? DisplayPreferenceMode.Original : DisplayPreferenceMode.FollowLibrary;
            var path = OriginalReceiptPath(request.RequestId);
            var receipt = System.IO.File.Exists(path) ? JsonSerializer.Deserialize<OriginalDisplayReceipt>(System.IO.File.ReadAllText(path)) : null;
            if (receipt is not null && (receipt.Key != key || receipt.Request != request)) throw new IdempotencyConflictException();
            if (receipt?.State == "Completed") return new { State = "Completed", receipt.Outcome, Preference = prefs.Read(key, field), Replayed = true };
            if (OriginalReceipts(key).Any(x => x.State != "Completed" && x.Request.RequestId != request.RequestId))
                throw new OperationValidationException("original_display_request_pending");
            if (receipt is null)
            {
                if (Store().FindOperation(request.RequestId.ToString("N")) is not null
                    || OverviewWrites(folder).List(key).Any(x => x.Id == request.RequestId.ToString("N"))) throw new IdempotencyConflictException();
                if (prefs.Read(key, field).Revision != request.PreferenceRevision) throw new RevisionConflictException();
                if (Store().ListUnresolvedOperations().Any(x => x.Key == key)
                    || OverviewWrites(folder).List(key).Any(x => x.State is "Prepared" or "Writing")) throw new OperationBusyException();
                if (mode == DisplayPreferenceMode.Original)
                {
                    if (field == DisplayField.Name)
                    {
                        var source = RequireSource(folder, movie, Store());
                        if (source.Id != request.SourceRevision || movie.Name != request.ExpectedDisplay) throw new RevisionConflictException();
                    }
                    else
                    {
                        var state = Overviews().Read(key); RequireOverviewSource(folder, item, state);
                        if (state.Revision != request.SourceRevision || (movie.Overview ?? "") != request.ExpectedDisplay) throw new RevisionConflictException();
                    }
                }
                receipt = new(key, request, "Prepared"); SaveOriginalReceipt(receipt);
            }
            var current = prefs.Read(key, field);
            if (current.Revision == request.PreferenceRevision) prefs.Set(key, field, current.Revision, mode);
            else if (current.Revision != request.PreferenceRevision + 1 || current.Mode != mode) throw new RevisionConflictException();
            string outcome = "PreferenceSaved";
            if (mode == DisplayPreferenceMode.Original)
            {
                if (field == DisplayField.Name)
                {
                    var store = Store(); var operations = new NameOperationService(store, NameTarget(folder, store));
                    var id = request.RequestId.ToString("N"); var existing = store.FindOperation(id);
                    var op = existing is not null ? await operations.ReconcileAsync(id, HttpContext.RequestAborted)
                        : await operations.ApplyOriginalAsync(id, Scope(folder), key, request.SourceRevision, request.ExpectedDisplay, HttpContext.RequestAborted);
                    if (op.State == OperationState.Retryable) op = await operations.CancelPendingAsync(id, HttpContext.RequestAborted);
                    outcome = op.State.ToString();
                    if (op.State is OperationState.Prepared or OperationState.Writing or OperationState.Retryable)
                        return new { State = "Pending", Outcome = outcome, Preference = prefs.Read(key, field) };
                }
                else
                {
                    var operations = OverviewWrites(folder);
                    var op = operations.List(key).Any(x => x.Id == request.RequestId.ToString("N"))
                        ? await operations.RecoverAsync(request.RequestId, key, HttpContext.RequestAborted)
                        : await operations.ApplyOriginalAsync(request.RequestId, key, request.SourceRevision, request.ExpectedDisplay, HttpContext.RequestAborted);
                    outcome = op.State;
                    if (op.State is "Prepared" or "Writing") return new { State = "Pending", Outcome = outcome, Preference = prefs.Read(key, field) };
                }
            }
            SaveOriginalReceipt(receipt with { State = "Completed", Outcome = outcome });
            return new { State = "Completed", Outcome = outcome, Preference = prefs.Read(key, field) };
        }
        catch (InvalidOperationException)
        {
            var path = OriginalReceiptPath(request.RequestId);
            if (System.IO.File.Exists(path))
            {
                var receipt = JsonSerializer.Deserialize<OriginalDisplayReceipt>(System.IO.File.ReadAllText(path))!;
                var key = Key(folder, item);
                if (receipt.Key == key && receipt.Request == request
                    && Store().FindOperation(request.RequestId.ToString("N")) is not { State: OperationState.Prepared or OperationState.Writing or OperationState.Retryable }
                    && !OverviewWrites(folder).List(key).Any(x => x.Id == request.RequestId.ToString("N") && x.State is "Prepared" or "Writing"))
                    SaveOriginalReceipt(receipt with { State = "Completed", Outcome = "NotApplied" });
            }
            throw;
        }
        finally { OriginalDisplayGate.Release(); }
    });

    [HttpGet("libraries/{folder:guid}/items/{item:guid}/original-display")]
    public ActionResult OriginalDisplayPreview(Guid folder, Guid item) => Guard(() =>
    {
        var movie = Resolve(folder, item); var key = Key(folder, item);
        var preferences = DisplayPreferences(); var source = Store().GetCurrentSource(key);
        var titleError = source is null ? "original_title_needs_review" :
            !Confirmed(movie, source, ReadBinding(key)) ? "SourceChanged" : null;
        var title = titleError is null ? source!.DisplayPrefix + source.OriginalTitle : null;
        var overview = JellyfinNfoOverview.Read(movie); var savedOverview = Overviews().Read(key);
        var observed = overview.Error is null && savedOverview.Source is { } previous
            && previous.NfoPath == overview.Path && previous.Hash == overview.Hash;
        object Preference(DisplayField field)
        {
            var value = preferences.Read(key, field);
            return new { value.Revision, Mode = value.Mode.ToString() };
        }
        return new
        {
            ItemId = item,
            Title = new { CurrentDisplay = movie.Name, OriginalDisplay = title,
                SourceId = source?.Id, Error = titleError, Matches = title is not null && title == movie.Name,
                Preference = Preference(DisplayField.Name) },
            Overview = new { CurrentDisplay = movie.Overview ?? "", OriginalDisplay = overview.Text,
                overview.Error, Matches = overview.Text is not null && overview.Text == (movie.Overview ?? ""),
                SourceObserved = observed, SavedRevision = savedOverview.Revision,
                Preference = Preference(DisplayField.Overview) },
            Pending = OriginalReceipts(key).Where(x => x.State != "Completed").Select(x => x.Request).ToArray(),
            ApplyAvailable = true
        };
    });
}
