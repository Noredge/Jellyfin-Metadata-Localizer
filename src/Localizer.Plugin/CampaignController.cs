using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Localizer.Core;
using Localizer.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record CampaignStartRequest(Guid RequestId, [Required] string Language, [Required] string Mode,
    bool ConfirmSend, bool ConfirmApply = false, string? ServiceId = null);
public sealed record CampaignResumeRequest(bool ConfirmSend, bool RetryFailed = false, bool AcceptUncertainRequest = false);

public sealed partial class AdminController
{
    private CampaignStore Campaigns() => new(Root);
    private ActionResult CampaignGuard(Func<object> action)
    {
        lock (Gate)
        {
            try { return Ok(action()); }
            catch (OperationValidationException error) { return Conflict(new { Error = error.Category }); }
            catch (KeyNotFoundException) { return NotFound(new { Error = "not_found_in_library" }); }
            catch (ArgumentException) { return BadRequest(new { Error = "invalid_input" }); }
            catch (InvalidOperationException) { return Conflict(new { Error = "changed_reload_required" }); }
            catch (IOException) { return StatusCode(503, new { Error = "storage_unavailable_check_campaign" }); }
            catch (Microsoft.Data.Sqlite.SqliteException) { return StatusCode(503, new { Error = "storage_unavailable_check_campaign" }); }
        }
    }
    private CampaignRecord OwnedCampaign(Guid folder, Guid id)
    {
        Nonempty(id);
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var campaign = Campaigns().Read(id);
        if (campaign.Scope != Scope(folder)) throw new KeyNotFoundException();
        return campaign;
    }
    private static object CampaignView(CampaignRecord campaign, CampaignWorker campaigns, bool details)
    {
        var active = campaigns.IsRunning(campaign.Id);
        var items = campaign.Items;
        return new
        {
            campaign.Id, campaign.Language, campaign.Mode, campaign.DisplayLanguage, campaign.IndependentDisplay, campaign.OverviewDisplayLanguage, campaign.Retranslate, campaign.PreserveHumanEdits, campaign.IncludeGenres, campaign.IncludeOverviews, campaign.IncludeTitles,
            ServiceId = campaign.Mode is "apply_saved" or "refresh_names" or "library_display" or "field_display" ? null : campaign.Service?.Id ?? "groq",
            Model = campaign.Mode is "apply_saved" or "refresh_names" or "library_display" or "field_display" ? null : campaign.Service?.Model ?? items.FirstOrDefault(x => x.Spec is not null)?.Spec?.Model ?? TitlePipeline.QwenModel,
            State = !active && campaign.State is "Running" or "PauseRequested" or "CancelRequested" ? "Interrupted" : campaign.State,
            WorkerRunning = active, Total = items.Length,
            Movies = items.Select(x => x.ItemId).Distinct().Count(),
            Fields = CampaignStatistics.Fields(campaign),
            Pending = items.Count(x => x.State is not ("Applied" or "Saved" or "Review" or "Skipped" or "Failed" or "Uncertain")),
            Saved = items.Count(x => x.State == "Saved"),
            Languages = items.GroupBy(x => x.Language ?? campaign.Language).Select(g => new { Language = g.Key,
                Total = g.Count(), Applied = g.Count(x => x.State == "Applied"), Saved = g.Count(x => x.State == "Saved"),
                Failed = g.Count(x => x.State is "Failed" or "Uncertain"), Skipped = g.Count(x => x.State == "Skipped") }).ToArray(),
            Translated = items.Count(x => x.CandidateId is not null),
            Applied = items.Count(x => x.State == "Applied"), Review = items.Count(x => x.State == "Review"),
            Skipped = items.Count(x => x.State == "Skipped"), Failed = items.Count(x => x.State is "Failed" or "Uncertain"),
            campaign.ErrorCategory, campaign.CreatedUtc, campaign.UpdatedUtc, campaign.NextRequestUtc,
            Items = (details ? items : []).Select(x => new
            { x.ItemId, x.Name, x.State, x.ErrorCategory, x.CandidateId, x.OperationId, x.Acceptance, Language = x.Language ?? campaign.Language, x.Field }).ToArray()
        };
    }

