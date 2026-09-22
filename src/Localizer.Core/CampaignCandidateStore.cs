using System.Globalization;

namespace Localizer.Core;

public sealed partial class CandidateStore
{
    // An unknown outcome belongs to its original queue even after a campaign is cancelled.
    // Starting a fresh campaign must not bypass the queue's explicit uncertain-request consent.
    public IReadOnlyDictionary<string, string[]> CampaignTranslationReservations(LibraryKey scope, TargetLanguage language)
    {
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT json_extract(i.payload_json,'$.Source.Key.ItemId'), i.job_id
            FROM translation_items i JOIN translation_jobs j ON j.id=i.job_id
            WHERE json_extract(j.payload_json,'$.Scope.ServerId')=$s
                AND json_extract(j.payload_json,'$.Scope.LibraryId')=$l
                AND json_extract(j.payload_json,'$.Language')=$lang
                AND json_extract(i.payload_json,'$.State') IN ($requesting,$received,$uncertain);
            """, ("$s", scope.ServerId), ("$l", scope.LibraryId), ("$lang", (int)language),
            ("$requesting", (int)TranslationItemState.Requesting), ("$received", (int)TranslationItemState.Received),
            ("$uncertain", (int)TranslationItemState.Uncertain));
        using var reader = cmd.ExecuteReader(); var rows = new List<(string Item, string Job)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows.GroupBy(x => x.Item, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Job).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    // The campaign journals its automatic-acceptance intent before calling this method. Approval is
    // the existing technical write gate, never a claim that a person reviewed the generated title.
    // Only an untouched first selection with no other candidate can cross that gate automatically.
    public Candidate AcceptCampaignCandidate(MediaKey key, TargetLanguage language, long sourceId, string candidateId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        EnsureNotBusy(db, tx, key); EnsureGenreNotBusy(db, tx, key);
        if (CurrentSource(db, tx, key)?.Id != sourceId) throw new SourceChangedException();
        var selected = Selected(db, tx, sourceId, language);
        if (selected is null || selected.Revision != 1 || selected.Candidate.Id != candidateId
            || selected.Candidate.IsHumanEdited
            || Convert.ToInt64(Scalar(db, tx, "SELECT COUNT(*) FROM candidates WHERE source_id=$s AND language=$l;",
                ("$s", sourceId), ("$l", language.Tag())), CultureInfo.InvariantCulture) != 1)
            throw new RevisionConflictException();
        var candidate = selected.Candidate;
        if (!candidate.Approved && candidate.Revision == 1)
            Execute(db, tx, "UPDATE candidates SET approved=1,revision=2,updated_utc=$now WHERE id=$id;", ("$now", Now()), ("$id", candidateId));
        else if (!candidate.Approved || candidate.Revision != 2) throw new RevisionConflictException();
        var result = CandidateById(db, tx, candidateId); tx.Commit(); return result;
    }
}
