using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Localizer.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record WorkbenchConfirmItem(Guid ItemId, [Required, StringLength(64, MinimumLength = 64)] string Observation);
public sealed record WorkbenchConfirmRequest([Required, MinLength(1), MaxLength(100)] WorkbenchConfirmItem[] Items, bool ConfirmSources);
public sealed record WorkbenchApproveItem(Guid ItemId, long SourceId, [Required, StringLength(64)] string CandidateId,
    long Revision, long SelectionRevision);
public sealed record WorkbenchApproveRequest([Required] string Language,
    [Required, MinLength(1), MaxLength(100)] WorkbenchApproveItem[] Items, bool ConfirmReview);
public sealed record WorkbenchCandidate(string Id, string Text, string InitialText, long Revision, bool Approved, bool IsHumanEdited);
public sealed record WorkbenchAlternate(string Language, WorkbenchCandidate? Candidate);
public sealed record WorkbenchRow(Guid Id, string Name, string OriginalTitle, string SuggestedOriginal,
    string SuggestedPrefix, string Observation, bool Confirmed, long? SourceId, string Status, string Reason,
    WorkbenchCandidate? Candidate, long? SelectionRevision, string[] Names,
    bool CanConfirm, bool CanTranslate, bool CanApprove, bool CanApply, WorkbenchAlternate? Alternate = null);
public sealed record WorkbenchMutationResult(Guid ItemId, bool Success, string? Error);

public sealed partial class AdminController
{
    private const int WorkbenchMaximum = 100000;
    private static readonly string[] WorkbenchStatuses = ["new", "untranslated", "changed", "review", "ready", "applied", "attention"];

