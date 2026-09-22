using Jellyfin.Data.Enums;
using Localizer.Core;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Localizer.Jellyfin;

internal static class JellyfinMovieAccess
{
    // Shared by both fields. External refreshes/plugins do not participate in this gate.
    internal static readonly SemaphoreSlim Writer = new(1, 1);
    internal static (Movie Movie, bool SaversDisabled)? Resolve(ILibraryManager library, IApplicationHost host,
        IReadOnlySet<Guid> selectedLibraries, MediaKey key, string? confirmedPath)
    {
        if (key.ServerId != host.SystemId || !Guid.TryParse(key.LibraryId, out var libraryId)
            || !Guid.TryParse(key.ItemId, out var itemId) || !selectedLibraries.Contains(libraryId)) return null;
        // Use a query with an empty-result path when the requested movie no longer exists.
        if (library.GetItemList(new InternalItemsQuery { ItemIds = [itemId], IncludeItemTypes = [BaseItemKind.Movie] })
            .SingleOrDefault() is not Movie movie) return null;
        if (confirmedPath is null || !Path.IsPathFullyQualified(movie.Path) || !Path.IsPathFullyQualified(confirmedPath)
            || !string.Equals(Path.GetFullPath(movie.Path), Path.GetFullPath(confirmedPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        var collections = library.GetCollectionFolders(movie).Select(x => x.Id).Distinct().ToArray();
        if (collections.Length != 1 || collections[0] != libraryId) return null;
        var folder = library.GetVirtualFolders().SingleOrDefault(x => Guid.TryParse(x.ItemId, out var id) && id == libraryId);
        return folder is null ? null : (movie, folder.LibraryOptions?.MetadataSavers is { Length: 0 });
    }
}
