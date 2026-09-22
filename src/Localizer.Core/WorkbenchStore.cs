using System.Text.Json;

namespace Localizer.Core;

public sealed record WorkbenchStoreSnapshot(IReadOnlyDictionary<MediaKey, SourceSnapshot> Sources,
    IReadOnlyDictionary<long, CandidateSelection> Selections, IReadOnlyDictionary<MediaKey, RestorePoint> RestorePoints,
    IReadOnlySet<MediaKey> Pending);

public sealed partial class CandidateStore
{
    // One scoped read transaction, rather than opening a connection for each movie and each column.
    public WorkbenchStoreSnapshot ReadWorkbench(LibraryKey scope, TargetLanguage language, int maximum = 100000)
    {
        if (maximum is < 1 or > 100000) throw new ArgumentException();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        var sources = new Dictionary<MediaKey, SourceSnapshot>();
        using (var cmd = Command(db, tx, """
            SELECT s.* FROM sources s JOIN source_heads h ON h.source_id=s.id
            WHERE h.server_id=$s AND h.library_id=$l LIMIT $n;
            """, ("$s", scope.ServerId), ("$l", scope.LibraryId), ("$n", maximum + 1)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) { var source = ReadSource(reader); sources.Add(source.Key, source); }
        if (sources.Count > maximum) throw new InvalidOperationException("Workbench source limit exceeded.");
        var selections = new Dictionary<long, CandidateSelection>();
        using (var cmd = Command(db, tx, """
            SELECT c.*, s.revision AS selection_revision FROM candidates c
            JOIN selections s ON s.candidate_id=c.id JOIN source_heads h ON h.source_id=s.source_id
            WHERE h.server_id=$s AND h.library_id=$l AND s.language=$lang;
            """, ("$s", scope.ServerId), ("$l", scope.LibraryId), ("$lang", language.Tag())))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var candidate = ReadCandidate(reader);
                selections.Add(candidate.SourceId, new(candidate, reader.GetInt64(reader.GetOrdinal("selection_revision"))));
            }
        var points = new Dictionary<MediaKey, RestorePoint>();
        using (var cmd = Command(db, tx, """
            SELECT o.payload_json,h.consumed FROM name_restore_heads h
            JOIN name_operations o ON o.id=h.apply_id
            WHERE h.server_id=$s AND h.library_id=$l LIMIT $n;
            """, ("$s", scope.ServerId), ("$l", scope.LibraryId), ("$n", maximum + 1)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var operation = JsonSerializer.Deserialize<NameOperation>(reader.GetString(0))!;
                points.Add(operation.Key, new(operation, reader.GetBoolean(1)));
            }
        if (points.Count > maximum) throw new InvalidOperationException("Workbench history limit exceeded.");
        var pending = new HashSet<MediaKey>();
        using (var cmd = Command(db, tx, """
            SELECT item_id FROM name_operations WHERE server_id=$s AND library_id=$l
                AND state IN ('Prepared','Writing','Retryable')
            UNION SELECT item_id FROM genre_operations WHERE server_id=$s AND library_id=$l
                AND state IN ('Prepared','Writing','Retryable') LIMIT $n;
            """, ("$s", scope.ServerId), ("$l", scope.LibraryId), ("$n", maximum + 1)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) pending.Add(new(scope.ServerId, scope.LibraryId, reader.GetString(0)));
        if (pending.Count > maximum) throw new InvalidOperationException("Workbench pending limit exceeded.");
        tx.Commit();
        return new(sources, selections, points, pending);
    }

    public Candidate ApproveWorkbenchSelection(MediaKey key, TargetLanguage language, long sourceId,
        string candidateId, long revision, long selectionRevision)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        EnsureNotBusy(db, tx, key); EnsureGenreNotBusy(db, tx, key);
        if (CurrentSource(db, tx, key)?.Id != sourceId) throw new SourceChangedException();
        var selected = Selected(db, tx, sourceId, language);
        if (selected is null || selected.Candidate.Id != candidateId || selected.Revision != selectionRevision
            || selected.Candidate.Revision != revision) throw new RevisionConflictException();
        if (!selected.Candidate.Approved)
            Execute(db, tx, "UPDATE candidates SET approved=1,revision=revision+1,updated_utc=$now WHERE id=$id;",
                ("$now", Now()), ("$id", candidateId));
        var result = CandidateById(db, tx, candidateId);
        tx.Commit(); return result;
    }
}
