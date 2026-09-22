namespace Localizer.Core;

public enum PreviewStatus { ReadyForReview, AlreadyMatches, MissingSource, SourceChanged, MissingCandidate, NeedsApproval, Locked, OutOfScope }
public sealed record ObservedMovie(MediaKey Key, bool IsMovie, bool NameLocked,
    string OriginalTitle, string DisplayPrefix, string CurrentName);
public sealed record LanguagePreviewEntry(MediaKey Key, TargetLanguage Language, PreviewStatus Status,
    string ExpectedCurrentName, string? ProposedName, long? SourceId, string? CandidateId,
    long? CandidateRevision, long? SelectionRevision);

/// <summary>Read-only previews. An application journal and fresh checks are required before any writes.</summary>
public sealed class LanguagePreview(CandidateStore store)
{
    public IReadOnlyList<LanguagePreviewEntry> Build(LibraryKey scope, TargetLanguage language, IEnumerable<ObservedMovie> movies)
    {
        _ = language.Tag();
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.ServerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.LibraryId);
        var observed = new List<ObservedMovie>();
        var readable = new List<ObservedMovie>();
        var seen = new HashSet<MediaKey>();
        foreach (var movie in movies)
        {
            if (!seen.Add(movie.Key)) throw new ArgumentException("Duplicate movie in preview scope.");
            observed.Add(movie);
            if (movie.Key.ServerId != scope.ServerId || movie.Key.LibraryId != scope.LibraryId || !movie.IsMovie || movie.NameLocked)
                continue;
            movie.Key.Validate();
            readable.Add(movie);
        }
        var snapshot = store.ReadPreviewInputs(scope, language, readable);
        var result = new List<LanguagePreviewEntry>(observed.Count);
        foreach (var movie in observed)
        {
            LanguagePreviewEntry Entry(PreviewStatus status, SourceSnapshot? source=null, CandidateSelection? selection=null) =>
                new(movie.Key,language,status,movie.CurrentName,
                    selection is null || source is null ? null : source.DisplayPrefix+selection.Candidate.Text,
                    source?.Id,selection?.Candidate.Id,selection?.Candidate.Revision,selection?.Revision);
            if (movie.Key.ServerId != scope.ServerId || movie.Key.LibraryId != scope.LibraryId || !movie.IsMovie)
            {
                result.Add(Entry(PreviewStatus.OutOfScope));
                continue;
            }
            if (movie.NameLocked)
            {
                result.Add(Entry(PreviewStatus.Locked));
                continue;
            }
            var source = snapshot.Sources.GetValueOrDefault(movie.Key);
            if (source is null)
            {
                result.Add(Entry(PreviewStatus.MissingSource));
                continue;
            }
            if (source.OriginalTitle != movie.OriginalTitle || source.DisplayPrefix != movie.DisplayPrefix)
            {
                result.Add(Entry(PreviewStatus.SourceChanged,source));
                continue;
            }
            var selection = snapshot.Selections.GetValueOrDefault(source.Id);
            if (selection is null)
            {
                result.Add(Entry(PreviewStatus.MissingCandidate,source));
                continue;
            }
            result.Add(Entry(!selection.Candidate.Approved ? PreviewStatus.NeedsApproval :
                movie.CurrentName == source.DisplayPrefix+selection.Candidate.Text ? PreviewStatus.AlreadyMatches :
                PreviewStatus.ReadyForReview,source,selection));
        }
        return result;
    }
}
