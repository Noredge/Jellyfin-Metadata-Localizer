using System.Text.Json;

namespace Localizer.Core;

public sealed record GenreConfirmedMapping(GenreTranslationSeed Source, string Text);
public sealed partial class CandidateStore
{
    public GenreOverride[]? ReadGenreSaveReceipt(Guid id, string fingerprint)
    {
        using var db = Open(); using var cmd = Command(db, null,
            "SELECT fingerprint,payload FROM genre_save_receipts WHERE id=$id", ("$id", id.ToString("N")));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != fingerprint) throw new IdempotencyConflictException();
        return JsonSerializer.Deserialize<GenreOverride[]>(reader.GetString(1))!;
    }

    // All selected mappings and the receipt commit together in the effective dictionary database.
    public GenreOverride[] SaveConfirmedGenres(Guid id, string fingerprint, IReadOnlyList<GenreConfirmedMapping> mappings)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(fingerprint) || mappings.Count is < 1 or > 100)
            throw new ArgumentException();
        if (mappings.Select(x => (x.Source.Original, x.Source.Language)).Distinct().Count() != mappings.Count) throw new ArgumentException();
        foreach (var value in mappings)
        {
            if (!Enum.IsDefined(value.Source.Language) || string.IsNullOrWhiteSpace(value.Source.Original)
                || string.IsNullOrWhiteSpace(value.Text) || value.Text.Length > 500 || value.Text.Any(char.IsControl)) throw new ArgumentException();
        }
        using var lease = AcquireWriterLease(); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var previous = Scalar(db, tx, "SELECT fingerprint FROM genre_save_receipts WHERE id=$id", ("$id", id.ToString("N")));
        if (previous is string old)
        {
            if (old != fingerprint) throw new IdempotencyConflictException();
            var json = (string)Scalar(db, tx, "SELECT payload FROM genre_save_receipts WHERE id=$id", ("$id", id.ToString("N")))!;
            tx.Commit(); return JsonSerializer.Deserialize<GenreOverride[]>(json)!;
        }
        var results = new List<GenreOverride>();
        foreach (var value in mappings)
        {
            var source = value.Source;
            var current = ReadGenreOverride(db, tx, source.Language, source.Original);
            var effective = current?.Replacement ?? GenreDictionary.Lookup(source.Original, source.Language);
            if (effective != source.ExpectedMapping || (current?.Revision ?? 0) != source.MappingRevision) throw new RevisionConflictException();
            results.Add(SetGenreOverride(db, tx, source.Language, source.Original, source.MappingRevision, value.Text));
        }
        Execute(db, tx, "INSERT INTO genre_save_receipts VALUES($id,$f,$p)", ("$id", id.ToString("N")),
            ("$f", fingerprint), ("$p", JsonSerializer.Serialize(results)));
        tx.Commit(); return results.ToArray();
    }
}
