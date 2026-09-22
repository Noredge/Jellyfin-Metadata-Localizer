using Jellyfin.Data.Enums;
using Localizer.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed partial class AdminController
{
    private WorkbenchRow[] SavedLanguageRows(Guid folder, TargetLanguage language)
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var raw = library.GetItemList(new InternalItemsQuery { ParentId = folder, Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie], Limit = WorkbenchMaximum + 1 });
        if (raw.Count > WorkbenchMaximum) throw new OperationValidationException("library_limit_exceeded");
        var data = Store().ReadWorkbench(Scope(folder), language, WorkbenchMaximum);
        var savers = WorkbenchSaversDisabled(folder);
        return raw.OfType<Movie>().Where(x => Belongs(x, folder)).Select(x => WorkbenchItem(folder, x, data, savers))
            .OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
    }

    [HttpGet("libraries/{folder:guid}/saved-language")]
    public ActionResult SavedLanguage(Guid folder, [FromQuery] string language = "zh-Hans") => CampaignGuard(() =>
    {
        var target = Languages.Parse(language); var rows = SavedLanguageRows(folder, target);
        var overviewStore = Overviews();
        var overviewSaved = rows.Count(x => overviewStore.Read(Key(folder, x.Id)).Selected(target) is not null);
        return new { Language = target.Tag(), Total = rows.Length, OverviewSaved = overviewSaved,
            Ready = rows.Count(x => x.CanApply), AlreadyMatches = rows.Count(x => x.Status == "applied"),
            Missing = rows.Count(x => x.Status is "new" or "untranslated"),
            NeedsReview = rows.Count(x => x.Status == "review"),
            Blocked = rows.Count(x => x.Status is "changed" or "attention") };
    });

    private object StartSavedLanguage(Guid folder, CampaignStartRequest request, CampaignWorker campaigns)
    {
        var language = Languages.Parse(request.Language); var store = Store();
        var rows = SavedLanguageRows(folder, language).Where(x => x.CanApply).ToArray();
        if (rows.Length > 5000) throw new OperationValidationException("campaign_limit_exceeded");
        var items = rows.Select(row =>
        {
            var key = Key(folder, row.Id); var source = store.GetCurrentSource(key) ?? throw new SourceChangedException();
            var selected = store.GetSelected(source.Id, language) ?? throw new SourceChangedException();
            if (source.Id != row.SourceId || selected.Candidate.Id != row.Candidate?.Id
                || selected.Candidate.Revision != row.Candidate.Revision || selected.Revision != row.SelectionRevision
                || !selected.Candidate.Approved) throw new RevisionConflictException();
            // Freeze both versions and observed display at request time. The worker must never choose
            // a newer candidate or adopt an external title while waiting behind another item.
            var preview = new LanguagePreviewEntry(key, language, PreviewStatus.ReadyForReview, row.Name,
                source.DisplayPrefix + selected.Candidate.Text, source.Id, selected.Candidate.Id,
                selected.Candidate.Revision, selected.Revision);
            return new CampaignItem(row.Id, row.Name, row.Observation, Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), Source: source, CandidateId: selected.Candidate.Id, TranslationOrigin: "Reused",
                Acceptance: "previously_accepted", Preview: preview);
        }).ToArray();
        var now = DateTimeOffset.UtcNow.ToString("O"); var journal = Campaigns();
        var campaign = journal.Create(new(request.RequestId, Scope(folder), language.Tag(), "apply_saved",
            items.Length == 0 ? "Completed" : "Running", now, now, items));
        if (items.Length != 0)
        {
            try { campaigns.Run(campaign.Id, new()); }
            catch (OperationBusyException)
            { journal.Change(campaign.Id, x => x with { State = "Paused", ErrorCategory = "WorkerBusy" }); throw; }
        }
        return CampaignView(journal.Read(campaign.Id), campaigns, true);
    }

    [NonAction]
    internal async Task<NameOperation> ApplySavedLanguageItem(CampaignRecord campaign, int ordinal,
        bool retryFailed, CancellationToken token)
    {
        var folder = Guid.Parse(campaign.Scope.LibraryId); var journal = Campaigns();
        var item = journal.Read(campaign.Id).Items[ordinal]; var store = Store();
        if (item.Source is null || item.Preview is null || item.CandidateId is null
            || item.Preview.Key != item.Source.Key || item.Preview.CandidateId != item.CandidateId
            || item.Preview.Language != Languages.Parse(item.Language ?? campaign.Language)) throw new SourceChangedException();
        var service = new NameOperationService(store, NameTarget(folder, store));
        var existing = store.FindOperation(item.OperationId);
        if (existing is not null)
        {
            if (existing.Key != item.Source.Key || existing.CandidateId != item.CandidateId) throw new IdempotencyConflictException();
            if (retryFailed && existing.State == OperationState.Blocked
                && existing.ErrorCategory is "NameLocked" or "UnsupportedSaverConfiguration" or "AdapterGuardBlocked")
                item = journal.ChangeItem(campaign.Id, ordinal, x => x with { OperationId = Guid.NewGuid().ToString("N"),
                    PreviousOperationIds = [.. x.PreviousOperationIds ?? [], x.OperationId] }).Items[ordinal];
            else return await service.ResumeAsync(item.OperationId, campaign.Scope, token);
        }
        lock (Gate)
        {
            ValidateCampaignSource(campaign, item, store, Resolve(folder, item.ItemId));
            if (!WorkbenchSaversDisabled(folder)) throw new OperationValidationException("metadata_savers_not_disabled");
            journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Applying" });
        }
        return await service.ApplyAsync(item.OperationId, campaign.Scope, item.Preview!, token);
    }
}
