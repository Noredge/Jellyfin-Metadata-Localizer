using Jellyfin.Data.Enums;
using Localizer.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed partial class AdminController
{
    [HttpGet("libraries/{folder:guid}/genres/discovery")]
    public ActionResult DiscoverGenres(Guid folder, [FromQuery] int start = 0, [FromQuery] int limit = 50) => Guard(() =>
    {
        if (start is < 0 or > 1000000 || limit is < 1 or > 50) throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var raw = library.GetItemList(new InternalItemsQuery { ParentId = folder, Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie], Limit = WorkbenchMaximum + 1 });
        if (raw.Count > WorkbenchMaximum) throw new OperationValidationException("library_limit_exceeded");
        var store = Store();
        var entries = new Dictionary<(string, TargetLanguage), GenreDictionaryEntry>();
        GenreDictionaryEntry Entry(string term, TargetLanguage language)
        {
            if (!entries.TryGetValue((term, language), out var entry))
                entries[(term, language)] = entry = store.GetGenreDictionaryEntry(language, term);
            return entry;
        }
        var movies = raw.OfType<Movie>().Where(x => Belongs(x, folder)).ToArray();
        var unconfirmed = 0; var observed = new List<(string Term, Guid Item)>();
        foreach (var movie in movies)
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            var key = Key(folder, movie.Id); var source = store.GetCurrentGenreSource(key);
            var confirmed = GenreConfirmed(movie, source, ReadGenreBinding(key));
            if (!confirmed) unconfirmed++;
            var terms = GenreDiscovery.ObservedTerms(movie.Genres, confirmed ? source!.OriginalValues : null,
                (term, language) => Entry(term, language).Effective);
            observed.AddRange(terms.Select(term => (term, movie.Id)));
        }
        var groups = observed.GroupBy(x => x.Term, StringComparer.Ordinal).ToArray();
        var missing = groups.Select(g => new { Original = g.Key, ItemCount = g.Count(),
            MissingLanguages = new[] { TargetLanguage.SimplifiedChinese, TargetLanguage.English }
                .Where(language => Entry(g.Key, language).Unknown).Select(language => language.Tag()).ToArray(),
            ExampleItemId = g.First().Item.ToString("N") }).Where(x => x.MissingLanguages.Length > 0)
            .OrderByDescending(x => x.ItemCount).ThenBy(x => x.Original, StringComparer.Ordinal).ToArray();
        var missingTerms = missing.Select(x => x.Original).ToHashSet(StringComparer.Ordinal);
        return new { MovieCount = movies.Length, UnconfirmedItemCount = unconfirmed, UniqueTermCount = groups.Length,
            UnknownTermCount = missing.Length, AffectedItemCount = observed.Where(x => missingTerms.Contains(x.Term)).Select(x => x.Item).Distinct().Count(),
            MissingChineseCount = missing.Count(x => x.MissingLanguages.Contains("zh-Hans")),
            MissingEnglishCount = missing.Count(x => x.MissingLanguages.Contains("en")),
            Start = start, NextStart = start + limit < missing.Length ? (int?)(start + limit) : null,
            Items = missing.Skip(start).Take(limit).ToArray() };
    });
}
