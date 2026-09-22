using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;

namespace Localizer.Jellyfin;

public static class JellyfinGenreIndex
{
    // Use the host's identity/path rules. Never invent IDs or edit Jellyfin's database directly.
    public static int Ensure(ILibraryManager library, IEnumerable<string> values, CancellationToken token)
    {
        var created = 0;
        foreach (var name in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var id = library.GetGenreId(name);
            var existing = library.GetItemById(id);
            if (existing is not null && existing is not Genre) throw new IOException("Genre identity belongs to another item type.");
            if (existing is Genre { PresentationUniqueKey.Length: > 0 }) continue;
            var genre = library.GetGenre(name);
            // GetGenre persists a shell; genre lists are grouped by this key. A null key
            // collapses every new genre into one row. Use the host's key and SaveItems path
            // (CreateItems upserts) without invoking metadata savers or remote refreshes.
            genre.PresentationUniqueKey = genre.CreatePresentationUniqueKey();
            library.CreateItems([genre], null, token);
            if (library.GetItemById(id) is not Genre { PresentationUniqueKey.Length: > 0 })
                throw new IOException("Genre index entry was not persisted.");
            created++;
        }
        return created;
    }

    public static async Task<int> RepairAsync(ILibraryManager library, Func<IEnumerable<string>> currentValues, CancellationToken token)
    {
        await JellyfinMovieAccess.Writer.WaitAsync(token);
        try { return Ensure(library, currentValues(), token); }
        finally { JellyfinMovieAccess.Writer.Release(); }
    }
}
