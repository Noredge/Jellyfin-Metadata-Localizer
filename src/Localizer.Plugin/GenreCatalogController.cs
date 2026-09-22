using System.Text.Json;
using Localizer.Core;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed partial class AdminController
{
    [HttpGet("libraries/{folder:guid}/genres/catalog")]
    public ActionResult GenreCatalog(Guid folder, [FromQuery] string language = "zh-Hans", [FromQuery] string? query = null,
        [FromQuery] bool unknownOnly = false, [FromQuery] int start = 0, [FromQuery] int limit = 20,
        [FromQuery] string? snapshot = null, [FromQuery] bool bilingual = false) => Guard(() =>
    {
        var targetLanguage = Languages.Parse(language);
        if (start is < 0 or > 1000000 || limit is < 1 or > 50 || query?.Length > 500 || snapshot?.Length > 64) throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var store = Store(); var selected = folder.ToString("N");
        var libraryNames = library.GetVirtualFolders().Where(x => Guid.TryParse(x.ItemId, out _))
            .ToDictionary(x => Guid.Parse(x.ItemId).ToString("N"), x => x.Name);
        var valid = new List<GenreSource>(); var excludedSelected = 0;
        foreach (var source in store.ListCurrentGenreSources(host.SystemId))
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            var accepted = false;
            if (Guid.TryParse(source.Key.LibraryId, out var sourceFolder) && Guid.TryParse(source.Key.ItemId, out var sourceItem))
            {
                try { accepted = GenreConfirmed(Resolve(sourceFolder, sourceItem), source, ReadGenreBinding(source.Key)); }
                catch (KeyNotFoundException) { /* Historical identities are not live impact counts. */ }
            }
            if (accepted) valid.Add(source); else if (source.Key.LibraryId == selected) excludedSelected++;
        }
        // Original words remain exact ordinal keys; repeated members count only once per item.
        var terms = valid.SelectMany(s => s.OriginalValues.Distinct(StringComparer.Ordinal).Select(word => (Word: word, Source: s)))
            .GroupBy(x => x.Word, StringComparer.Ordinal).Where(g => g.Any(x => x.Source.Key.LibraryId == selected))
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var local = g.Where(x => x.Source.Key.LibraryId == selected).ToArray();
                return new { Entry = store.GetGenreDictionaryEntry(targetLanguage, g.Key),
                    Alternate = bilingual ? store.GetGenreDictionaryEntry(targetLanguage == TargetLanguage.English ? TargetLanguage.SimplifiedChinese : TargetLanguage.English, g.Key) : null, LocalItemCount = local.Length,
                    ServerItemCount = g.Count(), Libraries = g.GroupBy(x => x.Source.Key.LibraryId).OrderBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => new { Id = x.Key, Name = libraryNames[x.Key], ItemCount = x.Count() }).ToArray(),
                    ExampleItemId = local[0].Source.Key.ItemId, SourceId = local[0].Source.Id };
            }).ToArray();
        // Keep pagination stable across relevant source/dictionary changes; refresh explicitly after a conflict.
        var version = Hash(JsonSerializer.Serialize(new { Scope = Scope(folder), Language = language,
            Sources = valid.Select(x => new { x.Id, x.Key, x.Version }), Terms = terms.Select(x => new { x.Entry, x.Alternate }) }));
        if (snapshot is not null && snapshot != version) throw new RevisionConflictException();
        var matches = terms.Where(x => (!unknownOnly || x.Entry.Unknown || x.Alternate?.Unknown == true) && (string.IsNullOrEmpty(query)
            || x.Entry.Original.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (x.Entry.Effective?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (x.Alternate?.Effective?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))).ToArray();
        return new { Language = language, Snapshot = version, ConfirmedItemCount = valid.Count(x => x.Key.LibraryId == selected),
            ExcludedSourceCount = excludedSelected, UniqueTermCount = terms.Length, UnknownTermCount = terms.Count(x => x.Entry.Unknown || x.Alternate?.Unknown == true),
            Total = matches.Length, Start = start, Limit = limit, NextStart = start + limit < matches.Length ? (int?)(start + limit) : null,
            Items = matches.Skip(start).Take(limit).ToArray() };
    });
}
