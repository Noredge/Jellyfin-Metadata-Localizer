using Microsoft.Data.Sqlite;

namespace Localizer.Core;

public enum DisplayField { Name, Overview }
public enum DisplayPreferenceMode { FollowLibrary, Original }
public sealed record DisplayPreference(long Revision, DisplayPreferenceMode Mode);

// Stores administrator intent only. Applying it requires a separately validated, journaled write.
public sealed class DisplayPreferenceStore
{
    private readonly string connectionString;
    private readonly string leasePath;
    public DisplayPreferenceStore(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        leasePath = path + ".writer.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA application_id"; var app = Convert.ToInt64(cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA user_version"; var version = Convert.ToInt64(cmd.ExecuteScalar());
        if (app == 0 && version == 0)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
            if (Convert.ToInt64(cmd.ExecuteScalar()) != 0) throw new InvalidOperationException("Not a display preference database.");
            cmd.CommandText = """
                CREATE TABLE display_preferences(server TEXT NOT NULL, library TEXT NOT NULL, item TEXT NOT NULL,
                    field INTEGER NOT NULL, revision INTEGER NOT NULL, mode INTEGER NOT NULL,
                    PRIMARY KEY(server,library,item,field));
                PRAGMA application_id=1246571602; PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery();
        }
        else if (app != 1246571602 || version != 1) throw new InvalidOperationException("Unsupported display preference database.");
        tx.Commit();
    }

    public DisplayPreference Read(MediaKey key, DisplayField field)
    {
        Validate(key, field); using var db = Open(); return Read(db, null, key, field);
    }

    public DisplayPreference Set(MediaKey key, DisplayField field, long expectedRevision, DisplayPreferenceMode mode)
    {
        using var lease = AcquireWriteLease();
        Validate(key, field);
        if (expectedRevision < 0 || !Enum.IsDefined(mode)) throw new ArgumentException();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var current = Read(db, tx, key, field);
        if (current.Revision != expectedRevision) throw new RevisionConflictException();
        if (current.Mode == mode) { tx.Commit(); return current; }
        var next = new DisplayPreference(checked(current.Revision + 1), mode);
        using var cmd = Command(db, tx, key, field);
        cmd.CommandText = """
            INSERT INTO display_preferences VALUES($server,$library,$item,$field,$revision,$mode)
            ON CONFLICT(server,library,item,field) DO UPDATE SET revision=$revision, mode=$mode;
            """;
        cmd.Parameters.AddWithValue("$revision", next.Revision); cmd.Parameters.AddWithValue("$mode", (int)mode);
        cmd.ExecuteNonQuery(); tx.Commit(); return next;
    }

    public IDisposable AcquireWriteLease()
    {
        try { return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new OperationBusyException(); }
    }

    private static DisplayPreference Read(SqliteConnection db, SqliteTransaction? tx, MediaKey key, DisplayField field)
    {
        using var cmd = Command(db, tx, key, field);
        cmd.CommandText = "SELECT revision,mode FROM display_preferences WHERE server=$server AND library=$library AND item=$item AND field=$field";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new(0, DisplayPreferenceMode.FollowLibrary);
        var revision = reader.GetInt64(0); var mode = (DisplayPreferenceMode)reader.GetInt32(1);
        if (revision < 1 || !Enum.IsDefined(mode)) throw new InvalidOperationException("Invalid display preference.");
        return new(revision, mode);
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, MediaKey key, DisplayField field)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$server", key.ServerId); cmd.Parameters.AddWithValue("$library", key.LibraryId);
        cmd.Parameters.AddWithValue("$item", key.ItemId); cmd.Parameters.AddWithValue("$field", (int)field); return cmd;
    }
    private static void Validate(MediaKey key, DisplayField field)
    { key.Validate(); if (!Enum.IsDefined(field)) throw new ArgumentException(); }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
}
