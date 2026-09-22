using System.Text.Json;
using Localizer.Core;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record DetectedChange(Guid ItemId, string Name, string[] Reasons);
public sealed record ChangeSummary(int Movies, int NewItems, int ChangedSources, int DisplayConflicts,
    int MissingChinese, int MissingEnglish, int NfoReady, int NfoChanged, int NfoBlocked, int UnknownTerms, int AffectedItems);
public sealed record ChangeSnapshot(string Version, DateTimeOffset CheckedUtc, ChangeSummary Summary,
    DetectedChange[] Items, string[] UnknownTerms);
public sealed record ChangeWatchRequest(bool Enabled, bool OnlyIfNew = false);

public sealed partial class AdminController
{
    // Observation only: no source import, candidate acceptance, model call or media write.
    [NonAction]
    internal ChangeSnapshot DetectChanges(Guid folder, CancellationToken token)
    {
        var movies = LibraryMovies(folder); var store = Store();
        var zh = store.ReadWorkbench(Scope(folder), TargetLanguage.SimplifiedChinese);
        var en = store.ReadWorkbench(Scope(folder), TargetLanguage.English);
        var savers = WorkbenchSaversDisabled(folder);
        var rows = new List<DetectedChange>(); var unknown = new HashSet<string>(StringComparer.Ordinal);
        var dictionary = new Dictionary<string, bool>(StringComparer.Ordinal);
        var fresh = 0; var changed = 0; var display = 0; var missingZh = 0; var missingEn = 0;
        var nfoReady = 0; var nfoChanged = 0; var nfoBlocked = 0;
        foreach (var movie in movies)
        {
            token.ThrowIfCancellationRequested();
            var reasons = new List<string>();
            try
            {
                var row = WorkbenchItem(folder, movie, zh, savers);
                var english = WorkbenchItem(folder, movie, en, savers);
                if (row.SourceId is null) { fresh++; reasons.Add("new"); }
                else if (!row.Confirmed) { changed++; reasons.Add("source_changed"); }
                else if (row.Reason == "display_name_changed" && english.Reason == "display_name_changed")
                { display++; reasons.Add("display_changed"); }
                if (row.SourceId is null || row.Confirmed && row.Candidate is null) { missingZh++; reasons.Add("missing_zh"); }
                if (english.SourceId is null || english.Confirmed && english.Candidate is null) { missingEn++; reasons.Add("missing_en"); }
                if (row.SourceId is null && !row.CanConfirm) reasons.Add("original_needs_review");
                var nfo = InspectNfoGenre(folder, movie, store);
                switch (nfo.State)
                {
                    case "Ready": nfoReady++; reasons.Add("nfo_ready"); break;
                    case "Changed": nfoChanged++; reasons.Add("nfo_changed"); break;
                    case "Blocked": nfoBlocked++; reasons.Add(nfo.Error ?? "genre_nfo_invalid"); break;
                }
                var source = store.GetCurrentGenreSource(Key(folder, movie.Id));
                var values = nfo.Read?.Values ?? source?.OriginalValues ?? movie.Genres;
                foreach (var term in values.Distinct(StringComparer.Ordinal))
                {
                    if (!dictionary.TryGetValue(term, out var missing))
                        dictionary[term] = missing = store.GetGenreDictionaryEntry(TargetLanguage.SimplifiedChinese, term).Unknown
                            || store.GetGenreDictionaryEntry(TargetLanguage.English, term).Unknown;
                    if (missing) { unknown.Add(term); if (!reasons.Contains("genre_dictionary_missing")) reasons.Add("genre_dictionary_missing"); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { reasons.Add("detection_item_unavailable"); }
            if (reasons.Count > 0) rows.Add(new(movie.Id, movie.Name, reasons.ToArray()));
        }
        var summary = new ChangeSummary(movies.Length, fresh, changed, display, missingZh, missingEn,
            nfoReady, nfoChanged, nfoBlocked, unknown.Count, rows.Count);
        var items = rows.OrderBy(x => x.ItemId).ToArray(); var terms = unknown.Order(StringComparer.Ordinal).ToArray();
        var version = Hash(JsonSerializer.Serialize(new { summary, items, terms }));
        return new(version, DateTimeOffset.UtcNow, summary, items, terms);
    }

    [HttpPost("libraries/{folder:guid}/changes/watch")]
    public ActionResult WatchChanges(Guid folder, [FromBody] ChangeWatchRequest request,
        [FromServices] ChangeDetectionWorker detection) => Guard(() =>
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        detection.Watch(folder, request.Enabled, request.OnlyIfNew);
        return new { detection.Read(folder).Enabled };
    });

    [HttpGet("libraries/{folder:guid}/changes")]
    public ActionResult LibraryChanges(Guid folder, [FromServices] ChangeDetectionWorker detection,
        [FromQuery] int start = 0, [FromQuery] int limit = 50) => Guard(() =>
    {
        if (start is < 0 or > 5000 || limit is < 1 or > 100) throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var state = detection.Read(folder);
        return new { state.Enabled, state.Scanning, state.Error, state.Snapshot?.Version, state.Snapshot?.CheckedUtc,
            state.Snapshot?.Summary, Start = start,
            NextStart = state.Snapshot is { } snapshot && start + limit < snapshot.Items.Length ? (int?)(start + limit) : null,
            UnknownTerms = state.Snapshot?.UnknownTerms.Take(50).ToArray() ?? [],
            Items = state.Snapshot?.Items.Skip(start).Take(limit).ToArray() ?? [] };
    });
}
