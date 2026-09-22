using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Localizer.Core;

public sealed partial class CandidateStore
{
    private static void CreateGenreSchema(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, """
        CREATE TABLE genre_sources(id INTEGER PRIMARY KEY, server_id TEXT NOT NULL, library_id TEXT NOT NULL,
            item_id TEXT NOT NULL, version INTEGER NOT NULL, canonical TEXT NOT NULL, original_json TEXT NOT NULL,
            UNIQUE(server_id,library_id,item_id,version), UNIQUE(id,server_id,library_id,item_id));
        CREATE TABLE genre_source_heads(server_id TEXT NOT NULL, library_id TEXT NOT NULL, item_id TEXT NOT NULL,
            source_id INTEGER NOT NULL, PRIMARY KEY(server_id,library_id,item_id),
            FOREIGN KEY(source_id,server_id,library_id,item_id) REFERENCES genre_sources(id,server_id,library_id,item_id));
        CREATE TABLE genre_overrides(language TEXT NOT NULL CHECK(language IN ('zh-Hans','en')), original TEXT NOT NULL,
            replacement TEXT, revision INTEGER NOT NULL CHECK(revision>0), PRIMARY KEY(language,original));
        CREATE TABLE genre_mappings(id TEXT PRIMARY KEY, source_id INTEGER NOT NULL REFERENCES genre_sources(id), payload_json TEXT NOT NULL);
        CREATE TABLE genre_operations(id TEXT PRIMARY KEY, server_id TEXT NOT NULL, library_id TEXT NOT NULL,
            item_id TEXT NOT NULL, state TEXT NOT NULL, payload_json TEXT NOT NULL);
        CREATE UNIQUE INDEX one_pending_genre_operation ON genre_operations(server_id,library_id,item_id)
            WHERE state IN ('Prepared','Writing','Retryable');
        CREATE TABLE genre_restore_heads(server_id TEXT NOT NULL, library_id TEXT NOT NULL, item_id TEXT NOT NULL,
            apply_id TEXT NOT NULL REFERENCES genre_operations(id), consumed INTEGER NOT NULL CHECK(consumed IN (0,1)),
            PRIMARY KEY(server_id,library_id,item_id));
        PRAGMA user_version=3;
        """);

