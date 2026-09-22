using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Localizer.Core;

public sealed partial class CandidateStore
{
    private static void CreateTranslationSchema(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx, """
        CREATE TABLE translation_jobs(id TEXT PRIMARY KEY, payload_json TEXT NOT NULL);
        CREATE TABLE translation_items(job_id TEXT NOT NULL REFERENCES translation_jobs(id), ordinal INTEGER NOT NULL,
            payload_json TEXT NOT NULL, PRIMARY KEY(job_id,ordinal));
        PRAGMA user_version=4;
        """);
    internal IDisposable AcquireTranslationLease()
    {
        try { return new FileStream(databasePath + ".translation.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new OperationBusyException(); }
    }
    public TranslationJob CreateTranslationJob(string id, LibraryKey scope, TargetLanguage language,
        IEnumerable<TitleTranslationInput> inputs, TranslationPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id); policy.Validate(); _ = language.Tag();
        var requested = inputs.ToArray();
        if (requested.Length is < 1 or > 200 || requested.Select(x => x.Key).Distinct().Count() != requested.Length)
            throw new ArgumentException("A task requires 1–200 distinct selected items.");
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var items = requested.Select((input, index) =>
        {
            input.Spec.Validate();
            if (input.Key.ServerId != scope.ServerId || input.Key.LibraryId != scope.LibraryId) throw new OperationValidationException("LibraryScopeChanged");
            var source = CurrentSource(db, tx, input.Key) ?? throw new OperationValidationException("SourceMissing");
            return new TranslationItem(id, index, source, input.Spec, TranslationItemState.Pending, 0, null, null, null);
        }).ToArray();
        // Existing fixed-policy task IDs must replay against their original four-field policy fingerprint.
        object fingerprintPolicy = policy.Adaptive ? policy : new { policy.MaxAttempts, policy.IntervalMs, policy.TimeoutMs, policy.RetryDelayMs };
        var fingerprint = Identity.Hash(JsonSerializer.Serialize(new { scope, language, policy = fingerprintPolicy, items }));
        var existing = ReadTranslationJob(db, tx, id);
        if (existing is not null) return existing.Fingerprint == fingerprint ? existing : throw new IdempotencyConflictException();
        var job = new TranslationJob(id, fingerprint, scope, language, policy, TranslationJobState.Ready, null, null, Now(), Now());
        Execute(db, tx, "INSERT INTO translation_jobs VALUES($id,$j);", ("$id", id), ("$j", JsonSerializer.Serialize(job)));
        foreach (var item in items) Execute(db, tx, "INSERT INTO translation_items VALUES($id,$n,$j);",
            ("$id", id), ("$n", item.Ordinal), ("$j", JsonSerializer.Serialize(item)));
        tx.Commit(); return job;
    }
    public TranslationJob GetTranslationJob(string id)
    { using var db = Open(); return ReadTranslationJob(db, null, id) ?? throw new KeyNotFoundException("Translation task missing."); }
    public IReadOnlyList<TranslationJob> ListTranslationJobs(LibraryKey scope, int limit = 50)
    {
        if (limit is < 1 or > 200) throw new ArgumentException("Invalid task list limit.");
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT payload_json FROM translation_jobs
            WHERE json_extract(payload_json,'$.Scope.ServerId')=$s AND json_extract(payload_json,'$.Scope.LibraryId')=$l
            ORDER BY json_extract(payload_json,'$.CreatedUtc') DESC,id DESC LIMIT $limit;
            """, ("$s", scope.ServerId), ("$l", scope.LibraryId), ("$limit", limit));
        using var reader = cmd.ExecuteReader(); var jobs = new List<TranslationJob>();
        while (reader.Read()) jobs.Add(JsonSerializer.Deserialize<TranslationJob>(reader.GetString(0))!);
        return jobs;
    }
    private static TranslationJob? ReadTranslationJob(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        var json = Scalar(db, tx, "SELECT payload_json FROM translation_jobs WHERE id=$id;", ("$id", id)) as string;
        return json is null ? null : JsonSerializer.Deserialize<TranslationJob>(json)!;
    }
    public IReadOnlyList<TranslationItem> GetTranslationItems(string id)
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT payload_json FROM translation_items WHERE job_id=$id ORDER BY ordinal;", ("$id", id));
        using var r = cmd.ExecuteReader(); var items = new List<TranslationItem>();
        while (r.Read()) items.Add(JsonSerializer.Deserialize<TranslationItem>(r.GetString(0))!);
        return items;
    }
    public TranslationJob RequestTranslationCancel(string id)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var job = ReadTranslationJob(db, tx, id) ?? throw new KeyNotFoundException();
        if (job.State is TranslationJobState.Completed or TranslationJobState.CompletedWithErrors) return job;
        job = job with { State = TranslationJobState.CancelRequested, UpdatedUtc = Now() };
        SaveTranslationJob(db, tx, job); tx.Commit(); return job;
    }
    internal TranslationJob SetTranslationJob(string id, TranslationJobState state, string? error = null, string? nextRequestUtc = null)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var current = ReadTranslationJob(db, tx, id)!;
        // A cancellation arriving during bookkeeping must not be overwritten by a Running update.
        if (current.State == TranslationJobState.CancelRequested && state == TranslationJobState.Running) state = current.State;
        var updated = current with { State = state, ErrorCategory = error, NextRequestUtc = nextRequestUtc ?? current.NextRequestUtc, UpdatedUtc = Now() };
        SaveTranslationJob(db, tx, updated); tx.Commit(); return updated;
    }
    private static void SaveTranslationJob(SqliteConnection db, SqliteTransaction tx, TranslationJob job) =>
        Execute(db, tx, "UPDATE translation_jobs SET payload_json=$j WHERE id=$id;", ("$id", job.Id), ("$j", JsonSerializer.Serialize(job)));
    internal void SaveTranslationItem(TranslationItem item)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Execute(db, tx, "UPDATE translation_items SET payload_json=$j WHERE job_id=$id AND ordinal=$n;",
            ("$id", item.JobId), ("$n", item.Ordinal), ("$j", JsonSerializer.Serialize(item)));
        tx.Commit();
    }
    public Candidate? FindTranslationCandidate(SourceSnapshot source, TargetLanguage language, GenerationSpec spec)
    {
        using var db = Open();
        var id = Scalar(db, null, "SELECT id FROM candidates WHERE source_id=$s AND language=$l AND input_fingerprint=$f;",
            ("$s", source.Id), ("$l", language.Tag()), ("$f", Identity.Fingerprint(source, language, spec))) as string;
        return id is null ? null : CandidateById(db, null, id);
    }
}
