using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Localizer.Core;

/// <summary>Plugin-owned title candidates only; no Jellyfin or provider transport.</summary>
public sealed partial class CandidateStore
{
    private readonly string connectionString;
    private readonly string databasePath;

    public CandidateStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var path = Path.GetFullPath(databasePath);
        this.databasePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true, Pooling = false, DefaultTimeout = 5
        }.ToString();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var schema = Convert.ToInt64(Scalar(db, tx, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        var application = Convert.ToInt64(Scalar(db, tx, "PRAGMA application_id;"), CultureInfo.InvariantCulture);
        if (schema is not (0 or 1 or 2 or 3 or 4 or 5)) throw new InvalidOperationException("Unsupported candidate database schema.");
        if ((schema > 0 && application != 0x4A4D4C43) || (schema == 0 && (application != 0 ||
            Convert.ToInt64(Scalar(db,tx,"SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';"),CultureInfo.InvariantCulture) != 0)))
            throw new InvalidOperationException("Database is not an owned Localizer candidate store.");
        if (schema == 0)
        {
            Execute(db, tx, """
                CREATE TABLE sources (
                    id INTEGER PRIMARY KEY, server_id TEXT NOT NULL, library_id TEXT NOT NULL, item_id TEXT NOT NULL,
                    version INTEGER NOT NULL, original_title TEXT NOT NULL, display_prefix TEXT NOT NULL,
                    source_hash TEXT NOT NULL, created_utc TEXT NOT NULL,
                    UNIQUE(server_id, library_id, item_id, version), UNIQUE(id, server_id, library_id, item_id));
                CREATE TABLE source_heads (
                    server_id TEXT NOT NULL, library_id TEXT NOT NULL, item_id TEXT NOT NULL, source_id INTEGER NOT NULL,
                    PRIMARY KEY(server_id, library_id, item_id),
                    FOREIGN KEY(source_id,server_id,library_id,item_id) REFERENCES sources(id,server_id,library_id,item_id));
                CREATE TABLE candidates (
                    id TEXT PRIMARY KEY, source_id INTEGER NOT NULL REFERENCES sources(id),
                    language TEXT NOT NULL CHECK(language IN ('zh-Hans','en')),
                    input_fingerprint TEXT NOT NULL, generated_text TEXT NOT NULL, edited_text TEXT,
                    revision INTEGER NOT NULL CHECK(revision>0), approved INTEGER NOT NULL CHECK(approved IN (0,1)),
                    provenance_json TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                    UNIQUE(source_id,language,input_fingerprint), UNIQUE(id,source_id,language));
                CREATE TABLE selections (
                    source_id INTEGER NOT NULL, language TEXT NOT NULL, candidate_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision>0), PRIMARY KEY(source_id,language),
                    FOREIGN KEY(candidate_id,source_id,language) REFERENCES candidates(id,source_id,language));
                PRAGMA user_version=1;
                PRAGMA application_id=0x4A4D4C43;
                """);
        }
        if (schema < 2) CreateOperationSchema(db,tx);
        if (schema < 3) CreateGenreSchema(db,tx);
        if (schema < 4) CreateTranslationSchema(db,tx);
        if (schema < 5) Execute(db, tx, "CREATE TABLE genre_save_receipts(id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, payload TEXT NOT NULL); PRAGMA user_version=5;");
        tx.Commit();
        Execute(db, null, "PRAGMA journal_mode=WAL;");
    }

    // Caller supplies an independently verified original title; never pass the displayed translation as source.
    public SourceSnapshot ObserveSource(MediaKey key, string originalTitle, string displayPrefix)
    {
        key.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(originalTitle);
        ArgumentNullException.ThrowIfNull(displayPrefix);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var previous = CurrentSource(db, tx, key);
        if (previous is not null && previous.OriginalTitle == originalTitle && previous.DisplayPrefix == displayPrefix)
        {
            tx.Commit();
            return previous;
        }
        var version = (previous?.Version ?? 0) + 1;
        EnsureNotBusy(db,tx,key);
        Execute(db, tx, """
            INSERT INTO sources(server_id,library_id,item_id,version,original_title,display_prefix,source_hash,created_utc)
            VALUES($s,$l,$i,$v,$o,$p,$h,$now);
            """, ("$s",key.ServerId),("$l",key.LibraryId),("$i",key.ItemId),("$v",version),
            ("$o",originalTitle),("$p",displayPrefix),("$h",Identity.Hash(originalTitle)),("$now",Now()));
        var id = Convert.ToInt64(Scalar(db, tx, "SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
        Execute(db, tx, """
            INSERT INTO source_heads VALUES($s,$l,$i,$id)
            ON CONFLICT(server_id,library_id,item_id) DO UPDATE SET source_id=excluded.source_id;
            """, ("$s",key.ServerId),("$l",key.LibraryId),("$i",key.ItemId),("$id",id));
        tx.Commit();
        return new(id,key,version,originalTitle,displayPrefix,Identity.Hash(originalTitle));
    }

    public SourceSnapshot? GetCurrentSource(MediaKey key)
    {
        key.Validate();
        using var db = Open();
        return CurrentSource(db,null,key);
    }

    // Only validated final title text enters here. All new candidates still require review.
    public Candidate SaveGenerated(long sourceId, TargetLanguage language, string text, GenerationSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();
        var tag = language.Tag();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var source = SourceById(db,tx,sourceId);
        var fingerprint = Identity.Fingerprint(source,language,spec);
        var id = Guid.NewGuid().ToString("N");
        var now = Now();
        Execute(db,tx,"""
            INSERT INTO candidates(id,source_id,language,input_fingerprint,generated_text,revision,approved,provenance_json,created_utc,updated_utc)
            VALUES($id,$source,$lang,$fp,$text,1,0,$spec,$now,$now)
            ON CONFLICT(source_id,language,input_fingerprint) DO NOTHING;
            """,("$id",id),("$source",sourceId),("$lang",tag),("$fp",fingerprint),("$text",text),
            ("$spec",JsonSerializer.Serialize(spec)),("$now",now));
        var savedId = (string)Scalar(db,tx,"SELECT id FROM candidates WHERE source_id=$s AND language=$l AND input_fingerprint=$f;",
            ("$s",sourceId),("$l",tag),("$f",fingerprint))!;
        // Regeneration never silently replaces the selected candidate or its human edits.
        Execute(db,tx,"""
            INSERT INTO selections VALUES($s,$l,$c,1) ON CONFLICT(source_id,language) DO NOTHING;
            """,("$s",sourceId),("$l",tag),("$c",savedId));
        var result = CandidateById(db,tx,savedId);
        tx.Commit();
        return result;
    }

    public Candidate GetCandidate(string id)
    {
        using var db = Open();
        return CandidateById(db,null,id);
    }

    public IReadOnlyList<Candidate> ListCandidates(long sourceId, TargetLanguage language)
    {
        using var db = Open();
        using var cmd = Command(db,null,"SELECT * FROM candidates WHERE source_id=$s AND language=$l ORDER BY created_utc,id;",
            ("$s",sourceId),("$l",language.Tag()));
        using var reader = cmd.ExecuteReader();
        var result = new List<Candidate>();
        while (reader.Read()) result.Add(ReadCandidate(reader));
        return result;
    }

    public CandidateSelection? GetSelected(long sourceId, TargetLanguage language)
    {
        using var db = Open();
        return Selected(db,null,sourceId,language);
    }

    public Candidate Edit(string id, long expectedRevision, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        EnsureNotBusy(db,tx,SourceById(db,tx,CandidateById(db,tx,id).SourceId).Key);
        var changed = Execute(db,tx,"""
            UPDATE candidates SET edited_text=$text,approved=0,revision=revision+1,updated_utc=$now
            WHERE id=$id AND revision=$revision;
            """,("$text",text),("$now",Now()),("$id",id),("$revision",expectedRevision));
        if (changed != 1) throw new RevisionConflictException();
        var result = CandidateById(db,tx,id);
        tx.Commit();
        return result;
    }

    public Candidate Approve(string id, long expectedRevision)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var existing = CandidateById(db,tx,id);
        if (existing.Revision != expectedRevision) throw new RevisionConflictException();
        if (!existing.Approved)
            Execute(db,tx,"UPDATE candidates SET approved=1,revision=revision+1,updated_utc=$now WHERE id=$id;",("$now",Now()),("$id",id));
        var result = CandidateById(db,tx,id);
        tx.Commit();
        return result;
    }

    public CandidateSelection Select(string id, long expectedSelectionRevision)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var candidate = CandidateById(db,tx,id);
        var source = SourceById(db,tx,candidate.SourceId);
        EnsureNotBusy(db,tx,source.Key);
        if (CurrentSource(db,tx,source.Key)?.Id != source.Id) throw new SourceChangedException();
        var existing = Selected(db,tx,source.Id,candidate.Language) ?? throw new InvalidOperationException("Missing candidate selection.");
        if (existing.Revision != expectedSelectionRevision) throw new RevisionConflictException();
        if (existing.Candidate.Id != candidate.Id)
            Execute(db,tx,"UPDATE selections SET candidate_id=$c,revision=revision+1 WHERE source_id=$s AND language=$l;",
                ("$c",id),("$s",source.Id),("$l",candidate.Language.Tag()));
        var result = Selected(db,tx,source.Id,candidate.Language)!;
        tx.Commit();
        return result;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        Execute(db,null,"PRAGMA synchronous=FULL;");
        return db;
    }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture);
    private static SourceSnapshot? CurrentSource(SqliteConnection db, SqliteTransaction? tx, MediaKey key)
    {
        using var cmd = Command(db,tx,"""
            SELECT s.* FROM sources s JOIN source_heads h ON h.source_id=s.id
            WHERE h.server_id=$s AND h.library_id=$l AND h.item_id=$i;
            """,("$s",key.ServerId),("$l",key.LibraryId),("$i",key.ItemId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadSource(reader) : null;
    }
    private static SourceSnapshot SourceById(SqliteConnection db, SqliteTransaction? tx, long id)
    {
        using var cmd = Command(db,tx,"SELECT * FROM sources WHERE id=$id;",("$id",id));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Source version does not exist.");
        return ReadSource(reader);
    }
    private static SourceSnapshot ReadSource(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")),new(r.GetString(r.GetOrdinal("server_id")),r.GetString(r.GetOrdinal("library_id")),r.GetString(r.GetOrdinal("item_id"))),
        r.GetInt64(r.GetOrdinal("version")),r.GetString(r.GetOrdinal("original_title")),r.GetString(r.GetOrdinal("display_prefix")),r.GetString(r.GetOrdinal("source_hash")));
    private static Candidate CandidateById(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var cmd = Command(db,tx,"SELECT * FROM candidates WHERE id=$id;",("$id",id));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Candidate does not exist.");
        return ReadCandidate(reader);
    }
    private static Candidate ReadCandidate(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("id")),r.GetInt64(r.GetOrdinal("source_id")),Languages.Parse(r.GetString(r.GetOrdinal("language"))),
        r.GetString(r.GetOrdinal("input_fingerprint")),r.GetString(r.GetOrdinal("generated_text")),
        r.IsDBNull(r.GetOrdinal("edited_text")) ? null : r.GetString(r.GetOrdinal("edited_text")),
        r.GetInt64(r.GetOrdinal("revision")),r.GetBoolean(r.GetOrdinal("approved")),r.GetString(r.GetOrdinal("provenance_json")));
    private static CandidateSelection? Selected(SqliteConnection db, SqliteTransaction? tx, long sourceId, TargetLanguage language)
    {
        using var cmd = Command(db,tx,"""
            SELECT c.*,s.revision AS selection_revision FROM candidates c JOIN selections s ON c.id=s.candidate_id
            WHERE s.source_id=$s AND s.language=$l;
            """,("$s",sourceId),("$l",language.Tag()));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(ReadCandidate(reader),reader.GetInt64(reader.GetOrdinal("selection_revision"))) : null;
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name,object? Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name,value) in parameters) cmd.Parameters.AddWithValue(name,value ?? DBNull.Value);
        return cmd;
    }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name,object? Value)[] parameters)
    {
        using var cmd = Command(db,tx,sql,parameters);
        return cmd.ExecuteNonQuery();
    }
    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name,object? Value)[] parameters)
    {
        using var cmd = Command(db,tx,sql,parameters);
        return cmd.ExecuteScalar();
    }
}
