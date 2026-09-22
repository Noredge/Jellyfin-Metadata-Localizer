using Localizer.Core;
using MediaBrowser.Controller.Entities.Movies;

namespace Localizer.Jellyfin;

public static class JellyfinNfoOverview
{
    public static NfoOverviewRead Read(Movie movie) =>
        NfoOverview.Read(movie.Path, movie.GetAdditionalParts().Select(x => x.Path));
}