    private WorkbenchRow WorkbenchItem(Guid folder, Movie movie, WorkbenchStoreSnapshot snapshot, bool saversDisabled)
    {
        var key = Key(folder, movie.Id);
        snapshot.Sources.TryGetValue(key, out var source);
        Binding? binding = null; var invalidBinding = false;
        try { binding = ReadBinding(key); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { invalidBinding = true; }
        var confirmed = !invalidBinding && Confirmed(movie, source, binding);
        var original = movie.OriginalTitle ?? "";
        var reliable = !string.IsNullOrWhiteSpace(original) && original.Length <= 8000
            && movie.Name.EndsWith(original, StringComparison.Ordinal) && movie.Name.Length - original.Length <= 500;
        var prefix = reliable ? movie.Name[..^original.Length] : "";
        var selected = source is null ? null : snapshot.Selections.GetValueOrDefault(source.Id);
        var candidate = selected?.Candidate;
        var locked = movie.IsLocked || movie.LockedFields.Contains(MetadataField.Name);
        var pending = snapshot.Pending.Contains(key);
        var status = "attention"; var reason = "original_title_needs_review";
        var expected = source is null ? null : TitleDisplayBaseline.Expected(source,
            binding?.ConfirmedDisplayName, snapshot.RestorePoints.GetValueOrDefault(key));
        var selectedMatches = confirmed && candidate is not null && movie.Name == source!.DisplayPrefix + candidate.Text;
        if (source is not null && !confirmed) { status = "changed"; reason = "source_binding_changed"; }
        else if (invalidBinding || (source is null && binding is not null)) reason = "source_binding_needs_review";
        else if (pending) reason = "pending_operation_needs_review";
        else if (confirmed && movie.Name != expected && !selectedMatches) { status = "changed"; reason = "display_name_changed"; }
        else if (locked) reason = "name_locked";
        else if (source is null && reliable) { status = "new"; reason = "source_not_confirmed"; }
        else if (confirmed)
        {
            if (selectedMatches) { status = "applied"; reason = "selected_translation_matches"; }
            else if (candidate is null) { status = "untranslated"; reason = "no_selected_translation"; }
            else if (!candidate.Approved) { status = "review"; reason = "candidate_needs_review"; }
            else if (!saversDisabled) reason = "metadata_savers_not_disabled";
            else { status = "ready"; reason = "approved_translation_ready"; }
        }
        return new(movie.Id, movie.Name, original, confirmed ? source!.OriginalTitle : original,
            confirmed ? source!.DisplayPrefix : prefix, Observation(movie, source, binding), confirmed, source?.Id, status, reason,
            candidate is null ? null : new(candidate.Id, candidate.Text, candidate.GeneratedText, candidate.Revision,
                candidate.Approved, candidate.IsHumanEdited), selected?.Revision, [], status == "new",
            status == "untranslated", status == "review", status == "ready");
    }

    private bool WorkbenchSaversDisabled(Guid folder) => library.GetVirtualFolders().Single(x =>
        Guid.TryParse(x.ItemId, out var id) && id == folder).LibraryOptions?.MetadataSavers is { Length: 0 };

    [HttpGet("libraries/{folder:guid}/workbench")]
    public ActionResult Workbench(Guid folder, [FromQuery] string language = "zh-Hans", [FromQuery] string status = "all",
        [FromQuery] string? query = null, [FromQuery] int start = 0, [FromQuery] int limit = 50,
        [FromQuery] string? snapshot = null, [FromQuery] bool includeTranslations = false) => Guard(() =>
    {
        var target = Languages.Parse(language);
        if (start is < 0 or > WorkbenchMaximum || limit is < 1 or > 100 || query?.Length > 500
            || snapshot?.Length > 64 || (status is not ("all" or "todo") && !WorkbenchStatuses.Contains(status))) throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        // The explicit sentinel rejects oversized libraries instead of presenting incomplete totals as a whole library.
        var raw = library.GetItemList(new InternalItemsQuery { ParentId = folder, Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie], Limit = WorkbenchMaximum + 1 });
        if (raw.Count > WorkbenchMaximum) throw new InvalidOperationException("Workbench library limit exceeded.");
        var store = Store(); var data = store.ReadWorkbench(Scope(folder), target, WorkbenchMaximum);
        var alternateLanguage = target == TargetLanguage.English ? TargetLanguage.SimplifiedChinese : TargetLanguage.English;
        var alternateData = includeTranslations ? store.ReadWorkbench(Scope(folder), alternateLanguage, WorkbenchMaximum) : null;
        var reservations = store.CampaignTranslationReservations(Scope(folder), target);
        var saversDisabled = WorkbenchSaversDisabled(folder);
        var movies = raw.OfType<Movie>().Where(x => Belongs(x, folder)).ToDictionary(x => x.Id);
        var rows = movies.Values.Select(movie =>
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            var row = WorkbenchItem(folder, movie, data, saversDisabled);
            if (alternateData is not null)
            {
                var alternateSource = alternateData.Sources.GetValueOrDefault(Key(folder, movie.Id));
                var candidate = alternateSource?.Id == row.SourceId && row.SourceId is long sourceId
                    ? alternateData.Selections.GetValueOrDefault(sourceId)?.Candidate : null;
                row = row with { Alternate = new(alternateLanguage == TargetLanguage.English ? "en" : "zh-Hans",
                    candidate is null ? null : new(candidate.Id, candidate.Text, candidate.GeneratedText,
                        candidate.Revision, candidate.Approved, candidate.IsHumanEdited)) };
            }
            return row.Status is "new" or "untranslated" && reservations.ContainsKey(movie.Id.ToString("N"))
                ? row with { Status = "attention", Reason = "earlier_translation_outcome_needs_review", CanConfirm = false, CanTranslate = false }
                : row;
        }).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
        var version = Hash(JsonSerializer.Serialize(new { Scope = Scope(folder), Language = language, Status = status,
            Query = query ?? "", SaversDisabled = saversDisabled, Rows = rows }));
        if (snapshot is not null && snapshot != version) throw new RevisionConflictException();
        var matches = rows.Where(x => (status == "all" || x.Status == status || (status == "todo" && x.Status is "new" or "untranslated")) && (string.IsNullOrEmpty(query)
            || (Guid.TryParse(query, out var requestedId) && x.Id == requestedId)
            || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || x.OriginalTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
            || x.SuggestedOriginal.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (x.Candidate?.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (x.Alternate?.Candidate?.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))).ToArray();
        // People are suggestions for human review, not a completeness claim. Read only the visible page.
        var page = matches.Skip(start).Take(limit).Select(row => row with
        {
            Names = library.GetPeople(movies[row.Id]).Select(x => x.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x) && row.SuggestedOriginal.Contains(x, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()
        }).ToArray();
        var summary = WorkbenchStatuses.ToDictionary(x => x, x => rows.Count(row => row.Status == x));
        summary["todo"] = summary["new"] + summary["untranslated"];
        return new { Snapshot = version, Start = start, NextStart = start + limit < matches.Length ? (int?)(start + limit) : null,
            Total = matches.Length, LibraryTotal = rows.Length,
            Summary = summary, Items = page };
    });

    [HttpPost("libraries/{folder:guid}/workbench/confirm"), RequestSizeLimit(131072)]
    public ActionResult WorkbenchConfirm(Guid folder, [FromBody] WorkbenchConfirmRequest request) => Guard(() =>
    {
        if (!request.ConfirmSources || request.Items is null || request.Items.Length is < 1 or > 100
            || request.Items.Any(x => x is null) || request.Items.Select(x => x.ItemId).Distinct().Count() != request.Items.Length)
            throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var store = Store(); var result = new List<WorkbenchMutationResult>();
        var data = store.ReadWorkbench(Scope(folder), TargetLanguage.SimplifiedChinese);
        var saversDisabled = WorkbenchSaversDisabled(folder);
        foreach (var input in request.Items)
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            result.Add(WorkbenchMutation(input.ItemId, () =>
            {
                var movie = Resolve(folder, input.ItemId); var key = Key(folder, movie.Id);
                if (Observation(movie, store.GetCurrentSource(key)) != input.Observation) throw new RevisionConflictException();
                var row = WorkbenchItem(folder, movie, data, saversDisabled);
                if (!row.CanConfirm) throw new SourceChangedException();
                var source = store.ObserveSource(key, row.SuggestedOriginal, row.SuggestedPrefix);
                WriteBinding(key, new(source.Id, movie.Path, movie.OriginalTitle ?? "", row.Name));
            }));
        }
        return new { Items = result };
    });

