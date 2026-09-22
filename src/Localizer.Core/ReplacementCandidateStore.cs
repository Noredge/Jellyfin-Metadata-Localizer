using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Localizer.Core;

public sealed partial class CandidateStore
{
    // Includes all historical candidate revisions and the selection; an in-flight job may only
    // exclude its own newly generated candidate. Human changes during a run invalidate the plan.
    private static string ReplacementBaseline(SqliteConnection db, SqliteTransaction? tx, long sourceId,
        TargetLanguage language, string? exclude)
    {
        var versions = new List<string>();
        using (var cmd = Command(db, tx, "SELECT id,revision,approved FROM candidates WHERE source_id=$s AND language=$l ORDER BY id;",
            ("$s", sourceId), ("$l", language.Tag())))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) if (reader.GetString(0) != exclude)
                versions.Add(reader.GetString(0) + ":" + reader.GetInt64(1) + ":" + reader.GetInt64(2));
        var selected = Selected(db, tx, sourceId, language);
        var choice = selected is null || selected.Candidate.Id == exclude ? null : selected.Candidate.Id + ":" + selected.Revision;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { versions, choice }))));
    }
    public string GetReplacementBaseline(long sourceId, TargetLanguage language, string? exclude = null)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        var result = ReplacementBaseline(db, tx, sourceId, language, exclude); tx.Commit(); return result;
    }
    public CandidateSelection AcceptReplacementCandidate(MediaKey key, TargetLanguage language, long sourceId,
        string candidateId, string baseline, long selectionRevision)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        EnsureNotBusy(db, tx, key); EnsureGenreNotBusy(db, tx, key);
        if (CurrentSource(db, tx, key)?.Id != sourceId) throw new SourceChangedException();
        var candidate = CandidateById(db, tx, candidateId);
        var selected = Selected(db, tx, sourceId, language);
        if (candidate.SourceId != sourceId || candidate.Language != language || candidate.IsHumanEdited) throw new RevisionConflictException();
        // Exact replay after acceptance, including the selection revision, is safe after restart.
        if (candidate.Approved && candidate.Revision == 2 && selected?.Candidate.Id == candidateId
            && selected.Revision == selectionRevision + 1) { tx.Commit(); return selected; }
        if (candidate.Approved || candidate.Revision != 1 || selected is null
            || ReplacementBaseline(db, tx, sourceId, language, candidateId) != baseline)
            throw new RevisionConflictException();
        if (selected.Candidate.Id != candidateId)
        {
            if (selected.Revision != selectionRevision) throw new RevisionConflictException();
            Execute(db, tx, "UPDATE selections SET candidate_id=$c,revision=revision+1 WHERE source_id=$s AND language=$l;",
                ("$c", candidateId), ("$s", sourceId), ("$l", language.Tag()));
        }
        else if (selectionRevision != 0 || selected.Revision != 1) throw new RevisionConflictException();
        Execute(db, tx, "UPDATE candidates SET approved=1,revision=2,updated_utc=$now WHERE id=$id;", ("$now", Now()), ("$id", candidateId));
        var result = Selected(db, tx, sourceId, language)!; tx.Commit(); return result;
    }
}
