using Localizer.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record GenreIndexRepairRequest(bool ConfirmRepair);

public sealed partial class AdminController
{
    private string[] LibraryGenreNames(Guid folder) => LibraryMovies(folder).SelectMany(x => x.Genres)
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();

    [HttpGet("libraries/{folder:guid}/genres/index")]
    public ActionResult GenreIndex(Guid folder) => Guard(() =>
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var names = LibraryGenreNames(folder);
        return new { Total = names.Length, Missing = names.Count(x => library.GetItemById(library.GetGenreId(x))
            is not MediaBrowser.Controller.Entities.Genre { PresentationUniqueKey.Length: > 0 }) };
    });

    [HttpPost("libraries/{folder:guid}/genres/index/repair")]
    public Task<ActionResult> RepairGenreIndex(Guid folder, [FromBody] GenreIndexRepairRequest request) => GuardAsync(async () =>
    {
        if (!request.ConfirmRepair) throw new ArgumentException();
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        RequireIdle(Campaigns());
        // Repair only navigation entities used by this library's current movie genres.
        // No movie update, source import, translation, deletion or metadata refresh is involved.
        var created = await JellyfinGenreIndex.RepairAsync(library, () => LibraryGenreNames(folder), HttpContext.RequestAborted);
        return new { Created = created };
    });
}