    [HttpPost("libraries/{folder:guid}/workbench/approve"), RequestSizeLimit(131072)]
    public ActionResult WorkbenchApprove(Guid folder, [FromBody] WorkbenchApproveRequest request) => Guard(() =>
    {
        var language = Languages.Parse(request.Language);
        if (!request.ConfirmReview || request.Items is null || request.Items.Length is < 1 or > 100
            || request.Items.Any(x => x is null) || request.Items.Select(x => x.ItemId).Distinct().Count() != request.Items.Length)
            throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var store = Store(); var result = new List<WorkbenchMutationResult>();
        var data = store.ReadWorkbench(Scope(folder), language); var saversDisabled = WorkbenchSaversDisabled(folder);
        foreach (var input in request.Items)
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            result.Add(WorkbenchMutation(input.ItemId, () =>
            {
                var movie = Resolve(folder, input.ItemId);
                var source = RequireSource(folder, movie, store);
                if (source.Id != input.SourceId) throw new SourceChangedException();
                var row = WorkbenchItem(folder, movie, data, saversDisabled);
                if (!row.CanApprove) throw new RevisionConflictException();
                store.ApproveWorkbenchSelection(Key(folder, movie.Id), language, input.SourceId,
                    input.CandidateId, input.Revision, input.SelectionRevision);
            }));
        }
        return new { Items = result };
    });

    private static WorkbenchMutationResult WorkbenchMutation(Guid item, Action action)
    {
        try { if (item == Guid.Empty) throw new ArgumentException(); action(); return new(item, true, null); }
        catch (KeyNotFoundException) { return new(item, false, "not_found_in_library"); }
        catch (ArgumentException) { return new(item, false, "invalid_input"); }
        catch (InvalidOperationException) { return new(item, false, "changed_reload_required"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(item, false, "local_state_needs_review"); }
    }
}
