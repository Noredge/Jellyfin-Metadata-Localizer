using System.Text.Json;
using Jellyfin.Data.Enums;
using Localizer.Core;
using Localizer.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record PersonNameRefreshRequest(Guid PreviewId, Guid RequestId, bool ConfirmApply);
public sealed record SavedPersonNameRefresh(Guid Id, LibraryKey Scope, string Language, DateTimeOffset CreatedUtc,
    PersonNameSnapshot Names, CampaignItem[] Items);

public sealed partial class AdminController
{
    private string PersonRefreshPath(Guid id) => Path.Combine(Root, "person-name-previews", id.ToString("N") + ".json");

    [HttpPost("libraries/{folder:guid}/people/preview")]
    public ActionResult PreviewPersonNames(Guid folder, [FromBody] NamePreviewRequest request) => CampaignGuard(() =>
    {
        var language = Languages.Parse(request.Language); var people = DiscoverPersonNames(folder);
        if (!WorkbenchSaversDisabled(folder)) throw new OperationValidationException("metadata_savers_not_disabled");
        var raw = library.GetItemList(new InternalItemsQuery { ParentId = folder, Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie], Limit = WorkbenchMaximum + 1 });
        if (raw.Count > WorkbenchMaximum) throw new OperationValidationException("library_limit_exceeded");
        var store = Store(); var templates = store.ReadNameTemplates(Scope(folder), language);
        var id = Guid.NewGuid(); var items = new List<CampaignItem>(); var skipped = new Dictionary<string, int>();
        void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
        foreach (var movie in raw.OfType<Movie>().Where(x => Belongs(x, folder)))
        {
            try
            {
                var key = Key(folder, movie.Id); var source = RequireSource(folder, movie, store);
                var selected = store.GetSelected(source.Id, language);
                if (selected is null || !selected.Candidate.Approved) { Skip("missing_or_unaccepted"); continue; }
                if (selected.Candidate.IsHumanEdited) { Skip("human_edit_preserved"); continue; }
                if (movie.IsLocked || movie.LockedFields.Contains(MediaBrowser.Model.Entities.MetadataField.Name)
                    || movie.Name != source.DisplayPrefix + selected.Candidate.Text) { Skip("display_changed_or_locked_preserved"); continue; }
                var refreshed = PersonNameRendering.Refresh(source, selected.Candidate, people,
                    templates.GetValueOrDefault(selected.Candidate.Id), id);
                if (refreshed.Text == selected.Candidate.Text) { Skip("already_consistent"); continue; }
                items.Add(new(movie.Id, movie.Name, Observation(movie, source), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                    Source: source, Spec: refreshed.Spec, Acceptance: "person_name_update",
                    NameRefresh: new(selected.Candidate.Id, selected.Candidate.Revision, selected.Revision, refreshed.Text)));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException)
            { Skip("source_or_name_template_unavailable"); }
        }
        var saved = new SavedPersonNameRefresh(id, Scope(folder), language.Tag(), DateTimeOffset.UtcNow, people, items.ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(PersonRefreshPath(id))!);
        using (var stream = new FileStream(PersonRefreshPath(id), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, saved); stream.Flush(true); }
        return new { PreviewId = id, Language = language.Tag(), NameRevision = people.Revision, Total = items.Count, Skipped = skipped,
            Changes = items.Select(x => new { x.ItemId, Before = x.Name, After = x.Source!.DisplayPrefix + x.NameRefresh!.Text }).ToArray() };
    });

    [HttpPost("libraries/{folder:guid}/people/apply")]
    public ActionResult ApplyPersonNames(Guid folder, [FromBody] PersonNameRefreshRequest request, [FromServices] CampaignWorker campaigns) => CampaignGuard(() =>
    {
        Nonempty(request.PreviewId); Nonempty(request.RequestId);
        if (!request.ConfirmApply || !FolderExists(folder)) throw new ArgumentException();
        var path = PersonRefreshPath(request.PreviewId);
        if (!System.IO.File.Exists(path)) throw new KeyNotFoundException();
        var preview = JsonSerializer.Deserialize<SavedPersonNameRefresh>(System.IO.File.ReadAllText(path)) ?? throw new ArgumentException();
        if (preview.Id != request.PreviewId || preview.Scope != Scope(folder)) throw new ArgumentException();
        var journal = Campaigns(); var previous = journal.List().SingleOrDefault(x => x.Id == request.RequestId);
        if (previous is not null)
        {
            if (previous.Mode != "refresh_names" || previous.Scope != preview.Scope || previous.Language != preview.Language
                || previous.Items.Length != preview.Items.Length
                || previous.Items.Where((x, n) => x.ItemId != preview.Items[n].ItemId || x.NameRefresh != preview.Items[n].NameRefresh || x.Spec != preview.Items[n].Spec).Any())
                throw new IdempotencyConflictException();
            return CampaignView(previous, campaigns, true);
        }
        if (preview.CreatedUtc < DateTimeOffset.UtcNow.AddHours(-24) || CurrentPersonNames().Revision != preview.Names.Revision) throw new RevisionConflictException();
        if (!WorkbenchSaversDisabled(folder)) throw new OperationValidationException("metadata_savers_not_disabled");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var campaign = journal.Create(new(request.RequestId, preview.Scope, preview.Language, "refresh_names",
            preview.Items.Length == 0 ? "Completed" : "Running", now, now, preview.Items, Names: preview.Names));
        if (preview.Items.Length > 0)
        {
            try { campaigns.Run(campaign.Id, new()); }
            catch (OperationBusyException) { journal.Change(campaign.Id, x => x with { State = "Paused", ErrorCategory = "WorkerBusy" }); throw; }
        }
        return CampaignView(journal.Read(campaign.Id), campaigns, true);
    });

    [NonAction]
    internal void PrepareNameRefreshItem(CampaignRecord campaign, int ordinal)
    {
        lock (Gate)
        {
            var journal = Campaigns(); var item = journal.Read(campaign.Id).Items[ordinal];
            if (item.CandidateId is not null) return;
            if (item.Source is null || item.Spec is null || item.NameRefresh is null) throw new SourceChangedException();
            var folder = Guid.Parse(campaign.Scope.LibraryId); var store = Store();
            ValidateCampaignSource(campaign, item, store, Resolve(folder, item.ItemId));
            if (!WorkbenchSaversDisabled(folder)) throw new OperationValidationException("metadata_savers_not_disabled");
            var language = Languages.Parse(campaign.Language);
            var selected = store.DeriveNamedCandidate(item.Source.Key, item.Source.Id, language, item.NameRefresh, item.Spec);
            var preview = new LanguagePreviewEntry(item.Source.Key, language, PreviewStatus.ReadyForReview,
                item.Name, item.Source.DisplayPrefix + selected.Candidate.Text, item.Source.Id, selected.Candidate.Id,
                selected.Candidate.Revision, selected.Revision);
            journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Prepared", CandidateId = selected.Candidate.Id, TranslationOrigin = "Reused", Preview = preview });
        }
    }
}
