using System.Text.Json;
using Localizer.Core;
using Localizer.Translation;

namespace Localizer.Plugin;

public sealed record OverviewCampaignPlan(long SourceVersion, string SourceHash, string ExpectedDisplay,
    string? BaselineCandidateId, OverviewTranslationInput Input);

public sealed record CampaignItem(Guid ItemId, string Name, string Observation, string JobId, string OperationId,
    string State = "Pending", string? ErrorCategory = null, SourceSnapshot? Source = null,
    GenerationSpec? Spec = null, string? CandidateId = null, string? Acceptance = null,
    LanguagePreviewEntry? Preview = null, string[]? PreviousOperationIds = null, PersonNameRefresh? NameRefresh = null,
    string? Language = null, string Field = "Name", string? BaselineHash = null,
    long BaselineSelectionRevision = 0, GenrePreviewEntry? GenrePreview = null, OverviewCampaignPlan? OverviewPlan = null,
    string? TranslationOrigin = null, long OriginalRevision = 0);
public sealed record CampaignRecord(Guid Id, LibraryKey Scope, string Language, string Mode, string State,
    string CreatedUtc, string UpdatedUtc, CampaignItem[] Items, string? ErrorCategory = null,
    string? NextRequestUtc = null, TranslationServiceProfile? Service = null, PersonNameSnapshot? Names = null,
    TitleRules? Rules = null, string? RequestFingerprint = null, string? DisplayLanguage = null,
    bool Retranslate = false, bool PreserveHumanEdits = false, bool IncludeGenres = false, bool IncludeOverviews = false, bool IncludeTitles = true,
    bool IndependentDisplay = false, string? OverviewDisplayLanguage = null);

public sealed record CampaignFieldCounts(string Field, string Language, int Total, int Available,
    int Generated, int Reused, int OriginUnknown, int SavedOnly, int Applied, int Skipped, int NeedsAttention, int Pending);

public static class CampaignStatistics
{
    public static CampaignFieldCounts[] Fields(CampaignRecord campaign) => campaign.Items
        .GroupBy(x => new { x.Field, Language = x.Language ?? campaign.Language })
        .Select(g => new CampaignFieldCounts(g.Key.Field, g.Key.Language, g.Count(),
            g.Count(x => x.CandidateId is not null),
            g.Count(x => x.CandidateId is not null && x.TranslationOrigin == "Generated"),
            g.Count(x => x.CandidateId is not null && x.TranslationOrigin == "Reused"),
            g.Count(x => x.CandidateId is not null && x.TranslationOrigin is not ("Generated" or "Reused")),
            g.Count(x => x.State == "Saved"), g.Count(x => x.State == "Applied"),
            g.Count(x => x.State == "Skipped"), g.Count(x => x.State is "Failed" or "Uncertain" or "Review"),
            g.Count(x => x.State is not ("Applied" or "Saved" or "Skipped" or "Failed" or "Uncertain" or "Review"))))
        .ToArray();
}

