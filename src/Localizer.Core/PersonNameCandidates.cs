using System.Text.Json;

namespace Localizer.Core;

public sealed record PersonNameRefresh(string PreviousCandidateId, long PreviousCandidateRevision, long PreviousSelectionRevision, string Text);

public sealed partial class CandidateStore
{
    public IReadOnlyDictionary<string, string> ReadNameTemplates(LibraryKey scope, TargetLanguage language)
    {
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT json_extract(i.payload_json,'$.CandidateId'),json_extract(i.payload_json,'$.Result.NameTemplate')
            FROM translation_items i JOIN translation_jobs j ON j.id=i.job_id
            WHERE json_extract(j.payload_json,'$.Scope.ServerId')=$s AND json_extract(j.payload_json,'$.Scope.LibraryId')=$l
              AND json_extract(j.payload_json,'$.Language')=$lang
              AND json_extract(i.payload_json,'$.CandidateId') IS NOT NULL
              AND json_extract(i.payload_json,'$.Result.NameTemplate') IS NOT NULL;
            """, ("$s", scope.ServerId), ("$l", scope.LibraryId), ("$lang", (int)language));
        using var reader = cmd.ExecuteReader(); var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var id = reader.GetString(0); var template = reader.GetString(1);
            if (result.TryGetValue(id, out var prior) && prior != template) throw new InvalidOperationException("AmbiguousNameTemplate");
            result[id] = template;
        }
        return result;
    }

    // The durable campaign owns this exact derivation. No model call and no mutation of its parent candidate.
    public CandidateSelection DeriveNamedCandidate(MediaKey key, long sourceId, TargetLanguage language, PersonNameRefresh input, GenerationSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Text); spec.Validate();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        EnsureNotBusy(db, tx, key); EnsureGenreNotBusy(db, tx, key);
        var source = CurrentSource(db, tx, key);
        if (source?.Id != sourceId) throw new SourceChangedException();
        var selected = Selected(db, tx, sourceId, language) ?? throw new SourceChangedException();
        var fingerprint = Identity.Fingerprint(source, language, spec);
        var id = Scalar(db, tx, "SELECT id FROM candidates WHERE source_id=$s AND language=$l AND input_fingerprint=$f;",
            ("$s", sourceId), ("$l", language.Tag()), ("$f", fingerprint)) as string;
        if (id is not null)
        {
            var own = CandidateById(db, tx, id);
            if (selected.Candidate.Id != id || selected.Revision != input.PreviousSelectionRevision + 1 || own.Revision != 1
                || own.IsHumanEdited || !own.Approved || own.GeneratedText != input.Text) throw new RevisionConflictException();
            tx.Commit(); return selected;
        }
        if (selected.Candidate.Id != input.PreviousCandidateId || selected.Candidate.Revision != input.PreviousCandidateRevision
            || selected.Revision != input.PreviousSelectionRevision || selected.Candidate.IsHumanEdited || !selected.Candidate.Approved)
            throw new RevisionConflictException();
        id = Guid.NewGuid().ToString("N"); var now = Now();
        Execute(db, tx, """
            INSERT INTO candidates(id,source_id,language,input_fingerprint,generated_text,revision,approved,provenance_json,created_utc,updated_utc)
            VALUES($id,$s,$l,$f,$text,1,1,$spec,$now,$now);
            """, ("$id", id), ("$s", sourceId), ("$l", language.Tag()), ("$f", fingerprint), ("$text", input.Text),
            ("$spec", JsonSerializer.Serialize(spec)), ("$now", now));
        Execute(db, tx, "UPDATE selections SET candidate_id=$id,revision=revision+1 WHERE source_id=$s AND language=$l;",
            ("$id", id), ("$s", sourceId), ("$l", language.Tag()));
        var result = Selected(db, tx, sourceId, language)!; tx.Commit(); return result;
    }
}
