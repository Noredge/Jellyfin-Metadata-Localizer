using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Localizer.Core;
using Localizer.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record LibraryTranslationRequest(Guid RequestId, [Required, MinLength(1), MaxLength(2)] string[] Languages,
    bool ConfirmSend, bool ConfirmApply = false, bool Retranslate = false, bool PreserveHumanEdits = false,
    string? DisplayLanguage = null, bool IncludeGenres = true, string? ServiceId = null, bool IncludeOverviews = false, bool IncludeTitles = true,
    Guid? ItemId = null, bool IndependentDisplay = false, string? OverviewDisplayLanguage = null);
public sealed record LibraryDisplayRequest(Guid RequestId, [Required] string Language, bool ConfirmApply, bool IncludeGenres = true, bool IncludeOverviews = false, bool IncludeTitles = true);

public sealed partial class AdminController
{
    private Movie[] LibraryMovies(Guid folder)
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var raw = library.GetItemList(new InternalItemsQuery { ParentId = folder, Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie], Limit = WorkbenchMaximum + 1 });
        if (raw.Count > WorkbenchMaximum) throw new OperationValidationException("library_limit_exceeded");
        var movies = raw.OfType<Movie>().Where(x => Belongs(x, folder)).OrderBy(x => x.Id).ToArray();
        if (movies.Length > 5000) throw new OperationValidationException("campaign_limit_exceeded");
        return movies;
    }
    private static void RequireIdle(CampaignStore journal)
    {
        if (journal.List().Any(x => x.State is "Running" or "PauseRequested" or "CancelRequested" or "Paused" or "Interrupted"))
            throw new OperationBusyException();
    }
    private static CampaignItem LibraryItem(Movie movie, string language, string field = "Name") =>
        new(movie.Id, movie.Name, "", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Language: language, Field: field);
    private object LaunchLibrary(CampaignRecord campaign, CampaignWorker worker)
    {
        if (campaign.Items.All(x => x.State is "Skipped" or "Saved")) campaign = campaign with { State = "Completed" };
        var journal = Campaigns(); journal.Create(campaign);
        if (campaign.Items.Any(x => x.State is not ("Skipped" or "Saved")))
        {
            try { worker.Run(campaign.Id, new()); }
            catch (OperationBusyException) { journal.Change(campaign.Id, x => x with { State = "Paused", ErrorCategory = "WorkerBusy" }); }
        }
        return CampaignView(journal.Read(campaign.Id), worker, true);
    }
    private CampaignItem PlanGenre(Guid folder, Movie movie, TargetLanguage language, CandidateStore store)
    {
        var item = LibraryItem(movie, language.Tag(), "Genres"); var key = Key(folder, movie.Id);
        var beforeValues = movie.Genres.ToArray();
        var imported = ImportNfoGenre(folder, movie, store);
        if (imported.State == "Blocked") return item with { State = "Skipped", ErrorCategory = imported.Error };
        var source = store.GetCurrentGenreSource(key);
        if (!GenreConfirmed(movie, source, ReadGenreBinding(key)))
            return item with { State = "Skipped", ErrorCategory = "genre_source_unconfirmed" };
        var snapshot = new GenreTargetSnapshot(key, true, movie.IsLocked || movie.LockedFields.Contains(MetadataField.Genres),
            WorkbenchSaversDisabled(folder), beforeValues);
        var preview = store.BuildGenrePreview(Scope(folder), language, snapshot);
        var error = !snapshot.MetadataSaversExplicitlyDisabled ? "UnsupportedSaverConfiguration"
            : preview.UnknownValues.Length > 0 ? "genre_mapping_missing"
            : preview.Status is PreviewStatus.ReadyForReview or PreviewStatus.AlreadyMatches ? null : preview.Status.ToString();
        return item with { GenrePreview = preview, State = error is null ? "Prepared" : "Skipped", ErrorCategory = error };
    }

    [HttpPost("libraries/{folder:guid}/campaigns/library")]
    public ActionResult StartLibraryTranslation(Guid folder, [FromBody] LibraryTranslationRequest request,
        [FromServices] CampaignWorker worker) => CampaignGuard(() =>
    {
        if (!request.IncludeTitles && !request.IncludeOverviews) throw new ArgumentException("NoTranslationFieldsSelected");
        Nonempty(request.RequestId);
        if (request.Languages is null || request.Languages.Length is < 1 or > 2
            || request.Languages.Distinct().Count() != request.Languages.Length || !request.ConfirmSend)
            throw new ArgumentException();
        var languages = request.Languages.Select(Languages.Parse).OrderBy(x => x.Tag(), StringComparer.Ordinal).ToArray();
        if (request.DisplayLanguage is not null && (!request.Languages.Contains(request.DisplayLanguage) || !request.ConfirmApply))
            throw new ArgumentException();
        if (!request.IndependentDisplay && request.OverviewDisplayLanguage is not null) throw new ArgumentException();
        var overviewDisplay = request.IndependentDisplay ? request.OverviewDisplayLanguage : request.DisplayLanguage;
        if (overviewDisplay is not null && (!request.Languages.Contains(overviewDisplay) || !request.ConfirmApply))
            throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var journal = Campaigns(); var fingerprint = Hash(JsonSerializer.Serialize(new { Scope = Scope(folder), Request = request }));
        var old = journal.List().SingleOrDefault(x => x.Id == request.RequestId);
        if (old is not null)
        {
            if (old.RequestFingerprint != fingerprint) throw new IdempotencyConflictException();
            return CampaignView(old, worker, true);
        }
        RequireIdle(journal);
        if ((request.DisplayLanguage is not null || overviewDisplay is not null) && !WorkbenchSaversDisabled(folder))
            throw new OperationValidationException("metadata_savers_not_disabled");
        if (request.ItemId == Guid.Empty) throw new ArgumentException();
        // Resolve validates both library membership and movie type. Never widen a missing item to the library.
        var movies = request.ItemId is Guid itemId ? new[] { Resolve(folder, itemId) } : LibraryMovies(folder);
        var service = Services().Select(request.ServiceId);
        var people = DiscoverPersonNames(folder); var store = Store(); var items = new List<CampaignItem>();
        // Complete the non-displayed language first. Thus both languages observe the same original
        // display, and a successful displayed-language write cannot invalidate the other input.
        foreach (var language in languages.OrderBy(x => x.Tag() == request.DisplayLanguage ? 1 : 0))
        {
            var data = store.ReadWorkbench(Scope(folder), language);
            var reservations = store.CampaignTranslationReservations(Scope(folder), language);
            using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Localizer.Rules." + language.Tag() + ".json")!;
            var rules = JsonSerializer.Deserialize<TitleRules>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            foreach (var movie in movies)
            {
                if (request.IncludeOverviews) items.Add(PlanOverview(folder, movie, language, true, request.Retranslate, request.PreserveHumanEdits, overviewDisplay));
                if (!request.IncludeTitles) continue;
                var item = LibraryItem(movie, language.Tag());
                var row = WorkbenchItem(folder, movie, data, WorkbenchSaversDisabled(folder));
                var error = row.Status is "changed" or "attention" ? row.Reason
                    : reservations.ContainsKey(movie.Id.ToString("N")) ? "earlier_translation_outcome_needs_review"
                    : !request.Retranslate && row.SourceId is not null && store.ListCandidates(row.SourceId.Value, language).Count > 0 ? "translation_already_exists"
                    : request.Retranslate && request.PreserveHumanEdits && row.Candidate?.IsHumanEdited == true ? "human_translation_preserved" : null;
                if (error is not null)
                {
                    // Filling another language must still apply an existing accepted display-language
                    // translation; preserving a human edit also preserves its ability to be displayed.
                    if (language.Tag() == request.DisplayLanguage && error is "translation_already_exists" or "human_translation_preserved"
                        && row.Candidate?.Approved == true && row.Status is "ready" or "applied")
                    {
                        var savedSource = RequireSource(folder, movie, store); var selected = store.GetSelected(savedSource.Id, language)!;
                        items.Add(item with { State = "Prepared", Source = savedSource, CandidateId = selected.Candidate.Id, TranslationOrigin = "Reused",
                            Acceptance = "previously_accepted", Preview = new(Key(folder, movie.Id), language, PreviewStatus.ReadyForReview,
                                movie.Name, savedSource.DisplayPrefix + selected.Candidate.Text, savedSource.Id,
                                selected.Candidate.Id, selected.Candidate.Revision, selected.Revision) });
                    }
                    else items.Add(item with { State = "Skipped", ErrorCategory = error });
                    continue;
                }
                var key = Key(folder, movie.Id); var source = store.GetCurrentSource(key);
                if (source is null)
                {
                    if (!row.CanConfirm) { items.Add(item with { State = "Skipped", ErrorCategory = "original_title_needs_review" }); continue; }
                    // Same reliable OriginalTitle/prefix confirmation as the existing one-click task.
                    source = store.ObserveSource(key, row.SuggestedOriginal, row.SuggestedPrefix);
                    WriteBinding(key, new(source.Id, movie.Path, movie.OriginalTitle ?? "", row.Name));
                }
                else source = RequireSource(folder, movie, store);
                var names = library.GetPeople(movie).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var spec = PrepareConfiguredTitle(source, language, names, rules, service, people);
                var context = JsonNode.Parse(spec.ContextJson)!.AsObject();
                context["CampaignId"] = JsonValue.Create(request.RequestId); context["CampaignItemId"] = JsonValue.Create(movie.Id);
                spec = spec with { ContextJson = context.ToJsonString() };
                items.Add(item with { State = "Prepared", Source = source, Spec = spec, Observation = Observation(movie, source),
                    BaselineHash = store.GetReplacementBaseline(source.Id, language),
                    BaselineSelectionRevision = store.GetSelected(source.Id, language)?.Revision ?? 0 });
            }
        }
        if (request.DisplayLanguage is not null && request.IncludeGenres)
            items.AddRange(movies.Select(x => PlanGenre(folder, x, Languages.Parse(request.DisplayLanguage), store)));
        var now = DateTimeOffset.UtcNow.ToString("O");
        items = items.OrderBy(x => x.Language == (x.Field == "Overview" ? overviewDisplay : request.DisplayLanguage) ? 1 : 0).ToList();
        return LaunchLibrary(new(request.RequestId, Scope(folder), languages.Length == 2 ? "multi" : languages[0].Tag(),
            "library_translate", items.Any(x => x.State != "Skipped") ? "Running" : "Completed", now, now, items.ToArray(),
            Service: service, Names: people, RequestFingerprint: fingerprint, DisplayLanguage: request.DisplayLanguage,
            Retranslate: request.Retranslate, PreserveHumanEdits: request.PreserveHumanEdits, IncludeGenres: request.IncludeGenres, IncludeOverviews: request.IncludeOverviews, IncludeTitles: request.IncludeTitles,
            IndependentDisplay: request.IndependentDisplay, OverviewDisplayLanguage: overviewDisplay), worker);
    });

    [HttpPost("libraries/{folder:guid}/campaigns/library-display")]
    public ActionResult StartLibraryDisplay(Guid folder, [FromBody] LibraryDisplayRequest request,
        [FromServices] CampaignWorker worker) => CampaignGuard(() =>
    {
        if (!request.IncludeTitles && !request.IncludeOverviews) throw new ArgumentException("NoTranslationFieldsSelected");
        Nonempty(request.RequestId); var language = Languages.Parse(request.Language);
        if (!request.ConfirmApply) throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var journal = Campaigns(); var fingerprint = Hash(JsonSerializer.Serialize(new { Scope = Scope(folder), Request = request }));
        var previous = journal.List().SingleOrDefault(x => x.Id == request.RequestId);
        if (previous is not null)
        {
            if (previous.RequestFingerprint != fingerprint) throw new IdempotencyConflictException();
            return CampaignView(previous, worker, true);
        }
        RequireIdle(journal);
        if (!WorkbenchSaversDisabled(folder)) throw new OperationValidationException("metadata_savers_not_disabled");
        var movies = LibraryMovies(folder); var store = Store(); var data = store.ReadWorkbench(Scope(folder), language);
        var items = new List<CampaignItem>();
        foreach (var movie in movies)
        {
            if (request.IncludeOverviews) items.Add(PlanOverview(folder, movie, language, false, false, true, request.Language));
            if (!request.IncludeTitles) { if (request.IncludeGenres) items.Add(PlanGenre(folder, movie, language, store)); continue; }
            var row = WorkbenchItem(folder, movie, data, true); var item = LibraryItem(movie, language.Tag());
            if (!row.CanApply && row.Status != "applied") item = item with { State = "Skipped", ErrorCategory = row.Reason };
            else
            {
                var source = RequireSource(folder, movie, store); var selected = store.GetSelected(source.Id, language)!;
                if (!selected.Candidate.Approved) item = item with { State = "Skipped", ErrorCategory = "candidate_needs_review" };
                else item = item with { State = "Prepared", Source = source, CandidateId = selected.Candidate.Id, TranslationOrigin = "Reused",
                    Acceptance = "previously_accepted", Preview = new(Key(folder, movie.Id), language,
                        PreviewStatus.ReadyForReview, movie.Name, source.DisplayPrefix + selected.Candidate.Text,
                        source.Id, selected.Candidate.Id, selected.Candidate.Revision, selected.Revision) };
            }
            items.Add(item);
            if (request.IncludeGenres) items.Add(PlanGenre(folder, movie, language, store));
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        return LaunchLibrary(new(request.RequestId, Scope(folder), language.Tag(), "library_display",
            items.Any(x => x.State != "Skipped") ? "Running" : "Completed", now, now, items.ToArray(),
            RequestFingerprint: fingerprint, DisplayLanguage: language.Tag(), IncludeGenres: request.IncludeGenres, IncludeOverviews: request.IncludeOverviews, IncludeTitles: request.IncludeTitles), worker);
    });

    [NonAction]
    internal CampaignItem PrepareLibraryItem(CampaignRecord campaign, int ordinal)
    {
        lock (Gate)
        {
            var item = Campaigns().Read(campaign.Id).Items[ordinal]; var language = Languages.Parse(item.Language!);
            var store = Store(); var movie = Resolve(Guid.Parse(campaign.Scope.LibraryId), item.ItemId);
            ValidateCampaignSource(campaign, item, store, movie);
            if (item.Source is null || item.Spec is null || item.BaselineHash is null) throw new SourceChangedException();
            var job = ExistingPreparationJob(store, item.JobId);
            var ownCandidate = job is null ? null : store.GetTranslationItems(item.JobId).Single().CandidateId;
            if (store.GetReplacementBaseline(item.Source.Id, language, ownCandidate) != item.BaselineHash)
                throw new OperationValidationException("candidate_changed_preserved");
            if (store.CampaignTranslationReservations(campaign.Scope, language).TryGetValue(item.Source.Key.ItemId, out var reservations)
                && reservations.Any(x => x != item.JobId)) throw new OperationValidationException("earlier_translation_outcome_needs_review");
            if (job is null) store.CreateTranslationJob(item.JobId, campaign.Scope, language, [new(item.Source.Key, item.Spec)], campaign.Service!.Policy());
            else
            {
                var input = store.GetTranslationItems(item.JobId).Single();
                if (job.Scope != campaign.Scope || job.Language != language || input.Source != item.Source || input.Spec != item.Spec)
                    throw new IdempotencyConflictException();
            }
            return item;
        }
    }
    [NonAction]
    internal async Task FinishLibraryTitle(CampaignRecord campaign, int ordinal, bool retryFailed, CancellationToken token)
    {
        var journal = Campaigns(); var item = journal.Read(campaign.Id).Items[ordinal]; var store = Store();
        var language = Languages.Parse(item.Language!); var folder = Guid.Parse(campaign.Scope.LibraryId);
        // A frozen write intent can be resumed without accepting a different candidate.
        if (item.Preview is null)
        {
            lock (Gate)
            {
                ValidateCampaignSource(campaign, item, store, Resolve(folder, item.ItemId));
                var candidate = store.GetCandidate(item.CandidateId!);
                if (candidate.ProvenanceJson != JsonSerializer.Serialize(item.Spec)) throw new RevisionConflictException();
                item = journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Accepting", Acceptance = "automatic" }).Items[ordinal];
                var selected = store.AcceptReplacementCandidate(item.Source!.Key, language, item.Source.Id, candidate.Id,
                    item.BaselineHash!, item.BaselineSelectionRevision);
                var preview = new LanguagePreviewEntry(item.Source.Key, language, PreviewStatus.ReadyForReview,
                    item.Name, item.Source.DisplayPrefix + selected.Candidate.Text, item.Source.Id, selected.Candidate.Id,
                    selected.Candidate.Revision, selected.Revision);
                item = journal.ChangeItem(campaign.Id, ordinal, x => x with { Preview = preview }).Items[ordinal];
            }
        }
        if (campaign.DisplayLanguage != item.Language)
        { journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Saved", ErrorCategory = null }); return; }
        var result = await ApplySavedLanguageItem(campaign, ordinal, retryFailed, token);
        var success = result.State is OperationState.Applied or OperationState.ObservedExpected or OperationState.NoChange;
        journal.ChangeItem(campaign.Id, ordinal, x => x with { State = success ? "Applied" : "Failed",
            ErrorCategory = success ? null : result.ErrorCategory ?? result.State.ToString() });
    }
    [NonAction]
    internal async Task ApplyLibraryGenre(CampaignRecord campaign, int ordinal, bool retryFailed, CancellationToken token)
    {
        var journal = Campaigns(); var item = journal.Read(campaign.Id).Items[ordinal]; var store = Store();
        var preview = item.GenrePreview ?? throw new SourceChangedException();
        var folder = Guid.Parse(campaign.Scope.LibraryId);
        if (RequireGenreSource(folder, Resolve(folder, item.ItemId), store).Id != preview.SourceId) throw new SourceChangedException();
        if (ReadGenreBinding(preview.Key) is { } binding && !NfoBindingCurrent(binding, preview.Key))
            throw new OperationValidationException("genre_nfo_changed_retry");
        var service = new GenreOperationService(store, GenreTarget(folder, store));
        var existing = store.FindGenreOperation(item.OperationId);
        if (existing is not null && (existing.Key != preview.Key || existing.SourceId != preview.SourceId
            || existing.Language != preview.Language || existing.MappingId != preview.MappingId)) throw new IdempotencyConflictException();
        if (retryFailed && existing?.State == OperationState.Blocked
            && existing.ErrorCategory is "GenresLocked" or "UnsupportedSaverConfiguration" or "AdapterGuardBlocked")
        {
            item = journal.ChangeItem(campaign.Id, ordinal, x => x with { OperationId = Guid.NewGuid().ToString("N"),
                PreviousOperationIds = [.. x.PreviousOperationIds ?? [], x.OperationId] }).Items[ordinal];
            existing = null;
        }
        var result = existing is null ? await service.ApplyAsync(item.OperationId, campaign.Scope, preview, token)
            : await service.ResumeAsync(item.OperationId, campaign.Scope, token);
        var success = result.State is OperationState.Applied or OperationState.ObservedExpected or OperationState.NoChange;
        journal.ChangeItem(campaign.Id, ordinal, x => x with { State = success ? "Applied" : "Failed",
            ErrorCategory = success ? null : result.ErrorCategory ?? result.State.ToString() });
    }
}