// One transaction commits item progress and its campaign header together. Legacy JSON journals
// are imported once and retained for offline rollback; the database is authoritative thereafter.
public sealed class CampaignStore(string root)
{
    private static readonly object Gate = new();
    private sealed record Cached(string Revision, CampaignRecord Value);
    private static readonly Dictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);
    private string DatabasePath => Path.GetFullPath(Path.Combine(root, "campaigns.db"));
    private string DirectoryPath => Path.Combine(root, "campaigns");
    private string CacheKey(Guid id) => DatabasePath + "|" + id.ToString("N");
    private static CampaignRecord Copy(CampaignRecord value) => value with
    {
        Items = value.Items.Select(x => x with {
            OverviewPlan = x.OverviewPlan is null ? null : JsonSerializer.Deserialize<OverviewCampaignPlan>(JsonSerializer.Serialize(x.OverviewPlan)),
            PreviousOperationIds = x.PreviousOperationIds is null ? null : (string[])x.PreviousOperationIds.Clone(),
            GenrePreview = x.GenrePreview is null ? null : x.GenrePreview with {
                ExpectedValues = (string[])x.GenrePreview.ExpectedValues.Clone(),
                ProposedValues = (string[])x.GenrePreview.ProposedValues.Clone(),
                UnknownValues = (string[])x.GenrePreview.UnknownValues.Clone() }
        }).ToArray(),
        Names = value.Names is null ? null : value.Names with
        { Entries = value.Names.Entries.Select(x => x with { Aliases = (string[])x.Aliases.Clone() }).ToArray() },
        Rules = value.Rules is null ? null : value.Rules with { Rules = (TerminologyRule[])value.Rules.Rules.Clone() }
    };
    private void Remember(CampaignRecord value, string revision)
    {
        if (Cache.Count >= 8) Cache.Clear();
        Cache[CacheKey(value.Id)] = new(revision, Copy(value));
    }
    private Microsoft.Data.Sqlite.SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
        try
        {
            db.Open();
            using (var durability = db.CreateCommand())
            { durability.CommandText = "PRAGMA synchronous=FULL;"; durability.ExecuteNonQuery(); }
            using var tx = db.BeginTransaction(deferred: false);
            var version = Convert.ToInt32(Scalar(db, tx, "PRAGMA user_version;"));
            var application = Convert.ToInt32(Scalar(db, tx, "PRAGMA application_id;"));
            if (version == 0 && application == 0 && Convert.ToInt32(Scalar(db, tx,
                "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';")) == 0)
                Execute(db, tx, """
                    CREATE TABLE campaigns(id TEXT PRIMARY KEY, revision TEXT NOT NULL,
                        header_json TEXT NOT NULL, item_count INTEGER NOT NULL CHECK(item_count>=0));
                    CREATE TABLE campaign_items(campaign_id TEXT NOT NULL REFERENCES campaigns(id),
                        ordinal INTEGER NOT NULL CHECK(ordinal>=0), payload_json TEXT NOT NULL,
                        PRIMARY KEY(campaign_id,ordinal));
                    PRAGMA application_id=0x4A4D4C50;
                    PRAGMA user_version=1;
                    """);
            else if (version != 1 || application != 0x4A4D4C50)
                throw new IOException("UnsupportedCampaignDatabase");
            tx.Commit();
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static Microsoft.Data.Sqlite.SqliteCommand Command(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx, string sql, params (string, object)[] parameters)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
    private static object? Scalar(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx, string sql, params (string, object)[] parameters)
    { using var cmd = Command(db, tx, sql, parameters); return cmd.ExecuteScalar(); }
    private static void Execute(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx, string sql, params (string, object)[] parameters)
    { using var cmd = Command(db, tx, sql, parameters); cmd.ExecuteNonQuery(); }
    private static string? Revision(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx, Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Campaign identity required.");
        return Scalar(db, tx, "SELECT revision FROM campaigns WHERE id=$id;", ("$id", id.ToString("N"))) as string;
    }
    private static void Insert(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx, CampaignRecord value)
    {
        Execute(db, tx, "INSERT INTO campaigns VALUES($id,$revision,$header,$count);",
            ("$id", value.Id.ToString("N")), ("$revision", Guid.NewGuid().ToString("N")),
            ("$header", JsonSerializer.Serialize(value with { Items = [] })), ("$count", value.Items.Length));
        using var cmd = Command(db, tx, "INSERT INTO campaign_items VALUES($id,$ordinal,$payload);",
            ("$id", value.Id.ToString("N")), ("$ordinal", 0), ("$payload", ""));
        for (var i = 0; i < value.Items.Length; i++)
        {
            cmd.Parameters["$ordinal"].Value = i;
            cmd.Parameters["$payload"].Value = JsonSerializer.Serialize(value.Items[i]);
            cmd.ExecuteNonQuery();
        }
    }
    private void Import(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx, Guid id)
    {
        if (Revision(db, tx, id) is not null) return;
        var path = Path.Combine(DirectoryPath, id.ToString("N") + ".json");
        if (!File.Exists(path)) return;
        var value = JsonSerializer.Deserialize<CampaignRecord>(File.ReadAllText(path)) ?? throw new IOException("InvalidCampaignJournal");
        if (value.Id != id || value.Items is null) throw new IOException("InvalidCampaignIdentity");
        Insert(db, tx, value);
    }
    private void ImportAll(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        if (!Directory.Exists(DirectoryPath)) return;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) Import(db, tx, id);
    }
    private CampaignRecord Load(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx, Guid id)
    {
        Import(db, tx, id);
        var revision = Revision(db, tx, id) ?? throw new KeyNotFoundException();
        if (Cache.TryGetValue(CacheKey(id), out var cached) && cached.Revision == revision) return Copy(cached.Value);
        CampaignRecord value; int count;
        using (var cmd = Command(db, tx, "SELECT header_json,item_count FROM campaigns WHERE id=$id;", ("$id", id.ToString("N"))))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new KeyNotFoundException();
            value = JsonSerializer.Deserialize<CampaignRecord>(reader.GetString(0)) ?? throw new IOException("InvalidCampaignJournal");
            count = reader.GetInt32(1);
        }
        if (value.Id != id) throw new IOException("InvalidCampaignIdentity");
        var items = new List<CampaignItem>();
        using (var cmd = Command(db, tx, "SELECT ordinal,payload_json FROM campaign_items WHERE campaign_id=$id ORDER BY ordinal;", ("$id", id.ToString("N"))))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                if (reader.GetInt32(0) != items.Count) throw new IOException("InvalidCampaignOrdinal");
                items.Add(JsonSerializer.Deserialize<CampaignItem>(reader.GetString(1)) ?? throw new IOException("InvalidCampaignItem"));
            }
        if (count != items.Count) throw new IOException("InvalidCampaignItemCount");
        value = value with { Items = items.ToArray() };
        Remember(value, revision); return Copy(value);
    }
    public CampaignRecord Read(Guid id)
    {
        lock (Gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
            var value = Load(db, tx, id); tx.Commit(); return value;
        }
    }
    public CampaignRecord[] List()
    {
        lock (Gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
            ImportAll(db, tx);
            var ids = new List<Guid>();
            using (var cmd = Command(db, tx, "SELECT id FROM campaigns;"))
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) ids.Add(Guid.ParseExact(reader.GetString(0), "N"));
            var values = ids.Select(id => Load(db, tx, id)).OrderByDescending(x => x.CreatedUtc, StringComparer.Ordinal).ToArray();
            tx.Commit(); return values;
        }
    }
    public CampaignRecord Create(CampaignRecord value)
    {
        lock (Gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
            ImportAll(db, tx);
            if (Revision(db, tx, value.Id) is not null)
            {
                var previous = Load(db, tx, value.Id); Identity(previous, value); tx.Commit(); return previous;
            }
            using (var cmd = Command(db, tx, "SELECT header_json FROM campaigns;"))
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    if (JsonSerializer.Deserialize<CampaignRecord>(reader.GetString(0))!.State is
                        "Running" or "PauseRequested" or "CancelRequested" or "Paused" or "Interrupted") throw new OperationBusyException();
            Insert(db, tx, value); var revision = Revision(db, tx, value.Id)!;
            tx.Commit(); Remember(value, revision); return Copy(value);
        }
    }
    private static void Identity(CampaignRecord previous, CampaignRecord next)
    {
        if (next.Id != previous.Id || next.Scope != previous.Scope || next.Language != previous.Language || next.Mode != previous.Mode)
            throw new IdempotencyConflictException();
    }
    private static bool Same(CampaignItem a, CampaignItem b) =>
        a with { PreviousOperationIds = null, GenrePreview = null } == b with { PreviousOperationIds = null, GenrePreview = null }
        && JsonSerializer.Serialize(a.GenrePreview) == JsonSerializer.Serialize(b.GenrePreview)
        && (a.PreviousOperationIds is null) == (b.PreviousOperationIds is null)
        && (a.PreviousOperationIds ?? []).SequenceEqual(b.PreviousOperationIds ?? []);
    public CampaignRecord Change(Guid id, Func<CampaignRecord, CampaignRecord> change) => Mutate(id, change, null);
    public CampaignRecord ChangeItem(Guid id, int ordinal, Func<CampaignItem, CampaignItem> change) =>
        Mutate(id, value => { value.Items[ordinal] = change(value.Items[ordinal]); return value; }, ordinal);
    private CampaignRecord Mutate(Guid id, Func<CampaignRecord, CampaignRecord> change, int? ordinal)
    {
        lock (Gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
            var previous = Load(db, tx, id);
            var next = change(Copy(previous)) with { UpdatedUtc = DateTimeOffset.UtcNow.ToString("O") };
            Identity(previous, next);
            if (previous.Service != next.Service || JsonSerializer.Serialize(previous.Names) != JsonSerializer.Serialize(next.Names)
                || JsonSerializer.Serialize(previous.Rules) != JsonSerializer.Serialize(next.Rules)
                || previous.RequestFingerprint != next.RequestFingerprint || previous.DisplayLanguage != next.DisplayLanguage
                || previous.IndependentDisplay != next.IndependentDisplay || previous.OverviewDisplayLanguage != next.OverviewDisplayLanguage
                || previous.Retranslate != next.Retranslate || previous.PreserveHumanEdits != next.PreserveHumanEdits
                || previous.IncludeGenres != next.IncludeGenres || previous.IncludeOverviews != next.IncludeOverviews || previous.IncludeTitles != next.IncludeTitles)
                throw new InvalidOperationException("CampaignConfigurationFrozen");
            if (next.Items.Length != previous.Items.Length) throw new InvalidOperationException("CampaignItemsFrozen");
            for (var i = ordinal ?? 0; i < (ordinal.HasValue ? ordinal.Value + 1 : next.Items.Length); i++)
            {
                if (previous.Items[i].ItemId != next.Items[i].ItemId || previous.Items[i].Language != next.Items[i].Language
                    || previous.Items[i].Field != next.Items[i].Field || previous.Items[i].BaselineHash != next.Items[i].BaselineHash
                    || previous.Items[i].BaselineSelectionRevision != next.Items[i].BaselineSelectionRevision
                    || previous.Items[i].OriginalRevision != next.Items[i].OriginalRevision
                    || JsonSerializer.Serialize(previous.Items[i].OverviewPlan) != JsonSerializer.Serialize(next.Items[i].OverviewPlan))
                    throw new IdempotencyConflictException();
                if (Same(previous.Items[i], next.Items[i])) continue;
                Execute(db, tx, "UPDATE campaign_items SET payload_json=$payload WHERE campaign_id=$id AND ordinal=$ordinal;",
                    ("$payload", JsonSerializer.Serialize(next.Items[i])), ("$id", id.ToString("N")), ("$ordinal", i));
            }
            var revision = Guid.NewGuid().ToString("N");
            Execute(db, tx, "UPDATE campaigns SET revision=$revision,header_json=$header WHERE id=$id;",
                ("$revision", revision), ("$header", JsonSerializer.Serialize(next with { Items = [] })), ("$id", id.ToString("N")));
            tx.Commit(); Remember(next, revision); return Copy(next);
        }
    }
}