    [HttpGet("libraries/{folder:guid}/campaigns")]
    public ActionResult ListCampaigns(Guid folder, [FromServices] CampaignWorker campaigns) => CampaignGuard(() =>
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        return Campaigns().List().Where(x => x.Scope == Scope(folder)).Take(50).Select(x => CampaignView(x, campaigns, false)).ToArray();
    });

    [HttpGet("libraries/{folder:guid}/campaigns/{id:guid}")]
    public ActionResult ReadCampaign(Guid folder, Guid id, [FromServices] CampaignWorker campaigns) => CampaignGuard(() =>
        CampaignView(OwnedCampaign(folder, id), campaigns, true));

    [HttpPost("libraries/{folder:guid}/campaigns")]
    public ActionResult StartCampaign(Guid folder, [FromBody] CampaignStartRequest request,
        [FromServices] CampaignWorker campaigns) => CampaignGuard(() =>
    {
        Nonempty(request.RequestId); var language = Languages.Parse(request.Language);
        if (request.Mode is not ("auto_apply" or "review" or "apply_saved")
            || (request.Mode != "apply_saved" && !request.ConfirmSend)
            || (request.Mode is "auto_apply" or "apply_saved" && !request.ConfirmApply)) throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var journal = Campaigns();
        var previous = journal.List().SingleOrDefault(x => x.Id == request.RequestId);
        if (previous is not null)
        {
            if (previous.Scope != Scope(folder) || previous.Language != language.Tag() || previous.Mode != request.Mode)
                throw new IdempotencyConflictException();
            if (request.ServiceId is not null && request.ServiceId != (previous.Service?.Id ?? "groq"))
                throw new IdempotencyConflictException();
            return CampaignView(previous, campaigns, true);
        }
        if (request.Mode is "auto_apply" or "apply_saved" && !WorkbenchSaversDisabled(folder))
            throw new OperationValidationException("metadata_savers_not_disabled");
        if (request.Mode == "apply_saved") return StartSavedLanguage(folder, request, campaigns);
        var selectedService = Services().Select(request.ServiceId);
        var nameSnapshot = DiscoverPersonNames(folder);
        using var rulesStream = typeof(Plugin).Assembly.GetManifestResourceStream("Localizer.Rules." + language.Tag() + ".json")!;
        var frozenRules = JsonSerializer.Deserialize<TitleRules>(rulesStream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var raw = library.GetItemList(new InternalItemsQuery { ParentId = folder, Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie], Limit = WorkbenchMaximum + 1 });
        if (raw.Count > WorkbenchMaximum) throw new OperationValidationException("library_limit_exceeded");
        var store = Store(); var data = store.ReadWorkbench(Scope(folder), language);
        var reservations = store.CampaignTranslationReservations(Scope(folder), language);
        var savers = WorkbenchSaversDisabled(folder);
        var items = raw.OfType<Movie>().Where(x => Belongs(x, folder))
            .Select(x => WorkbenchItem(folder, x, data, savers))
            .Where(x => (x.CanConfirm || x.CanTranslate)
                && !reservations.ContainsKey(x.Id.ToString("N"))
                && (x.SourceId is null || store.ListCandidates(x.SourceId.Value, language).Count == 0))
            .OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id)
            .Select(x => new CampaignItem(x.Id, x.Name, x.Observation, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")))
            .ToArray();
        if (items.Length > 5000) throw new OperationValidationException("campaign_limit_exceeded");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var campaign = journal.Create(new(request.RequestId, Scope(folder), language.Tag(), request.Mode,
            items.Length == 0 ? "Completed" : "Running", now, now, items, Service: selectedService, Names: nameSnapshot, Rules: frozenRules));
        if (items.Length != 0)
        {
            try { campaigns.Run(campaign.Id, new()); }
            catch (OperationBusyException)
            { journal.Change(campaign.Id, x => x with { State = "Paused", ErrorCategory = "WorkerBusy" }); throw; }
        }
        return CampaignView(journal.Read(campaign.Id), campaigns, true);
    });

    [HttpPost("libraries/{folder:guid}/campaigns/{id:guid}/pause")]
    public ActionResult PauseCampaign(Guid folder, Guid id, [FromServices] CampaignWorker campaigns) => CampaignGuard(() =>
    {
        OwnedCampaign(folder, id);
        var next = Campaigns().Change(id, x => x.State is "Completed" or "CompletedWithErrors" or "Cancelled" or "CancelRequested" ? x :
            x with { State = campaigns.IsRunning(id) ? "PauseRequested" : "Paused", ErrorCategory = null });
        return CampaignView(next, campaigns, true);
    });

    [HttpPost("libraries/{folder:guid}/campaigns/{id:guid}/cancel")]
    public ActionResult CancelCampaign(Guid folder, Guid id, [FromServices] CampaignWorker campaigns) => CampaignGuard(() =>
    {
        OwnedCampaign(folder, id);
        var next = Campaigns().Change(id, x => x.State is "Completed" or "CompletedWithErrors" or "Cancelled" ? x :
            x with { State = campaigns.IsRunning(id) ? "CancelRequested" : "Cancelled", ErrorCategory = null });
        return CampaignView(next, campaigns, true);
    });

    [HttpPost("libraries/{folder:guid}/campaigns/{id:guid}/resume")]
    public ActionResult ResumeCampaign(Guid folder, Guid id, [FromBody] CampaignResumeRequest request,
        [FromServices] CampaignWorker campaigns) => CampaignGuard(() =>
    {
        var campaign = OwnedCampaign(folder, id);
        if (campaign.Mode is not ("apply_saved" or "refresh_names" or "library_display" or "field_display") && !request.ConfirmSend) throw new ArgumentException();
        if (campaign.State == "Completed") return CampaignView(campaign, campaigns, true);
        if (campaign.State is "Cancelled" or "CancelRequested") throw new OperationValidationException("campaign_cancelled");
        campaigns.Run(id, new(request.RetryFailed, request.AcceptUncertainRequest));
        return CampaignView(Campaigns().Read(id), campaigns, true);
    });

    // These methods are also used by the hosted worker. They have no HttpContext or internal HTTP dependency.
    [NonAction]
    internal CampaignItem PrepareCampaignItem(CampaignRecord campaign, int ordinal)
    {
        if (campaign.Mode == "library_translate") return PrepareLibraryItem(campaign, ordinal);
        lock (Gate)
        {
            var folder = Guid.Parse(campaign.Scope.LibraryId); var language = Languages.Parse(campaign.Language);
            if (Scope(folder) != campaign.Scope) throw new SourceChangedException();
            var journal = Campaigns(); var item = journal.Read(campaign.Id).Items[ordinal];
            var store = Store(); var movie = Resolve(folder, item.ItemId); var key = Key(folder, item.ItemId);
            if (store.CampaignTranslationReservations(campaign.Scope, language).TryGetValue(key.ItemId, out var reservations)
                && reservations.Any(x => x != item.JobId))
                throw new OperationValidationException("earlier_translation_outcome_needs_review");
            if (item.State == "Pending")
            {
                var current = store.GetCurrentSource(key);
                var observedPath = movie.Path;
                var observedOriginal = movie.OriginalTitle ?? "";
                if (Observation(movie, current) != item.Observation) throw new RevisionConflictException();
                var row = WorkbenchItem(folder, movie, store.ReadWorkbench(campaign.Scope, language), WorkbenchSaversDisabled(folder));
                if (row.Observation != item.Observation || row.Name != item.Name
                    || row.OriginalTitle != observedOriginal || movie.Path != observedPath)
                    throw new RevisionConflictException();
                if ((!row.CanConfirm && !row.CanTranslate)
                    || (current is not null && store.ListCandidates(current.Id, language).Count != 0))
                    throw new OperationValidationException("translation_already_exists_or_changed");
                var planned = current ?? new SourceSnapshot(0, key, 0, row.SuggestedOriginal, row.SuggestedPrefix, "");
                var names = library.GetPeople(movie).Select(x => x.Name)
                    .Where(x => !string.IsNullOrWhiteSpace(x) && (campaign.Service is not null || planned.OriginalTitle.Contains(x, StringComparison.Ordinal)))
                    .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (names.Length > 2000 || names.Any(x => x.Length > 200)) throw new ArgumentException();
                using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Localizer.Rules." + language.Tag() + ".json")!;
                var rules = campaign.Rules ?? JsonSerializer.Deserialize<TitleRules>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                // Missing snapshots identify a legacy task; never adopt today's defaults on resume.
                var spec = campaign.Service is null ? TitlePipeline.Prepare(planned, language, names, rules)
                    : PrepareConfiguredTitle(planned, language, names, rules, campaign.Service, campaign.Names ?? PersonNameSnapshot.Empty);
                var context = JsonNode.Parse(spec.ContextJson)!.AsObject();
                // Unique local provenance prevents another job's cached/generated candidate becoming ours.
                // The provider still receives exactly the existing protected source and prompt.
                context["CampaignId"] = JsonValue.Create(campaign.Id);
                context["CampaignItemId"] = JsonValue.Create(item.ItemId);
                spec = spec with { ContextJson = context.ToJsonString() };
                item = journal.ChangeItem(campaign.Id, ordinal, x => x with
                    { State = "Preparing", Source = planned, Spec = spec, ErrorCategory = null }).Items[ordinal];
                var source = current ?? store.ObserveSource(key, row.SuggestedOriginal, row.SuggestedPrefix);
                if (current is null) WriteBinding(key, new(source.Id, observedPath, observedOriginal, row.Name));
                item = journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Prepared", Source = source }).Items[ordinal];
            }
            else if (item.State == "Preparing")
            {
                // If confirmation was interrupted, resume only an already matching complete binding.
                // A half-written binding or a changed display remains an exception for the administrator.
                var source = RequireSource(folder, movie, store);
                if (item.Source is null || source.OriginalTitle != item.Source.OriginalTitle || source.DisplayPrefix != item.Source.DisplayPrefix)
                    throw new SourceChangedException();
                item = journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Prepared", Source = source }).Items[ordinal];
            }
            ValidateCampaignSource(campaign, item, store, movie);
            if (item.Spec is null || item.Source is null) throw new OperationValidationException("campaign_input_missing");
            var candidates = store.ListCandidates(item.Source.Id, language);
            if (candidates.Any(x => x.ProvenanceJson != JsonSerializer.Serialize(item.Spec)))
                throw new OperationValidationException("candidate_changed_preserved");
            var job = ExistingPreparationJob(store, item.JobId);
            if (job is null)
            {
                if (candidates.Count != 0) throw new OperationValidationException("translation_already_exists");
                store.CreateTranslationJob(item.JobId, campaign.Scope, language, [new(key, item.Spec)], campaign.Service?.Policy() ?? TranslationPolicy.AdaptiveGroq());
            }
            else
            {
                var input = store.GetTranslationItems(item.JobId).Single();
                if (job.Scope != campaign.Scope || job.Language != language || input.Source != item.Source || input.Spec != item.Spec)
                    throw new IdempotencyConflictException();
            }
            return item;
        }
    }

    private void ValidateCampaignSource(CampaignRecord campaign, CampaignItem item, CandidateStore store, Movie movie)
    {
        if (campaign.Scope.ServerId != host.SystemId || item.Source is null
            || RequireSource(Guid.Parse(campaign.Scope.LibraryId), movie, store).Id != item.Source.Id)
            throw new SourceChangedException();
        var ownOtherLanguage = campaign.Mode == "library_translate" && item.Language != campaign.DisplayLanguage
            && Campaigns().Read(campaign.Id).Items.Any(x => x.ItemId == item.ItemId && x.Field == "Name" && x.State == "Applied"
                && x.Language == campaign.DisplayLanguage && x.Source?.Id == item.Source.Id
                && store.GetRestorePoint(item.Source.Key)?.Application.Id == x.OperationId
                && store.FindOperation(x.OperationId)?.PlannedValue == movie.Name);
        if ((movie.Name != item.Name && !ownOtherLanguage) || movie.IsLocked || movie.LockedFields.Contains(MediaBrowser.Model.Entities.MetadataField.Name))
            throw new OperationValidationException("display_changed_or_locked_preserved");
    }

    [NonAction]
    internal async Task<NameOperation> ApplyCampaignItem(CampaignRecord campaign, int ordinal, bool retryFailed, CancellationToken token)
    {
        var folder = Guid.Parse(campaign.Scope.LibraryId); var language = Languages.Parse(campaign.Language);
        var journal = Campaigns(); var item = journal.Read(campaign.Id).Items[ordinal];
        var store = Store(); var target = NameTarget(folder, store); var service = new NameOperationService(store, target);
        var existing = store.FindOperation(item.OperationId);
        if (existing is not null)
        {
            if (item.Preview is null || existing.Key != item.Source?.Key || existing.CandidateId != item.CandidateId)
                throw new IdempotencyConflictException();
            // A transient guard rejection is terminal in the low-level journal. Explicit retry may
            // create a new intent using the SAME frozen preview; preserve the old ID for audit.
            // Conflicts never get a fresh preview or silently adopt a changed current title.
            if (retryFailed && existing.State == OperationState.Blocked
                && existing.ErrorCategory is "NameLocked" or "UnsupportedSaverConfiguration" or "AdapterGuardBlocked")
            {
                item = journal.ChangeItem(campaign.Id, ordinal, x => x with
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    PreviousOperationIds = [.. x.PreviousOperationIds ?? [], x.OperationId]
                }).Items[ordinal];
            }
            else return await service.ResumeAsync(item.OperationId, campaign.Scope, token);
        }
        lock (Gate)
        {
            var movie = Resolve(folder, item.ItemId); ValidateCampaignSource(campaign, item, store, movie);
            if (!WorkbenchSaversDisabled(folder)) throw new OperationValidationException("metadata_savers_not_disabled");
            if (item.CandidateId is null || item.Spec is null || item.Source is null) throw new SourceChangedException();
            var candidate = store.GetCandidate(item.CandidateId);
            if (candidate.SourceId != item.Source.Id || candidate.ProvenanceJson != JsonSerializer.Serialize(item.Spec)
                || candidate.IsHumanEdited || (item.Acceptance is null && (candidate.Approved || candidate.Revision != 1)))
                throw new OperationValidationException("candidate_changed_preserved");
            // Flush the explicit automatic-acceptance intent before the existing technical approval gate.
            item = journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Accepting", Acceptance = "automatic" }).Items[ordinal];
            store.AcceptCampaignCandidate(item.Source!.Id == candidate.SourceId ? item.Source.Key : throw new SourceChangedException(),
                language, item.Source.Id, candidate.Id);
        }
        if (item.Preview is null)
        {
            var current = await target.ReadAsync(item.Source!.Key, token) ?? throw new SourceChangedException();
            if (current.Movie.CurrentName != item.Name) throw new OperationValidationException("display_changed_preserved");
            var preview = new LanguagePreview(store).Build(campaign.Scope, language, [current.Movie]).Single();
            // Freeze the exact automatic acceptance revision, never adopt a later manual change.
            if (preview.CandidateId != item.CandidateId || preview.CandidateRevision != 2 || preview.SelectionRevision != 1
                || preview.Status is not (PreviewStatus.ReadyForReview or PreviewStatus.AlreadyMatches))
                throw new OperationValidationException("candidate_changed_preserved");
            item = journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Applying", Preview = preview }).Items[ordinal];
        }
        return await service.ApplyAsync(item.OperationId, campaign.Scope, item.Preview!, token);
    }
}
