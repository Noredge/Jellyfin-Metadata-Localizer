using Localizer.Core;
using Jellyfin.Data.Enums;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Localizer.Jellyfin;

// Supplied by a source-confirmation workflow, never inferred from the translated Name.
// A moved/rebound file must be confirmed again. Prefix changes are visible to the core's source checks.
public sealed record ConfirmedTitleBinding(string MediaPath, string DisplayPrefix,
    string? ConfirmedOriginalTitle = null, string? ObservedOriginalTitle = null);

/// <summary>In-process adapter for Jellyfin 12.0. No HTTP DTO, SQL, model or media-file writes.</summary>
public sealed class JellyfinNameTarget(ILibraryManager library, IApplicationHost host,
    Func<IReadOnlySet<Guid>> selectedLibraries,
    Func<MediaKey, ConfirmedTitleBinding?> confirmedBinding) : INameTarget
{
    // Serializes this adapter's calls only. Jellyfin refreshes and other plugins do not share this gate.


    public ValueTask<NameTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Resolve(key)?.Snapshot);
    }

    public async ValueTask<TargetWriteResult> WriteAsync(NameWriteRequest request, CancellationToken cancellationToken)
    {
        await JellyfinMovieAccess.Writer.WaitAsync(cancellationToken);
        try
        {
            var resolved = Resolve(request.Key);
            if (resolved is null) return TargetWriteResult.Blocked;
            var (movie, snapshot) = resolved.Value;
            if (snapshot.Movie.NameLocked || !snapshot.MetadataSaversExplicitlyDisabled)
                return TargetWriteResult.Blocked;
            if (snapshot.Movie.OriginalTitle != request.ExpectedOriginalTitle
                || snapshot.Movie.DisplayPrefix != request.ExpectedDisplayPrefix)
                return TargetWriteResult.Conflict;
            if (movie.Name == request.ProposedName) return TargetWriteResult.AlreadyMatches;
            if (movie.Name != request.ExpectedName) return TargetWriteResult.Conflict;
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ProposedName);
            cancellationToken.ThrowIfCancellationRequested();
            // Any exception from this point is an uncertain outcome. Do not convert it to Blocked/Conflict,
            // or blindly roll the shared object back: UpdateItemAsync may already have persisted or emitted events.
            movie.Name = request.ProposedName;
            await library.UpdateItemAsync(movie, movie.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
            return TargetWriteResult.Written;
        }
        finally { JellyfinMovieAccess.Writer.Release(); }
    }

    private (Movie Movie, NameTargetSnapshot Snapshot)? Resolve(MediaKey key)
    {
        var binding = confirmedBinding(key);
        var resolved = JellyfinMovieAccess.Resolve(library, host, selectedLibraries(), key, binding?.MediaPath);
        if (binding is null || resolved is null) return null;
        var (movie, saversDisabled) = resolved.Value;
        // Manual source text is independent of Jellyfin's OriginalTitle. Require a paired observation
        // so an external OriginalTitle edit cannot silently inherit a previous confirmation.
        if (binding.ConfirmedOriginalTitle is not null && (binding.ObservedOriginalTitle is null
            || binding.ObservedOriginalTitle != (movie.OriginalTitle ?? string.Empty))) return null;
        return (movie, new(new(key, true, movie.IsLocked || movie.LockedFields.Contains(MetadataField.Name),
            binding.ConfirmedOriginalTitle ?? movie.OriginalTitle ?? string.Empty, binding.DisplayPrefix, movie.Name), saversDisabled));
    }
}