    // Explicit independently confirmed source, never automatic adoption of the current translated Genres.
    public GenreSource ObserveGenreSource(MediaKey key, IEnumerable<string> originalValues)
    {
        key.Validate();
        var values = GenreValues.Copy(originalValues);
        var canonical = GenreValues.Canonical(values);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var previous = CurrentGenreSource(db, tx, key);
        if (previous is not null && GenreValues.Equal(previous.OriginalValues, values)) { tx.Commit(); return previous; }
        EnsureGenreNotBusy(db, tx, key);
        var version = (previous?.Version ?? 0) + 1;
        Execute(db, tx, "INSERT INTO genre_sources(server_id,library_id,item_id,version,canonical,original_json) VALUES($s,$l,$i,$v,$c,$j);",
            ("$s", key.ServerId), ("$l", key.LibraryId), ("$i", key.ItemId), ("$v", version), ("$c", canonical), ("$j", JsonSerializer.Serialize(values)));
        var id = Convert.ToInt64(Scalar(db, tx, "SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
        Execute(db, tx, """
            INSERT INTO genre_source_heads VALUES($s,$l,$i,$id)
            ON CONFLICT(server_id,library_id,item_id) DO UPDATE SET source_id=excluded.source_id;
            """, ("$s", key.ServerId), ("$l", key.LibraryId), ("$i", key.ItemId), ("$id", id));
        tx.Commit(); return new(id, key, version, values);
    }
    public GenreSource? GetCurrentGenreSource(MediaKey key) { using var db = Open(); return CurrentGenreSource(db, null, key); }
    public IReadOnlyList<GenreSource> ListCurrentGenreSources(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT s.id,s.library_id,s.item_id,s.version,s.original_json
            FROM genre_source_heads h JOIN genre_sources s ON s.id=h.source_id
            WHERE h.server_id=$s ORDER BY s.library_id,s.item_id;
            """, ("$s", serverId));
        using var reader = cmd.ExecuteReader(); var sources = new List<GenreSource>();
        while (reader.Read()) sources.Add(new(reader.GetInt64(0), new(serverId, reader.GetString(1), reader.GetString(2)),
            reader.GetInt64(3), JsonSerializer.Deserialize<string[]>(reader.GetString(4))!));
        return sources;
    }
    private static GenreSource? CurrentGenreSource(SqliteConnection db, SqliteTransaction? tx, MediaKey key)
    {
        using var cmd = Command(db, tx, """
            SELECT s.id,s.version,s.original_json FROM genre_sources s JOIN genre_source_heads h ON s.id=h.source_id
            WHERE h.server_id=$s AND h.library_id=$l AND h.item_id=$i;
            """, ("$s", key.ServerId), ("$l", key.LibraryId), ("$i", key.ItemId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(r.GetInt64(0), key, r.GetInt64(1), JsonSerializer.Deserialize<string[]>(r.GetString(2))!) : null;
    }
    private static void EnsureGenreNotBusy(SqliteConnection db, SqliteTransaction tx, MediaKey key)
    {
        if (Scalar(db, tx, """
            SELECT id FROM genre_operations WHERE server_id=$s AND library_id=$l AND item_id=$i
            AND state IN ('Prepared','Writing','Retryable') LIMIT 1;
            """, ("$s", key.ServerId), ("$l", key.LibraryId), ("$i", key.ItemId)) is not null) throw new OperationBusyException();
    }
    public GenreOverride? GetGenreOverride(TargetLanguage language, string original)
    { using var db = Open(); return ReadGenreOverride(db, null, language, original); }
    public GenreDictionaryEntry GetGenreDictionaryEntry(TargetLanguage language, string original)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(original);
        var user = GetGenreOverride(language, original);
        var builtin = GenreDictionary.Lookup(original, language);
        var effective = user?.Replacement ?? builtin;
        return new(original, builtin, user?.Replacement, effective, user?.Revision ?? 0, effective is null);
    }
    private static GenreOverride? ReadGenreOverride(SqliteConnection db, SqliteTransaction? tx, TargetLanguage language, string original)
    {
        using var cmd = Command(db, tx, "SELECT replacement,revision FROM genre_overrides WHERE language=$l AND original=$o;",
            ("$l", language.Tag()), ("$o", original));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(language, original, r.IsDBNull(0) ? null : r.GetString(0), r.GetInt64(1)) : null;
    }
    // null clears an override while preserving its revision tombstone for concurrent editors.
    public GenreOverride SetGenreOverride(TargetLanguage language, string original, long expectedRevision, string? replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(original);
        if (replacement is not null) ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
        using var lease = AcquireWriterLease();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var result = SetGenreOverride(db, tx, language, original, expectedRevision, replacement);
        tx.Commit(); return result;
    }
    private static GenreOverride SetGenreOverride(SqliteConnection db, SqliteTransaction tx, TargetLanguage language,
        string original, long expectedRevision, string? replacement)
    {
        var previous = ReadGenreOverride(db, tx, language, original);
        if ((previous?.Revision ?? 0) != expectedRevision) throw new RevisionConflictException();
        // Related pending operations freeze their dictionary inputs. Unrelated words/languages remain editable.
        using (var cmd = Command(db, tx, """
            SELECT s.original_json,o.payload_json,s.id FROM genre_operations o JOIN genre_sources s
            ON s.server_id=o.server_id AND s.library_id=o.library_id AND s.item_id=o.item_id
            WHERE o.state IN ('Prepared','Writing','Retryable');
            """))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var operation = JsonSerializer.Deserialize<GenreOperation>(r.GetString(1))!;
                if (r.GetInt64(2) == operation.SourceId && operation.Kind == OperationKind.Apply && operation.Language == language
                    && JsonSerializer.Deserialize<string[]>(r.GetString(0))!.Contains(original, StringComparer.Ordinal))
                    throw new OperationBusyException();
            }
        }
        if (previous is not null && previous.Replacement == replacement) return previous;
        var revision = (previous?.Revision ?? 0) + 1;
        Execute(db, tx, """
            INSERT INTO genre_overrides VALUES($l,$o,$v,$r)
            ON CONFLICT(language,original) DO UPDATE SET replacement=excluded.replacement,revision=excluded.revision;
            """, ("$l", language.Tag()), ("$o", original), ("$v", replacement), ("$r", revision));
        return new(language, original, replacement, revision);
    }
    private static GenreMapping ComputeGenreMapping(SqliteConnection db, SqliteTransaction? tx, GenreSource source, TargetLanguage language)
    {
        var inputs = source.OriginalValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(original =>
        {
            var user = ReadGenreOverride(db, tx, language, original);
            var translated = user?.Replacement ?? GenreDictionary.Lookup(original, language);
            return new { Original = original, Replacement = translated, Revision = user?.Revision ?? 0 };
        }).ToArray();
        // Only relevant effective entries participate; unrelated dictionary edits do not invalidate this mapping.
        var fingerprint = Identity.Hash(JsonSerializer.Serialize(new { source.Id, source.Key, Language = language.Tag(), Inputs = inputs }));
        var values = inputs.Select(x => x.Replacement ?? x.Original).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(fingerprint, source.Id, language, values, inputs.Where(x => x.Replacement is null).Select(x => x.Original).ToArray());
    }
    public GenreMapping GetGenreMapping(MediaKey key, TargetLanguage language)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var source = CurrentGenreSource(db, tx, key) ?? throw new OperationValidationException("GenreSourceMissing");
        var mapping = ComputeGenreMapping(db, tx, source, language);
        Execute(db, tx, "INSERT INTO genre_mappings VALUES($id,$s,$j) ON CONFLICT(id) DO NOTHING;",
            ("$id", mapping.Id), ("$s", source.Id), ("$j", JsonSerializer.Serialize(mapping)));
        tx.Commit(); return mapping;
    }
    public GenrePreviewEntry BuildGenrePreview(LibraryKey scope, TargetLanguage language, GenreTargetSnapshot current)
    {
        var source = GetCurrentGenreSource(current.Key);
        var inScope = current.Key.ServerId == scope.ServerId && current.Key.LibraryId == scope.LibraryId && current.IsMovie;
        if (!inScope || source is null)
            return new(current.Key, language, !inScope ? PreviewStatus.OutOfScope : PreviewStatus.MissingSource,
                source?.Id ?? 0, "", GenreValues.Copy(current.Values), [], []);
        var mapping = GetGenreMapping(current.Key, language);
        var status = current.Locked ? PreviewStatus.Locked : GenreValues.Equal(current.Values, mapping.Values)
            ? PreviewStatus.AlreadyMatches : PreviewStatus.ReadyForReview;
        return new(current.Key, language, status, source.Id, mapping.Id, GenreValues.Copy(current.Values), mapping.Values, mapping.UnknownValues);
    }
}
