using Localizer.Jellyfin;
using System.Text.Json;
using Localizer.Core;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

// Null means leave this field unchanged. Each selected field has one frozen target.
public sealed record FieldDisplayRequest(Guid RequestId, bool ConfirmApply,
    string? TitleLanguage = null, string? OverviewLanguage = null, string? GenreLanguage = null);

public sealed partial class AdminController
{
    [HttpPost("libraries/{folder:guid}/campaigns/field-display")]
    public ActionResult StartFieldDisplay(Guid folder, [FromBody] FieldDisplayRequest request,
        [FromServices] CampaignWorker worker) => CampaignGuard(() =>
    {
        Nonempty(request.RequestId);
        var targets = new[] { ("Name", request.TitleLanguage), ("Overview", request.OverviewLanguage), ("Genres", request.GenreLanguage) }
            .Where(x => x.Item2 is not null).ToArray();
        if (!request.ConfirmApply || targets.Length == 0 || targets.Any(x => x.Item2 is not ("zh-Hans" or "en" or "original"))) throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var journal = Campaigns(); var fingerprint = Hash(JsonSerializer.Serialize(new { Scope = Scope(folder), Request = request }));
        if (journal.List().SingleOrDefault(x => x.Id == request.RequestId) is { } previous)
        {
            if (previous.RequestFingerprint != fingerprint) throw new IdempotencyConflictException();
            return CampaignView(previous, worker, true);
        }
        RequireIdle(journal);
        if (!WorkbenchSaversDisabled(folder)) throw new OperationValidationException("metadata_savers_not_disabled");
        var store = Store(); var movies = LibraryMovies(folder); var items = new List<CampaignItem>();
        foreach (var (field, target) in targets)
        {
            var language = target == "original" ? TargetLanguage.SimplifiedChinese : Languages.Parse(target!);
            var data = store.ReadWorkbench(Scope(folder), language);
            foreach (var movie in movies)
            {
                var key = Key(folder, movie.Id); var item = LibraryItem(movie, target!, field);
                try
                {
                    if (field == "Genres")
                    {
                        if (target != "original") item = PlanGenre(folder, movie, language, store);
                        else
                        {
                            var imported = ImportNfoGenre(folder, movie, store);
                            if (imported.State == "Blocked") throw new OperationValidationException(imported.Error ?? "genre_source_unconfirmed");
                            var source = RequireGenreSource(folder, movie, store);
                            item = item with { State = "Prepared", GenrePreview = new(key, language, PreviewStatus.ReadyForReview,
                                source.Id, Hash(JsonSerializer.Serialize(source)).ToLowerInvariant(), movie.Genres.ToArray(), source.OriginalValues, [], true) };
                        }
                    }
                    else if (field == "Overview")
                    {
                        if (target != "original") item = PlanOverview(folder, movie, language, false, false, true, target);
                        else
                        {
                            var read = JellyfinNfoOverview.Read(movie);
                            if (read.Error is not null) throw new OperationValidationException(read.Error);
                            var saved = Overviews().Read(key); saved = Overviews().Observe(key, saved.Revision, read);
                            item = item with { State = "Prepared", Observation = movie.Overview ?? "", OriginalRevision = saved.Revision };
                        }
                    }
                    else
                    {
                        var source = RequireSource(folder, movie, store);
                        if (target == "original") item = item with { State = "Prepared", Source = source };
                        else
                        {
                            var row = WorkbenchItem(folder, movie, data, true);
                            if (!row.CanApply && row.Status != "applied") throw new OperationValidationException(row.Reason);
                            var selected = store.GetSelected(source.Id, language);
                            if (selected?.Candidate.Approved != true) throw new OperationValidationException("candidate_needs_review");
                            item = item with { State = "Prepared", Source = source, CandidateId = selected.Candidate.Id, TranslationOrigin = "Reused",
                                Preview = new(key, language, PreviewStatus.ReadyForReview, movie.Name, source.DisplayPrefix + selected.Candidate.Text,
                                    source.Id, selected.Candidate.Id, selected.Candidate.Revision, selected.Revision) };
                        }
                    }
                }
                catch (OperationValidationException error) { item = item with { State = "Skipped", ErrorCategory = error.Category }; }
                catch (SourceChangedException) { item = item with { State = "Skipped", ErrorCategory = "source_binding_changed" }; }
                items.Add(item);
            }
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        return LaunchLibrary(new(request.RequestId, Scope(folder), "multi", "field_display", "Running", now, now, items.ToArray(),
            RequestFingerprint: fingerprint, IncludeTitles: request.TitleLanguage is not null,
            IncludeOverviews: request.OverviewLanguage is not null, IncludeGenres: request.GenreLanguage is not null), worker);
    });

    [NonAction]
    internal async Task ApplyOriginalField(CampaignRecord campaign, int ordinal, CancellationToken token)
    {
        var item = Campaigns().Read(campaign.Id).Items[ordinal]; var folder = Guid.Parse(campaign.Scope.LibraryId);
        var key = Key(folder, item.ItemId); var movie = Resolve(folder, item.ItemId);
        if (item.Field == "Genres") { await ApplyLibraryGenre(campaign, ordinal, false, token); return; }
        string state; string? error;
        if (item.Field == "Name")
        {
            var store = Store(); var source = RequireSource(folder, movie, store);
            if (source.Id != item.Source?.Id) throw new SourceChangedException();
            var operations = new NameOperationService(store, NameTarget(folder, store));
            var existing = store.FindOperation(item.OperationId);
            var result = existing is null
                ? await operations.ApplyOriginalAsync(item.OperationId, campaign.Scope, key, source.Id, item.Name, token)
                : await operations.ResumeAsync(item.OperationId, campaign.Scope, token);
            state = result.State.ToString(); error = result.ErrorCategory;
        }
        else
        {
            RequireOverviewSource(folder, item.ItemId, Overviews().Read(key));
            var writes = OverviewWrites(folder); var id = Guid.Parse(item.OperationId);
            var result = writes.List(key).Any(x => x.Id == item.OperationId)
                ? await writes.RecoverAsync(id, key, token)
                : await writes.ApplyOriginalAsync(id, key, item.OriginalRevision, item.Observation, token);
            state = result.State; error = state;
        }
        var success = state is "Applied" or "ObservedExpected" or "ObservedApplied" or "NoChange";
        Campaigns().ChangeItem(campaign.Id, ordinal, x => x with { State = success ? "Applied" : "Failed", ErrorCategory = success ? null : error ?? state });
    }

    [HttpGet("libraries/{folder:guid}/display-state")]
    public ActionResult FieldDisplayState(Guid folder) => CampaignGuard(() =>
    {
        var movies = LibraryMovies(folder); var store = Store(); var overviews = Overviews();
        var counts = new Dictionary<string, Dictionary<string, int>>();
        foreach (var field in new[] { "Name", "Overview", "Genres" }) counts[field] = new() { ["zh-Hans"] = 0, ["en"] = 0, ["original"] = 0, ["total"] = movies.Length };
        foreach (var movie in movies)
        {
            var key = Key(folder, movie.Id); var source = store.GetCurrentSource(key);
            if (source is not null && Confirmed(movie, source, ReadBinding(key)))
            {
                if (movie.Name == source.DisplayPrefix + source.OriginalTitle) counts["Name"]["original"]++;
                foreach (var language in new[] { TargetLanguage.SimplifiedChinese, TargetLanguage.English })
                    if (store.GetSelected(source.Id, language) is { Candidate.Approved: true } selected && movie.Name == source.DisplayPrefix + selected.Candidate.Text) counts["Name"][language.Tag()]++;
            }
            var overview = overviews.Read(key);
            var overviewRead = JellyfinNfoOverview.Read(movie);
            if (overview.Source is not null && overviewRead.Error is null && overviewRead.Hash == overview.Source.Hash && overviewRead.Path == overview.Source.NfoPath)
            {
                if (movie.Overview == overview.Source.Text) counts["Overview"]["original"]++;
                foreach (var language in new[] { TargetLanguage.SimplifiedChinese, TargetLanguage.English })
                    if (overview.Selected(language) is { } candidate && movie.Overview == candidate.Text) counts["Overview"][language.Tag()]++;
            }
            else if (string.IsNullOrEmpty(movie.Overview) && overviewRead.Error == "overview_empty") counts["Overview"]["total"]--;
            var genres = store.GetCurrentGenreSource(key);
            if (GenreConfirmed(movie, genres, ReadGenreBinding(key)))
            {
                if (GenreValues.Equal(movie.Genres, genres!.OriginalValues)) counts["Genres"]["original"]++;
                foreach (var language in new[] { TargetLanguage.SimplifiedChinese, TargetLanguage.English })
                {
                    var preview = store.BuildGenrePreview(Scope(folder), language, new(key, true, false, true, movie.Genres));
                    if (preview.UnknownValues.Length == 0 && GenreValues.Equal(movie.Genres, preview.ProposedValues)) counts["Genres"][language.Tag()]++;
                }
            }
        }
        return new { Fields = counts };
    });
}
