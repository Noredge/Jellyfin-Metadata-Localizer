using System.Diagnostics;
using System.Text.Json;
using Localizer.Core;
using Localizer.Plugin;
using Microsoft.Data.Sqlite;

if (args.Length != 3) throw new ArgumentException("Expected scratch, report, repository root.");
var root = Path.GetFullPath(args[0]);
var repo = Path.GetFullPath(args[2]);
if (!root.StartsWith(Path.Combine(repo, "work", "m9-campaign-store") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
    || Directory.Exists(root)) throw new ArgumentException("Fresh owned synthetic directory required.");
Directory.CreateDirectory(root);
var checks = new List<object>(); var failures = 0;
var scope = new LibraryKey("synthetic-server", "synthetic-library");
string Json<T>(T value) => JsonSerializer.Serialize(value);
void Assert(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
void ThrowsBounds(Action action)
{
    try { action(); }
    catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException) { return; }
    throw new InvalidOperationException("Expected bounds rejection");
}
void Check(string name, Action action)
{
    try { action(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; checks.Add(new { name, passed = false, error = error.GetType().Name + ": " + error.Message }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
string Fresh() { var path = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
CampaignRecord Record(int count = 3, string state = "Completed")
{
    var id = Guid.NewGuid();
    var items = Enumerable.Range(0, count).Select(index =>
    {
        var itemId = Guid.NewGuid();
        var key = new MediaKey(scope.ServerId, scope.LibraryId, itemId.ToString("N"));
        var source = new SourceSnapshot(index + 1, key, 2, "合成原题", "SYN · ", "synthetic-hash");
        var spec = new GenerationSpec("https://synthetic.invalid", "offline", "p1", "r1", "{}", "{}", "Synthetic prompt");
        return new CampaignItem(itemId, "SYN · 合成原题", new string('a', 64), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            "Translated", "synthetic-category", source, spec, Guid.NewGuid().ToString("N"), "automatic",
            new(key, TargetLanguage.SimplifiedChinese, PreviewStatus.ReadyForReview, "SYN · 合成原题", "SYN · 合成译文", index + 1, "candidate", 2, 1),
            ["first-previous-operation", "second-previous-operation"]);
    }).ToArray();
    return new(id, scope, "zh-Hans", "auto_apply", state, "2026-09-06T00:00:00+00:00", "2026-09-06T01:00:00+00:00",
        items, "preserved-error", "2026-09-06T02:00:00+00:00");
}
string Legacy(string path, CampaignRecord record, Guid? fileId = null)
{
    var directory = Path.Combine(path, "campaigns"); Directory.CreateDirectory(directory);
    var file = Path.Combine(directory, (fileId ?? record.Id).ToString("N") + ".json");
    File.WriteAllText(file, Json(record)); return file;
}
SqliteConnection Database(string path)
{
    var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "campaigns.db"),
        Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true, Pooling = false }.ToString());
    db.Open(); return db;
}
string Revision(string path, Guid id)
{
    using var db = Database(path); using var command = db.CreateCommand();
    command.CommandText = "SELECT revision FROM campaigns WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString("N"));
    return (string)command.ExecuteScalar()!;
}
void Sql(string path, string sql)
{
    using var db = Database(path); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
}

Check("legacy_import_preserves_all_fields_and_original_json", () =>
{
    var path = Fresh(); var record = Record(); var file = Legacy(path, record); var original = File.ReadAllBytes(file);
    var store = new CampaignStore(path); var imported = store.Read(record.Id);
    Assert(Json(imported) == Json(record) && File.ReadAllBytes(file).SequenceEqual(original));
    Assert(File.Exists(Path.Combine(path, "campaigns.db")));
    var version = Revision(path, record.Id);
    Assert(Json(new CampaignStore(path).Read(record.Id)) == Json(record) && Revision(path, record.Id) == version);
});
Check("import_is_once_and_database_remains_authoritative", () =>
{
    var path = Fresh(); var record = Record(); var file = Legacy(path, record); var store = new CampaignStore(path);
    store.Read(record.Id); var version = Revision(path, record.Id);
    File.WriteAllText(file, Json(record with { State = "Paused", ErrorCategory = "changed-legacy-copy" }));
    Assert(Json(store.Read(record.Id)) == Json(record) && Revision(path, record.Id) == version);
    Assert(store.List().Length == 1 && Revision(path, record.Id) == version);
    using var db = Database(path); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM campaign_items";
    Assert((long)command.ExecuteScalar()! == record.Items.Length);
});
Check("list_imports_all_legacy_records_and_create_observes_legacy_busy_task", () =>
{
    var path = Fresh(); var first = Record(); var second = Record(state: "Paused"); Legacy(path, first); Legacy(path, second);
    var store = new CampaignStore(path); Assert(store.List().Length == 2);
    Throws<OperationBusyException>(() => store.Create(Record()));
    Assert(store.List().Length == 2);
});
Check("legacy_identity_mismatch_is_rejected_without_partial_import", () =>
{
    var path = Fresh(); var record = Record(); var wrongId = Guid.NewGuid(); Legacy(path, record, wrongId);
    var store = new CampaignStore(path); Throws<IOException>(() => store.Read(wrongId));
    if (File.Exists(Path.Combine(path, "campaigns.db")))
    {
        using var db = Database(path); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM campaigns";
        Assert((long)command.ExecuteScalar()! == 0);
    }
});
Check("create_idempotency_and_immutable_campaign_identity", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(); store.Create(record);
    Assert(Json(store.Create(record with { State = "Running", Items = [] })) == Json(record));
    Throws<IdempotencyConflictException>(() => store.Create(record with { Language = "en" }));
    Throws<IdempotencyConflictException>(() => store.Change(record.Id, current => current with { Id = Guid.NewGuid() }));
    Throws<IdempotencyConflictException>(() => store.Change(record.Id, current => current with { Scope = new("other", "library") }));
    Throws<IdempotencyConflictException>(() => store.Change(record.Id, current => current with { Language = "en" }));
    Throws<IdempotencyConflictException>(() => store.Change(record.Id, current => current with { Mode = "review" }));
    Assert(Json(store.Read(record.Id)) == Json(record));
});
Check("empty_identity_and_out_of_range_update_leave_store_unchanged", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(); store.Create(record);
    Throws<ArgumentException>(() => store.Create(Record() with { Id = Guid.Empty }));
    ThrowsBounds(() => store.ChangeItem(record.Id, -1, item => item));
    ThrowsBounds(() => store.ChangeItem(record.Id, record.Items.Length, item => item));
    Assert(Json(store.Read(record.Id)) == Json(record));
});
Check("callback_exception_cannot_mutate_cached_arrays_or_database", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(); store.Create(record);
    var original = Json(store.Read(record.Id)); var version = Revision(path, record.Id);
    Throws<ArithmeticException>(() => store.Change(record.Id, current =>
    {
        current.Items[0] = current.Items[0] with { State = "Applied" };
        current.Items[1].PreviousOperationIds![0] = "changed";
        throw new ArithmeticException("synthetic callback failure");
    }));
    Throws<ArithmeticException>(() => store.ChangeItem(record.Id, 0, item =>
    {
        item.PreviousOperationIds![0] = "changed";
        throw new ArithmeticException("synthetic item callback failure");
    }));
    Assert(Json(store.Read(record.Id)) == original && Revision(path, record.Id) == version);
});
Check("returned_and_input_arrays_cannot_pollute_internal_snapshot", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(); var expected = Json(record);
    var created = store.Create(record);
    record.Items[0] = record.Items[0] with { State = "untrusted input mutation" };
    created.Items[1].PreviousOperationIds![0] = "untrusted output mutation";
    var read = store.Read(created.Id); read.Items[0] = read.Items[0] with { State = "untrusted read mutation" };
    var listed = store.List(); listed[0].Items[2].PreviousOperationIds![1] = "untrusted list mutation";
    Assert(Json(store.Read(created.Id)) == expected);
});
Check("parallel_pause_and_per_item_progress_do_not_lose_each_other", () =>
{
    var path = Fresh(); var first = new CampaignStore(path); var second = new CampaignStore(path); var record = Record(30, "Running"); first.Create(record);
    using var start = new ManualResetEventSlim(false);
    var pause = Task.Run(() => { start.Wait(); for (var i = 0; i < 15; i++) first.Change(record.Id, current => current with { State = "Paused" }); });
    var progress = Task.Run(() => { start.Wait(); for (var i = 0; i < 30; i++) second.ChangeItem(record.Id, i, item => item with { State = "Applied" }); });
    start.Set(); Task.WaitAll(pause, progress);
    var final = first.Read(record.Id);
    Assert(final.State == "Paused" && final.Items.All(x => x.State == "Applied"));
});
Check("reopen_and_external_revision_change_invalidate_cached_snapshot", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(); store.Create(record);
    var second = new CampaignStore(path); second.ChangeItem(record.Id, 0, item => item with { State = "Applied" });
    Assert(store.Read(record.Id).Items[0].State == "Applied");
    var expected = second.Read(record.Id) with { State = "ExternallyUpdated", Items = [] };
    using (var db = Database(path))
    using (var command = db.CreateCommand())
    {
        command.CommandText = "UPDATE campaigns SET revision=$revision,header_json=$header WHERE id=$id";
        command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$header", Json(expected));
        command.Parameters.AddWithValue("$id", record.Id.ToString("N")); command.ExecuteNonQuery();
    }
    Assert(store.Read(record.Id).State == "ExternallyUpdated" && store.Read(record.Id).Items[0].State == "Applied");
});
Check("sqlite_header_failure_rolls_back_prior_item_update_and_revision", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(); store.Create(record);
    var original = Json(store.Read(record.Id)); var version = Revision(path, record.Id);
    Sql(path, "CREATE TRIGGER fail_header BEFORE UPDATE ON campaigns BEGIN SELECT RAISE(ABORT,'synthetic rollback'); END;");
    try { Throws<SqliteException>(() => store.ChangeItem(record.Id, 0, item => item with { State = "Applied" })); }
    finally { Sql(path, "DROP TRIGGER fail_header;"); }
    Assert(Json(store.Read(record.Id)) == original && Revision(path, record.Id) == version);
    Assert(Json(new CampaignStore(path).Read(record.Id)) == original);
    store.ChangeItem(record.Id, 0, item => item with { State = "Applied" });
    Assert(store.Read(record.Id).Items[0].State == "Applied" && Revision(path, record.Id) != version);
});
Check("legacy_import_sql_failure_rolls_back_header_and_all_inserted_items", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(); var file = Legacy(path, record);
    var original = File.ReadAllBytes(file);
    Throws<KeyNotFoundException>(() => store.Read(Guid.NewGuid()));
    Sql(path, "CREATE TRIGGER fail_import BEFORE INSERT ON campaign_items WHEN NEW.ordinal=1 BEGIN SELECT RAISE(ABORT,'synthetic import rollback'); END;");
    try { Throws<SqliteException>(() => store.Read(record.Id)); }
    finally { Sql(path, "DROP TRIGGER fail_import;"); }
    using (var db = Database(path))
    using (var command = db.CreateCommand())
    {
        command.CommandText = "SELECT (SELECT COUNT(*) FROM campaigns)+(SELECT COUNT(*) FROM campaign_items)";
        Assert((long)command.ExecuteScalar()! == 0);
    }
    Assert(File.ReadAllBytes(file).SequenceEqual(original) && Json(store.Read(record.Id)) == Json(record));
});
Check("null_and_empty_previous_operations_are_distinct_and_update_return_is_isolated", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(); store.Create(record);
    store.ChangeItem(record.Id, 0, item => item with { PreviousOperationIds = null });
    var empty = store.ChangeItem(record.Id, 0, item => item with { PreviousOperationIds = [] });
    Assert(empty.Items[0].PreviousOperationIds is { Length: 0 });
    empty.Items[0] = empty.Items[0] with { State = "untrusted output" };
    Assert(store.Read(record.Id).Items[0].State == "Translated");
    using var db = Database(path); using var command = db.CreateCommand();
    command.CommandText = "SELECT payload_json FROM campaign_items WHERE ordinal=0";
    Assert(JsonSerializer.Deserialize<CampaignItem>((string)command.ExecuteScalar()!)!.PreviousOperationIds is { Length: 0 });
});
Check("header_change_failure_is_atomic_and_item_identity_is_immutable", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(); store.Create(record);
    var original = Json(store.Read(record.Id));
    Throws<IdempotencyConflictException>(() => store.ChangeItem(record.Id, 0, item => item with { ItemId = Guid.NewGuid() }));
    Sql(path, "CREATE TRIGGER fail_header BEFORE UPDATE ON campaigns BEGIN SELECT RAISE(ABORT,'synthetic rollback'); END;");
    try { Throws<SqliteException>(() => store.Change(record.Id, current => current with { State = "Paused" })); }
    finally { Sql(path, "DROP TRIGGER fail_header;"); }
    Assert(Json(store.Read(record.Id)) == original);
});
Check("sqlite_header_is_small_and_database_integrity_remains_valid", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(100); store.Create(record);
    using var db = Database(path); using var command = db.CreateCommand();
    command.CommandText = "SELECT header_json FROM campaigns";
    Assert(JsonSerializer.Deserialize<CampaignRecord>((string)command.ExecuteScalar()!)!.Items.Length == 0);
    command.CommandText = "SELECT COUNT(*) FROM campaign_items"; Assert((long)command.ExecuteScalar()! == 100);
    command.CommandText = "PRAGMA integrity_check"; Assert((string?)command.ExecuteScalar() == "ok");
    command.CommandText = "PRAGMA foreign_key_check"; using var reader = command.ExecuteReader(); Assert(!reader.Read());
});
Check("service_names_and_rules_are_frozen_and_caller_mutation_is_isolated", () =>
{
    var path = Fresh(); var store = new CampaignStore(path);
    var record = Record() with
    {
        Service = (Localizer.Translation.TranslationServiceProfile.GroqDefault() with { Model = "synthetic-model" }),
        Names = new(1, [new(Guid.NewGuid().ToString("N"), "Original", ["Alias"])]),
        Rules = new("v1", "Synthetic rules", [])
    };
    store.Create(record);
    record.Names.Entries[0].Aliases[0] = "caller mutation";
    Assert(store.Read(record.Id).Names!.Entries[0].Aliases[0] == "Alias");
    Throws<InvalidOperationException>(() => store.Change(record.Id, value => value with { Service = value.Service! with { Model = "other-model" } }));
    Throws<InvalidOperationException>(() => store.Change(record.Id, value => value with { Names = value.Names! with { Revision = 2 } }));
    Throws<InvalidOperationException>(() => store.Change(record.Id, value => value with { Rules = value.Rules! with { Instructions = "changed" } }));
    Assert(store.ChangeItem(record.Id, 0, item => item with { State = "Translated" }).Rules!.Instructions == "Synthetic rules");
});
Check("m12_bilingual_options_and_item_language_are_frozen", () =>
{
    var store = new CampaignStore(Fresh()); var record = Record() with { Mode = "library_translate", Language = "multi",
        RequestFingerprint = "frozen", DisplayLanguage = "en", Retranslate = true, IncludeGenres = true };
    record.Items[0] = record.Items[0] with { Language = "en", BaselineHash = "baseline", BaselineSelectionRevision = 3 };
    store.Create(record);
    Throws<InvalidOperationException>(() => store.Change(record.Id, x => x with { PreserveHumanEdits = true }));
    Throws<InvalidOperationException>(() => store.Change(record.Id, x => x with { DisplayLanguage = "zh-Hans" }));
    Throws<InvalidOperationException>(() => store.Change(record.Id, x => x with { RequestFingerprint = "different" }));
    Throws<IdempotencyConflictException>(() => store.ChangeItem(record.Id, 0, x => x with { Language = "zh-Hans" }));
    Throws<IdempotencyConflictException>(() => store.ChangeItem(record.Id, 0, x => x with { Field = "Genres" }));
    Assert(store.ChangeItem(record.Id, 0, x => x with { State = "Saved" }).Items[0].BaselineSelectionRevision == 3);
});
Check("m12_genre_frozen_preview_arrays_are_isolated", () =>
{
    var store = new CampaignStore(Fresh()); var record = Record(); var id = record.Items[0].ItemId;
    record.Items[0] = record.Items[0] with { Field = "Genres", Language = "en",
        GenrePreview = new(new(scope.ServerId, scope.LibraryId, id.ToString("N")), TargetLanguage.English,
            PreviewStatus.ReadyForReview, 1, "mapping", ["original"], ["translated"], []) };
    store.Create(record);
    record.Items[0].GenrePreview!.ExpectedValues[0] = "caller mutation";
    var read = store.Read(record.Id); read.Items[0].GenrePreview!.ProposedValues[0] = "reader mutation";
    var actual = store.Change(record.Id, x => x with { ErrorCategory = "test" }).Items[0].GenrePreview!;
    Assert(actual.ExpectedValues[0] == "original" && actual.ProposedValues[0] == "translated");
});
Check("translation_origin_survives_apply_failure_and_retry", () =>
{
    var path = Fresh(); var store = new CampaignStore(path); var record = Record(); store.Create(record);
    Assert(store.Read(record.Id).Items[0].TranslationOrigin is null);
    store.ChangeItem(record.Id, 0, x => x with { CandidateId = "synthetic-candidate", TranslationOrigin = "Generated", State = "Translated" });
    store.ChangeItem(record.Id, 0, x => x with { State = "Failed", ErrorCategory = "synthetic-write-failure" });
    Assert(new CampaignStore(path).Read(record.Id).Items[0].TranslationOrigin == "Generated");
    store.ChangeItem(record.Id, 0, x => x with { State = "Applied", ErrorCategory = null });
    Assert(store.Read(record.Id).Items[0].TranslationOrigin == "Generated");
    var oldJson = Json(record).Replace(",\"TranslationOrigin\":null", "");
    Assert(JsonSerializer.Deserialize<CampaignRecord>(oldJson)!.Items[0].TranslationOrigin is null);
});
Check("mixed_field_language_statistics_do_not_count_apply_failures_as_generation_failures", () =>
{
    var record = Record(); var seed = record.Items[0];
    var rows = new[] {
        seed with { Field = "Name", Language = "en", CandidateId = "a", TranslationOrigin = "Generated", State = "Failed" },
        seed with { Field = "Name", Language = "en", CandidateId = "b", TranslationOrigin = "Reused", State = "Applied" },
        seed with { Field = "Name", Language = "en", CandidateId = "c", TranslationOrigin = null, State = "Saved" },
        seed with { Field = "Name", Language = "en", CandidateId = null, TranslationOrigin = null, State = "Failed" },
        seed with { Field = "Overview", Language = "en", CandidateId = "d", TranslationOrigin = "Generated", State = "Saved" },
        seed with { Field = "Overview", Language = "zh-Hans", CandidateId = "e", TranslationOrigin = "Reused", State = "Applied" },
        seed with { Field = "Overview", Language = "zh-Hans", CandidateId = null, State = "Skipped" },
        seed with { Field = "Overview", Language = "zh-Hans", CandidateId = null, State = "Pending" }
    };
    var counts = CampaignStatistics.Fields(record with { Items = rows });
    Assert(counts.Length == 3);
    Assert(counts.Single(x => x.Field == "Name") == new CampaignFieldCounts("Name", "en", 4, 3, 1, 1, 1, 1, 1, 0, 2, 0));
    Assert(counts.Single(x => x.Field == "Overview" && x.Language == "en") == new CampaignFieldCounts("Overview", "en", 1, 1, 1, 0, 0, 1, 0, 0, 0, 0));
    Assert(counts.Single(x => x.Language == "zh-Hans") == new CampaignFieldCounts("Overview", "zh-Hans", 3, 1, 0, 1, 0, 0, 1, 1, 0, 1));
    Assert(counts.All(x => x.Available == x.Generated + x.Reused + x.OriginUnknown));
    Assert(counts.All(x => x.Total == x.SavedOnly + x.Applied + x.Skipped + x.NeedsAttention + x.Pending));
    Assert(CampaignStatistics.Fields(record with { Items = [] }).Length == 0);
});
var output = new { stage = "M36 campaign storage and translation origin compatibility", completed = true,
    total = checks.Count, passed = checks.Count - failures, failed = failures, productionWrites = 0, cloudRequests = 0,
    jellyfinStarted = false, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, checks };
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;
