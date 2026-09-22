using System.Text.Json;

namespace Localizer.Core;

internal sealed record PreviewStoreSnapshot(IReadOnlyDictionary<MediaKey, SourceSnapshot> Sources,
    IReadOnlyDictionary<long, CandidateSelection> Selections);

public sealed partial class CandidateStore
{
    // LanguagePreview validates scope/keys and excludes locked/out-of-scope rows before this call.
    // Both reads share a snapshot, so a source-head or selection change cannot mix two DB versions.
    // JSON parameters avoid SQLite's variable-count limit without scanning unrelated library rows.
    internal PreviewStoreSnapshot ReadPreviewInputs(LibraryKey scope, TargetLanguage language,
        IReadOnlyList<ObservedMovie> movies)
    {
        var sources = new Dictionary<MediaKey, SourceSnapshot>();
        var selections = new Dictionary<long, CandidateSelection>();
        if (movies.Count == 0) return new(sources, selections);

        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        // Keep requested keys outermost: reordering these joins can rescan the JSON for every
        // library row instead of using one indexed lookup per requested key.
        using (var cmd = Command(db, tx, """
            SELECT s.* FROM json_each($items) requested
            CROSS JOIN source_heads h ON h.server_id=$server AND h.library_id=$library AND h.item_id=requested.value
            CROSS JOIN sources s ON s.id=h.source_id;
            """, ("$items", JsonSerializer.Serialize(movies.Select(x => x.Key.ItemId))),
            ("$server", scope.ServerId), ("$library", scope.LibraryId)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) { var source = ReadSource(reader); sources.Add(source.Key, source); }

        // A changed original/prefix must report SourceChanged without touching its candidate, just
        // as the single-item path did. Only current, matching source IDs enter the selection query.
        var matchingSources = movies.Where(movie => sources.TryGetValue(movie.Key, out var source)
                && source.OriginalTitle == movie.OriginalTitle && source.DisplayPrefix == movie.DisplayPrefix)
            .Select(movie => sources[movie.Key].Id).ToArray();
        if (matchingSources.Length != 0)
        {
            using var cmd = Command(db, tx, """
                SELECT c.*, selected.revision AS selection_revision FROM json_each($sources) requested
                CROSS JOIN selections selected ON selected.source_id=requested.value AND selected.language=$language
                CROSS JOIN candidates c ON c.id=selected.candidate_id;
                """, ("$sources", JsonSerializer.Serialize(matchingSources)), ("$language", language.Tag()));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var candidate = ReadCandidate(reader);
                selections.Add(candidate.SourceId, new(candidate, reader.GetInt64(reader.GetOrdinal("selection_revision"))));
            }
        }
        tx.Commit();
        return new(sources, selections);
    }
}
