using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Localizer.Unified;

/// <summary>Only the unified database is known to runtime storage. No legacy discovery or fallback.</summary>
public sealed class UnifiedDatabase
{
    public const int SchemaVersion = 1;
    public const int ApplicationId = 1246571857;
    public static string SchemaText
    {
        get
        {
            using var stream = typeof(UnifiedDatabase).Assembly.GetManifestResourceStream("Localizer.Unified.Schema.sql")!;
            using var reader = new StreamReader(stream); return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }
    }
    public static string SchemaHash => Hash(SchemaText);
    public string Path { get; }
    public QueueWakeup WorkAvailable { get; }
    public UnifiedDatabase(string root, bool create = false) : this(root, create, new QueueWakeup()) { }
    public UnifiedDatabase(string root, bool create, QueueWakeup workAvailable)
    {
        ArgumentNullException.ThrowIfNull(workAvailable);
        WorkAvailable = workAvailable;
        if (!System.IO.Path.IsPathFullyQualified(root)) throw new ArgumentException("Absolute data root required.");
        Path = System.IO.Path.Combine(root, "metadata-localizer.db");
        if (!File.Exists(Path))
        {
            if (!create) throw new InvalidOperationException("UnifiedDatabaseRequired");
            Directory.CreateDirectory(root);
        }
        using var db = Open(create);
        using var tx = db.BeginTransaction(deferred: false);
        var app = Scalar<long>(db, tx, "PRAGMA application_id");
        var version = Scalar<long>(db, tx, "PRAGMA user_version");
        if (app == 0 && version == 0 && create && Scalar<long>(db, tx,
            "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'") == 0)
        {
            Execute(db, tx, SchemaText);
            Execute(db, tx, "INSERT INTO runtime_state VALUES('schema_definition',$h)", ("$h", SchemaHash));
        }
        else if (app != ApplicationId || version != SchemaVersion)
            throw new InvalidOperationException("UnsupportedUnifiedSchema");
        if (Scalar<string>(db, tx, "SELECT value FROM runtime_state WHERE key='schema_definition'") != SchemaHash)
            throw new InvalidOperationException("UnsupportedUnifiedSchemaDefinition");
        // Additive read index: schema-1 rows and their definition hash remain compatible
        // with prior private versions. Install atomically, without rewriting history.
        Execute(db, tx, """
            CREATE INDEX IF NOT EXISTS work_read_order ON work_items(
              CASE state WHEN 'running' THEN 0 WHEN 'queued' THEN 1 WHEN 'waiting' THEN 2 WHEN 'paused' THEN 3 ELSE 4 END,
              created_utc DESC,id)
            """);
        Execute(db, tx, """
            CREATE INDEX IF NOT EXISTS work_issue_candidates ON work_items(target_kind,target_epoch,term_id,media_id,field)
              WHERE state IN('failed','paused') AND issue_kind IS NOT NULL;
            CREATE INDEX IF NOT EXISTS work_term_history ON work_items(term_id,target_epoch,control_revision) WHERE term_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS work_media_history ON work_items(media_id,field,source_id,source_revision,control_revision,render_revision,target_epoch)
              WHERE media_id IS NOT NULL
            """);
        Execute(db, tx, "CREATE INDEX IF NOT EXISTS source_owner ON source_versions(owner_id)");
        tx.Commit();
        Execute(db, null, "PRAGMA journal_mode=WAL");
        // Reopening a store may have installed an additive index. Retire old cached schemas.
        ReleaseReadPool();
    }
    public SqliteConnection Open() => Open(false);
    private string ReadConnectionString => new SqliteConnectionStringBuilder { DataSource = Path,
        Mode = SqliteOpenMode.ReadOnly, Pooling = true, ForeignKeys = true, DefaultTimeout = 5 }.ToString();
    // A separate read-only pool cannot inherit writable connection state. Each borrower owns
    // its connection and readers; writes still use fresh, unpooled FULL transactions below.
    public SqliteConnection OpenRead()
    {
        using var timing = Localizer.Core.OperationTimings.Measure("db_read_open");
        var db = new SqliteConnection(ReadConnectionString);
        try { db.Open(); Execute(db, null, "PRAGMA busy_timeout=5000; PRAGMA query_only=ON"); return db; }
        catch { db.Dispose(); throw; }
    }
    public void ReleaseReadPool()
    { using var connection = new SqliteConnection(ReadConnectionString); SqliteConnection.ClearPool(connection); }
    private SqliteConnection Open(bool create)
    {
        using var timing = Localizer.Core.OperationTimings.Measure("db_write_open");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false,
            ForeignKeys = true, DefaultTimeout = 5 }.ToString());
        db.Open();
        Execute(db, null, "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL");
        return db;
    }
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation, bool wakeExecutor = true)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var value = operation(db, tx);
        var changed = Scalar<long>(db, tx, "SELECT total_changes()") > 0;
        tx.Commit();
        // Failed/rolled-back and read-only transactions cannot wake the executor.
        // All mutation entry points, including saved display intents, share this commit boundary.
        if (changed && wakeExecutor) WorkAvailable.Notify();
        return value;
    }
    public static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    public static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    { using var cmd = Command(db, tx, sql, args); return cmd.ExecuteNonQuery(); }
    public static T Scalar<T>(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(db, tx, sql, args); var result = cmd.ExecuteScalar();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Id() => Guid.NewGuid().ToString("N");
    public static string Now() => DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
