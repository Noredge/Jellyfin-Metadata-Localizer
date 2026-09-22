using Localizer.Core;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Localizer.Jellyfin;

public sealed class JellyfinOverviewTarget(ILibraryManager library, IApplicationHost host,
    Func<IReadOnlySet<Guid>> selectedLibraries, Func<MediaKey, string?> confirmedPath) : IOverviewTarget
{
    public ValueTask<OverviewTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var resolved = JellyfinMovieAccess.Resolve(library, host, selectedLibraries(), key, confirmedPath(key));
        if (resolved is null) return ValueTask.FromResult<OverviewTargetSnapshot?>(null);
        var (movie, saversDisabled) = resolved.Value;
        var source = JellyfinNfoOverview.Read(movie);
        return ValueTask.FromResult<OverviewTargetSnapshot?>(source.Hash is null ? null : new(movie.Overview ?? "", source.Hash,
            saversDisabled && !movie.IsLocked && !movie.LockedFields.Contains(MetadataField.Overview)));
    }
    public async ValueTask<TargetWriteResult> WriteAsync(OverviewWrite request, CancellationToken token)
    {
        await JellyfinMovieAccess.Writer.WaitAsync(token);
        try
        {
            var resolved = JellyfinMovieAccess.Resolve(library, host, selectedLibraries(), request.Key, confirmedPath(request.Key));
            if (resolved is null) return TargetWriteResult.Blocked;
            var (movie, saversDisabled) = resolved.Value;
            if (!saversDisabled || movie.IsLocked || movie.LockedFields.Contains(MetadataField.Overview)) return TargetWriteResult.Blocked;
            var source = JellyfinNfoOverview.Read(movie);
            if (source.Hash != request.SourceHash) return TargetWriteResult.Conflict;
            if ((movie.Overview ?? "") == request.Proposed) return TargetWriteResult.AlreadyMatches;
            if ((movie.Overview ?? "") != request.Expected) return TargetWriteResult.Conflict;
            token.ThrowIfCancellationRequested();
            movie.Overview = request.Proposed;
            await library.UpdateItemAsync(movie, movie.GetParent(), ItemUpdateType.MetadataEdit, token);
            return TargetWriteResult.Written;
        }
        finally { JellyfinMovieAccess.Writer.Release(); }
    }
}
