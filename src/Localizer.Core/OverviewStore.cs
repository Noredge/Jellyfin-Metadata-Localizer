using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Localizer.Core;

public sealed record OverviewSource(long Version, string Text, string NfoPath, string Hash);
public sealed record OverviewCandidate(string Id, long SourceVersion, TargetLanguage Language, string Text,
    bool HumanEdited, string Provider, string Model, string PromptVersion);
public sealed record OverviewFailure(long SourceVersion, TargetLanguage Language, string Category);
public sealed record OverviewState(MediaKey Key, long Revision, OverviewSource[] Sources,
    OverviewCandidate[] Candidates, OverviewFailure[] Failures)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public OverviewSource? Source => Sources.LastOrDefault();
    public OverviewCandidate? Selected(TargetLanguage language) => Candidates.LastOrDefault(x =>
        x.SourceVersion == Source?.Version && x.Language == language);
}

/// <summary>Independent synopsis history. Never uses the displayed Jellyfin overview as original input.</summary>
public sealed class OverviewStore
{
    private readonly string connectionString;
    public OverviewStore(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false,
            DefaultTimeout = 5 }.ToString();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var check = db.CreateCommand(); check.Transaction = tx;
        check.CommandText = "PRAGMA application_id";
        var app = Convert.ToInt64(check.ExecuteScalar());
        check.CommandText = "PRAGMA user_version"; var version = Convert.ToInt64(check.ExecuteScalar());
        if (app == 0 && version == 0)
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
            if (Convert.ToInt64(check.ExecuteScalar()) != 0) throw new InvalidOperationException("Not an overview database.");
            check.CommandText = """
                CREATE TABLE overviews(server TEXT NOT NULL, library TEXT NOT NULL, item TEXT NOT NULL,
                    revision INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(server,library,item));
                PRAGMA application_id=1246571599; PRAGMA user_version=1;
                """;
            check.ExecuteNonQuery();
        }
        else if (app != 1246571599 || version != 1) throw new InvalidOperationException("Unsupported overview database.");
        tx.Commit();
    }
    public OverviewState Read(MediaKey key)
    {
        key.Validate(); using var db = Open(); return Read(db, null, key);
    }
    public OverviewState Observe(MediaKey key, long revision, NfoOverviewRead source)
    {
        if (source.Error is not null || source.Text is null || source.Path is null || source.Hash is null)
            throw new ArgumentException("A verified NFO overview is required.");
        ValidateText(source.Text);
        if (!Path.IsPathFullyQualified(source.Path) || Identity.Hash(source.Text) != source.Hash) throw new ArgumentException();
        return Change(key, revision, state => state.Source is { } old && old.Text == source.Text && old.NfoPath == source.Path
            ? state : state with { Sources = [..state.Sources, new((state.Source?.Version ?? 0) + 1, source.Text, source.Path, source.Hash)] });
    }
    public OverviewState Save(MediaKey key, long revision, long sourceVersion, TargetLanguage language,
        string text, bool humanEdited, string provider, string model, string promptVersion, bool preserveManual = false, string? candidateId = null)
    {
        ValidateText(text); _ = language.Tag();
        ArgumentException.ThrowIfNullOrWhiteSpace(provider); ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        if (candidateId is not null && !Guid.TryParseExact(candidateId, "N", out _)) throw new ArgumentException();
        return Change(key, revision, state =>
        {
            RequireSource(state, sourceVersion);
            if (candidateId is not null && state.Candidates.SingleOrDefault(x => x.Id == candidateId) is { } prior)
            {
                if (prior.SourceVersion != sourceVersion || prior.Language != language || prior.Text != text
                    || prior.Provider != provider || prior.Model != model || prior.PromptVersion != promptVersion || prior.HumanEdited != humanEdited)
                    throw new IdempotencyConflictException();
                return state;
            }
            if (preserveManual && state.Selected(language)?.HumanEdited == true) return state;
            return state with { Candidates = [..state.Candidates, new(candidateId ?? Guid.NewGuid().ToString("N"), sourceVersion,
                language, text, humanEdited, provider, model, promptVersion)],
                Failures = state.Failures.Where(x => x.SourceVersion != sourceVersion || x.Language != language).ToArray() };
        });
    }
    public OverviewState Fail(MediaKey key, long revision, long sourceVersion, TargetLanguage language, string category)
    {
        _ = language.Tag();
        if (category is not ("rate_limit" or "refusal" or "output_budget" or "invalid_output" or "network"
            or "authentication" or "service_unavailable" or "interrupted")) throw new ArgumentException();
        return Change(key, revision, state =>
        {
            RequireSource(state, sourceVersion);
            return state with { Failures = [..state.Failures.Where(x => x.SourceVersion != sourceVersion || x.Language != language),
                new(sourceVersion, language, category)] };
        });
    }
    public static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 24000 || text.Contains('\0')) throw new ArgumentException("Invalid overview text.");
    }
    private static void RequireSource(OverviewState state, long version)
    { if (state.Source?.Version != version) throw new SourceChangedException(); }
    private OverviewState Change(MediaKey key, long revision, Func<OverviewState, OverviewState> update)
    {
        key.Validate(); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var old = Read(db, tx, key);
        if (old.Revision != revision) throw new RevisionConflictException();
        var next = update(old);
        if (ReferenceEquals(old, next)) { tx.Commit(); return old; }
        next = next with { Revision = checked(old.Revision + 1) };
        using var cmd = KeyCommand(db, tx, key);
        cmd.CommandText = """
            INSERT INTO overviews VALUES($s,$l,$i,$r,$p)
            ON CONFLICT(server,library,item) DO UPDATE SET revision=excluded.revision,payload=excluded.payload;
            """;
        cmd.Parameters.AddWithValue("$r", next.Revision); cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(next));
        cmd.ExecuteNonQuery(); tx.Commit(); return next;
    }
    private static OverviewState Read(SqliteConnection db, SqliteTransaction? tx, MediaKey key)
    {
        using var cmd = KeyCommand(db, tx, key);
        cmd.CommandText = "SELECT payload FROM overviews WHERE server=$s AND library=$l AND item=$i";
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<OverviewState>(json)!
            : new(key, 0, [], [], []);
    }
    private static SqliteCommand KeyCommand(SqliteConnection db, SqliteTransaction? tx, MediaKey key)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$s", key.ServerId); cmd.Parameters.AddWithValue("$l", key.LibraryId);
        cmd.Parameters.AddWithValue("$i", key.ItemId); return cmd;
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA synchronous=FULL"; cmd.ExecuteNonQuery(); return db;
    }
}
