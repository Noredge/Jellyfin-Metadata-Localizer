using Localizer.Jellyfin;
using Localizer.Core;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed partial class AdminController
{
    // Explicit refresh only: reads NFO sources but never confirms them or changes media.
    [HttpGet("libraries/{folder:guid}/coverage")]
    public ActionResult Coverage(Guid folder) => Guard(() =>
    {
        var movies = LibraryMovies(folder);
        var store = Store(); var overviews = Overviews();
        var languages = new[] { TargetLanguage.SimplifiedChinese, TargetLanguage.English };
        var snapshots = languages.ToDictionary(x => x, x => store.ReadWorkbench(Scope(folder), x));
        var titleSaved = new int[2]; var overviewSaved = new int[2];
        int titleSources = 0, overviewSources = 0, overviewEmpty = 0, overviewUnknown = 0;
        foreach (var movie in movies)
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            var key = Key(folder, movie.Id);
            var source = snapshots[languages[0]].Sources.GetValueOrDefault(key);
            var titleValid = false;
            try { titleValid = Confirmed(movie, source, ReadBinding(key)); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { }
            if (titleValid)
            {
                titleSources++;
                for (int i = 0; i < languages.Length; i++)
                    if (snapshots[languages[i]].Selections.GetValueOrDefault(source!.Id)?.Candidate.Approved == true) titleSaved[i]++;
            }
            var read = JellyfinNfoOverview.Read(movie);
            var state = overviews.Read(key);
            // Missing same-name NFO is unresolved until movie.nfo fallback is implemented.
            if (read.Error == "overview_empty") { overviewEmpty++; continue; }
            if (read.Error is not null || state.Source is null || state.Source.Hash != read.Hash || state.Source.NfoPath != read.Path)
            { overviewUnknown++; continue; }
            overviewSources++;
            for (int i = 0; i < languages.Length; i++) if (state.Selected(languages[i]) is not null) overviewSaved[i]++;
        }
        return new { CheckedUtc = DateTimeOffset.UtcNow, TotalMovies = movies.Length,
            Titles = new { Sources = titleSources, Unconfirmed = movies.Length - titleSources, Chinese = titleSaved[0], English = titleSaved[1] },
            Overviews = new { Sources = overviewSources, Empty = overviewEmpty, Unconfirmed = overviewUnknown, Chinese = overviewSaved[0], English = overviewSaved[1] } };
    });
}
