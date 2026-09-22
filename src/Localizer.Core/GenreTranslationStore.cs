using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Localizer.Core;

public sealed record GenreTranslationConnection(string Kind, string Endpoint, int MaxOutputTokens,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool UseJsonResponseFormat = false);
public sealed record GenreTranslationSeed(string Original, TargetLanguage Language, long MappingRevision, string? ExpectedMapping);
public sealed record GenreTranslationTaskItem(GenreTranslationSeed Source, string State = "Pending",
    string? Text = null, string? Error = null, bool HumanEdited = false);
// Configuration must contain no credentials. The service layer will freeze its validated profile separately.
public sealed record GenreTranslationTask(Guid Id, string Fingerprint, string Server, string Library,
    string ServiceId, string Model, string PromptVersion, long Revision, string State,
    GenreTranslationTaskItem[] Items, GenreTranslationConnection? Connection = null, int[]? ActiveItems = null);

/// <summary>Independent dictionary candidate store; never updates effective genre mappings or media.</summary>
public sealed class GenreTranslationStore
{
    private readonly string connectionString;
    public GenreTranslationStore(string path)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA application_id"; var app = Convert.ToInt64(cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA user_version"; var version = Convert.ToInt64(cmd.ExecuteScalar());
        if (app == 0 && version == 0)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
            if (Convert.ToInt64(cmd.ExecuteScalar()) != 0) throw new InvalidOperationException("Not a genre translation database.");
            cmd.CommandText = """
                CREATE TABLE tasks(id TEXT PRIMARY KEY,server TEXT NOT NULL,library TEXT NOT NULL,payload TEXT NOT NULL);
                CREATE INDEX task_scope ON tasks(server,library);
                PRAGMA application_id=1246571601; PRAGMA user_version=1;
                """; cmd.ExecuteNonQuery();
        }
        else if (app != 1246571601 || version != 1) throw new InvalidOperationException("Unsupported genre translation database.");
        tx.Commit();
    }

    public GenreTranslationTask Create(Guid id, string server, string library, string serviceId, string model,
        string promptVersion, IReadOnlyList<GenreTranslationSeed> items, GenreTranslationConnection connection)
    {
        ValidateConnection(connection);
        if (id == Guid.Empty || items.Count is < 1 or > 10000) throw new ArgumentException();
        foreach (var value in new[] { server, library, serviceId, model, promptVersion }) Validate(value, 500);
        foreach (var item in items)
        {
            Validate(item.Original, 500);
            if (!Enum.IsDefined(item.Language) || item.MappingRevision < 0) throw new ArgumentException();
            if (item.ExpectedMapping is not null) Validate(item.ExpectedMapping, 500);
        }
        if (items.Select(x => (x.Original, x.Language)).Distinct().Count() != items.Count) throw new ArgumentException("Duplicate word/language.");
        var frozen = items.OrderBy(x => x.Original, StringComparer.Ordinal).ThenBy(x => x.Language).ToArray();
        var fingerprint = Identity.Hash(JsonSerializer.Serialize(new { server, library, serviceId, model, promptVersion, connection, items = frozen }));
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var old = Read(db, tx, id);
        if (old is not null)
        {
            if (old.Fingerprint != fingerprint) throw new IdempotencyConflictException();
            tx.Commit(); return old;
        }
        var task = new GenreTranslationTask(id, fingerprint, server, library, serviceId, model, promptVersion,
            0, "Ready", frozen.Select(x => new GenreTranslationTaskItem(x)).ToArray(), connection);
        Write(db, tx, task); tx.Commit(); return task;
    }

    public GenreTranslationTask Get(Guid id) { using var db = Open(); return Read(db, null, id) ?? throw new KeyNotFoundException(); }
    public GenreTranslationTask[] List(string server, string library)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM tasks WHERE server=$s AND library=$l ORDER BY rowid DESC LIMIT 100";
        cmd.Parameters.AddWithValue("$s", server); cmd.Parameters.AddWithValue("$l", library);
        using var reader = cmd.ExecuteReader(); var tasks = new List<GenreTranslationTask>();
        while (reader.Read()) tasks.Add(JsonSerializer.Deserialize<GenreTranslationTask>(reader.GetString(0))!);
        return tasks.ToArray();
    }

    // Claim is committed before network IO. A process restart must not claim Sending again.
    public GenreTranslationTask Begin(Guid id, long revision) => Change(id, revision, task =>
    {
        if (task.State != "Ready") throw new OperationBusyException();
        return task with { State = "Sending" };
    });

    public GenreTranslationTask Complete(Guid id, long revision, IReadOnlyList<GenreTranslationTaskItem> results) => Change(id, revision, task =>
    {
        if (task.State != "Sending" || results.Count != task.Items.Length) throw new ArgumentException();
        var expected = task.Items.Select(x => x.Source).ToHashSet();
        if (results.Select(x => x.Source).Distinct().Count() != expected.Count || results.Any(x => !expected.Contains(x.Source))) throw new ArgumentException();
        foreach (var item in results)
        {
            if (item.HumanEdited || item.State is not ("Generated" or "Failed")) throw new ArgumentException();
            if (item.Text is not null) Validate(item.Text, 500);
            if (item.State == "Generated" && (item.Text is null || item.Error is not null)) throw new ArgumentException();
            if (item.State == "Failed") Validate(item.Error!, 100);
        }
        return task with { State = "Completed", Items = results.ToArray() };
    });

    public GenreTranslationTask BeginBatch(Guid id, long revision, int[] indices) => Change(id, revision, task =>
    {
        if (task.State != "Ready") throw new OperationBusyException();
        if (indices.Length is < 1 or > 50 || indices.Distinct().Count() != indices.Length
            || indices.Any(i => i < 0 || i >= task.Items.Length || task.Items[i].State != "Pending")
            || indices.Select(i => task.Items[i].Source.Original).Distinct().Count() > 25) throw new ArgumentException();
        return task with { State = "Sending", ActiveItems = indices.ToArray() };
    });

    public GenreTranslationTask FinishBatch(Guid id, long revision, IReadOnlyList<GenreTranslationTaskItem> results,
        bool uncertain = false, bool pause = false) => Change(id, revision, task =>
    {
        if (task.State != "Sending" || task.ActiveItems is not { Length: > 0 } active || results.Count != active.Length)
            throw new ArgumentException();
        var items = task.Items.ToArray();
        for (var n = 0; n < active.Length; n++)
        {
            var result = results[n];
            if (result.Source != items[active[n]].Source || result.HumanEdited
                || result.State is not ("Generated" or "Failed" or "Uncertain")) throw new ArgumentException();
            if (result.Text is not null) Validate(result.Text, 500);
            if (result.State == "Generated" && (result.Text is null || result.Error is not null)) throw new ArgumentException();
            if (result.State != "Generated") Validate(result.Error!, 100);
            if ((result.State == "Uncertain") != uncertain) throw new ArgumentException();
            items[active[n]] = result;
        }
        return task with { Items = items, ActiveItems = null,
            State = uncertain ? "Uncertain" : pause ? "Paused" : items.Any(x => x.State == "Pending") ? "Ready" : "Completed" };
    });

    // Explicit continuation only; failed/uncertain requests are not reset or resent here.
    public GenreTranslationTask ContinuePending(Guid id, long revision) => Change(id, revision, task =>
    {
        if (task.State != "Paused") throw new OperationBusyException();
        return task with { State = task.Items.Any(x => x.State == "Pending") ? "Ready" : "Completed" };
    });

    // Host must ensure there is no active worker before calling this method.
    // Never turn an uncertain provider outcome into a successful translation.
    public GenreTranslationTask EndStopped(Guid id, long revision) => Change(id, revision, task =>
    {
        if (task.State is not ("Ready" or "Paused" or "Uncertain" or "Sending")) throw new OperationBusyException();
        var items = task.Items.ToArray();
        foreach (var i in task.ActiveItems ?? []) items[i] = items[i] with { State = "Uncertain", Error = "interrupted" };
        for (var i = 0; i < items.Length; i++)
            if (items[i].State == "Pending") items[i] = items[i] with { State = "Skipped", Error = "ended_before_sending" };
        return task with { State = "Ended", Items = items, ActiveItems = null };
    });

    // Called only by startup recovery, before any workers start. No constructor side effects on live tasks.
    public int RecoverInterrupted()
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT payload FROM tasks";
        var pending = new List<GenreTranslationTask>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
        {
            var task = JsonSerializer.Deserialize<GenreTranslationTask>(reader.GetString(0))!;
            if (task.State == "Sending") pending.Add(task);
        }
        foreach (var task in pending)
        {
            var items = task.Items.ToArray();
            foreach (var i in task.ActiveItems ?? []) items[i] = items[i] with { State = "Uncertain", Error = "interrupted" };
            Write(db, tx, task with { State = "Uncertain", Revision = task.Revision + 1, Items = items, ActiveItems = null });
        }
        tx.Commit(); return pending.Count;
    }

    public GenreTranslationTask Edit(Guid id, long revision, string original, TargetLanguage language, string text) => Change(id, revision, task =>
    {
        Validate(text, 500);
        if (task.State is not ("Completed" or "Ended")) throw new OperationBusyException();
        var index = Array.FindIndex(task.Items, x => x.Source.Original == original && x.Source.Language == language);
        if (index < 0) throw new KeyNotFoundException();
        var items = task.Items.ToArray();
        items[index] = items[index] with { Text = text.Trim(), State = "Edited", Error = null, HumanEdited = true };
        return task with { Items = items };
    });

    private GenreTranslationTask Change(Guid id, long revision, Func<GenreTranslationTask, GenreTranslationTask> change)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var old = Read(db, tx, id) ?? throw new KeyNotFoundException();
        if (old.Revision != revision) throw new RevisionConflictException();
        var next = change(old) with { Revision = old.Revision + 1 };
        Write(db, tx, next); tx.Commit(); return next;
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private static GenreTranslationTask? Read(SqliteConnection db, SqliteTransaction? tx, Guid id)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT payload FROM tasks WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString("N"));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<GenreTranslationTask>(json) : null;
    }
    private static void Write(SqliteConnection db, SqliteTransaction tx, GenreTranslationTask task)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO tasks VALUES($id,$s,$l,$p) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        cmd.Parameters.AddWithValue("$id", task.Id.ToString("N")); cmd.Parameters.AddWithValue("$s", task.Server);
        cmd.Parameters.AddWithValue("$l", task.Library); cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(task)); cmd.ExecuteNonQuery();
    }
    private static void ValidateConnection(GenreTranslationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.MaxOutputTokens is < 512 or > 4096) throw new ArgumentException();
        if (connection.Kind == "openai" && connection.Endpoint == "https://api.openai.com/v1"
            || connection.Kind == "groq" && connection.Endpoint == "https://api.groq.com/openai/v1") return;
        // The transport validates public destination addresses before any connection. The store
        // accepts only bounded HTTPS syntax so a frozen custom profile can survive restarts.
        if (connection.Kind == "openai-compatible")
        {
            if (connection.Endpoint is null || connection.Endpoint.Length > 2048
                || connection.Endpoint.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || connection.Endpoint.Contains('\\')
                || !Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var custom) || custom.Scheme != "https"
                || !string.IsNullOrEmpty(custom.UserInfo) || !string.IsNullOrEmpty(custom.Query) || !string.IsNullOrEmpty(custom.Fragment))
                throw new ArgumentException("Invalid dictionary service connection.");
            return;
        }
        if (connection.Kind != "lmstudio" || !Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !uri.IsLoopback || uri.AbsolutePath != "/api/v1"
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Invalid dictionary service connection.");
    }
    private static void Validate(string value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(char.IsControl)) throw new ArgumentException();
    }
}
