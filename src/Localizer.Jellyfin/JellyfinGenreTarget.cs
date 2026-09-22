using Localizer.Core;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Localizer.Jellyfin;

public sealed class JellyfinGenreTarget(ILibraryManager library, IApplicationHost host,
    Func<IReadOnlySet<Guid>> selectedLibraries, Func<MediaKey, string?> confirmedPath) : IGenreTarget
{
    public ValueTask<GenreTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var resolved = JellyfinMovieAccess.Resolve(library, host, selectedLibraries(), key, confirmedPath(key));
        return ValueTask.FromResult(resolved is null ? null : new GenreTargetSnapshot(key, true,
            resolved.Value.Movie.IsLocked || resolved.Value.Movie.LockedFields.Contains(MetadataField.Genres),
            resolved.Value.SaversDisabled, GenreValues.Copy(resolved.Value.Movie.Genres)));
    }
    public async ValueTask<TargetWriteResult> WriteAsync(GenreWriteRequest request, CancellationToken token)
    {
        var proposed = GenreValues.Copy(request.ProposedValues);
        var expected = GenreValues.Copy(request.ExpectedValues);
        await JellyfinMovieAccess.Writer.WaitAsync(token);
        try
        {
            var resolved = JellyfinMovieAccess.Resolve(library, host, selectedLibraries(), request.Key, confirmedPath(request.Key));
            if (resolved is null) return TargetWriteResult.Blocked;
            var (movie, saversDisabled) = resolved.Value;
            if (!saversDisabled || movie.IsLocked || movie.LockedFields.Contains(MetadataField.Genres)) return TargetWriteResult.Blocked;
            if (GenreValues.Equal(movie.Genres, proposed))
            {
                JellyfinGenreIndex.Ensure(library, proposed, token);
                return TargetWriteResult.AlreadyMatches;
            }
            if (!GenreValues.Equal(movie.Genres, expected)) return TargetWriteResult.Conflict;
            token.ThrowIfCancellationRequested();
            // Updating movie strings alone does not create Jellyfin's addressable Genre items.
            // Materialize them first so the web client's genreId links resolve immediately.
            JellyfinGenreIndex.Ensure(library, proposed, token);
            // Preserve the request's raw representation for restoration. Do not touch Name or other fields.
            movie.Genres = proposed;
            await library.UpdateItemAsync(movie, movie.GetParent(), ItemUpdateType.MetadataEdit, token);
            return TargetWriteResult.Written;
        }
        finally { JellyfinMovieAccess.Writer.Release(); }
    }
}
