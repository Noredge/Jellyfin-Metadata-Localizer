using System.Text.Json;
using Localizer.Unified;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

if (args.Length == 2 && args[0] == "--read") { ReadModelChecks.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--executor") { await ExecutorChecks.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--private22") { await Private22Checks.Run(args[1], (name,ok)=>{Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)throw new InvalidOperationException(name);});return; }
if (args.Length == 2 && args[0] == "--private21") { await Private21Checks.Run(args[1], (name,ok)=>{Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)throw new InvalidOperationException(name);});return; }
if (args.Length == 2 && args[0] == "--private20") { await Private20Checks.Run(args[1], (name,ok)=>{Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)throw new InvalidOperationException(name);});return; }
if (args.Length == 2 && args[0] == "--private19") { await Private19Checks.Run(args[1], (name,ok)=>{Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)throw new InvalidOperationException(name);});return; }
if (args.Length == 2 && args[0] == "--private18") { await Private18Checks.Run(args[1], (name,ok)=>{Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)throw new InvalidOperationException(name);});return; }
if (args.Length == 2 && args[0] == "--private14") { await Private14Checks.Run(args[1], (name,ok)=>{Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)throw new InvalidOperationException(name);});return; }
if (args.Length == 3 && args[0] == "--executor-process") { await ExecutorProcessChecks.Child(args[1], args[2]); return; }
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0]))
    throw new ArgumentException("Use one new absolute test directory.");
var root = args[0]; Directory.CreateDirectory(root);
var checks = new List<object>();
void Check(string name, bool condition)
{ checks.Add(new { name, passed = condition }); Console.WriteLine((condition ? "PASS " : "FAIL ") + name); if (!condition) throw new Exception(name); }
void Reject(string name, Action action)
{
    try { action(); } catch (Exception e) when (e is SqliteException or InvalidOperationException) { Check(name, true); return; }
    Check(name, false);
}
try
{
    File.WriteAllText(Path.Combine(root, "candidates.db"), "Unrelated locked legacy data");
    File.WriteAllText(Path.Combine(root, "global-settings.json"), "Invalid legacy settings sentinel");
    using var locked = new FileStream(Path.Combine(root, "candidates.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var database = new UnifiedDatabase(root, create: true); var content = new ContentStore(database);
    Check("runtime storage initializes with legacy database exclusively locked", File.Exists(database.Path));
    Check("legacy business settings never parsed", File.ReadAllText(Path.Combine(root, "global-settings.json")) == "Invalid legacy settings sentinel");
    var media = content.RegisterMedia(new("server", "library", "item"), "C:\\fixture\\movie.mp4", "fixture");
    Check("media triple identity stable", media == content.RegisterMedia(new("server", "library", "item"), "C:\\fixture\\movie.mp4", "fixture"));
    var a = content.Observe(media, "Name", "原文 A", "{}", "LAB-1 · ");
    var again = content.Observe(media, "Name", "原文 A", "{}", "LAB-1 · ");
    Check("duplicate observation preserves source revision", a.SourceId == again.SourceId && a.SourceRevision == again.SourceRevision);
    var b = content.Observe(media, "Name", "原文 B", "{}");
    var back = content.Observe(media, "Name", "原文 A", "{}", "LAB-1 · ");
    Check("A B A reuses immutable source and advances authority", a.SourceId == back.SourceId && a.SourceRevision < b.SourceRevision && b.SourceRevision < back.SourceRevision);
    Reject("old A control authority never revives", () => content.SaveManual(a, "zh-Hans", "旧人工稿", false, "old", "stale"));
    var manual = content.SaveManual(back, "zh-Hans", "第一版人工稿", true, "original", "manual-1");
    Check("manual show selects version", content.Selected(back.SourceId, "zh-Hans")?.Id == manual.Id);
    var controlled = content.ReadField(media, "Name")!;
    var draft = content.SaveManual(controlled, "zh-Hans", "只保存的草稿", false, "display", "manual-2");
    Check("save only preserves selected pointer", content.Selected(back.SourceId, "zh-Hans")?.Id == manual.Id);
    Check("save only advances manual control revision", content.ReadField(media, "Name")!.ControlRevision == controlled.ControlRevision + 1);
    var replay = content.SaveManual(controlled, "zh-Hans", "只保存的草稿", false, "display", "manual-2");
    Check("manual request replay creates no new version", replay.Id == draft.Id && content.History(back.SourceId, "zh-Hans", 0, 100).Length == 2);
    Reject("manual idempotency key cannot describe another command", () => content.SaveManual(controlled, "zh-Hans", "different", false, "display", "manual-2"));
    Reject("save-only replay cannot silently become save-and-show", () => content.SaveManual(controlled, "zh-Hans", "只保存的草稿", true, "display", "manual-2"));
    Check("history pagination returns requested slice", content.History(back.SourceId, "zh-Hans", 1, 1).Length == 1);
    var prefixed = content.Observe(media, "Name", "原文 A", "{}", "LAB-2 · ");
    Check("prefix edit reuses text source and selected translation", prefixed.SourceId == back.SourceId && prefixed.RenderRevision > back.RenderRevision && content.Selected(prefixed.SourceId, "zh-Hans")?.Id == manual.Id);
    var genres = content.Observe(media, "Genres", "[\"旅行\",\"自然\",\"旅行\"]", "{}");
    var reordered = content.Observe(media, "Genres", "[\"自然\",\"旅行\"]", "{}");
    Check("genre order and duplicates preserve content fingerprint", genres.SourceId == reordered.SourceId);
    Check("genre rendering keeps current order", JsonSerializer.Deserialize<string[]>(reordered.RenderJson)!.SequenceEqual(new[] { "自然", "旅行" }));
    var empty = content.Observe(media, "Overview", " \r\n", "{}");
    Check("empty overview is a normal empty field", empty.Availability == "empty" && empty.Content == "");
    using (var db = database.Open())
    {
        long Count(string sql) => Scalar<long>(db, null, sql);
        Check("shared genre terms deduplicated", Count("SELECT COUNT(*) FROM genre_terms") == 2);
        Reject("immutable source cannot be overwritten", () => Execute(db, null, "UPDATE source_versions SET content='bad' WHERE id=$id", ("$id", a.SourceId)));
        Reject("manual history cannot be overwritten", () => Execute(db, null, "UPDATE translation_versions SET text='bad' WHERE id=$id", ("$id", manual.Id)));
        Reject("source revision cannot regress", () => Execute(db, null, "UPDATE media_fields SET source_revision=0 WHERE media_id=$m AND field='Name'", ("$m", media)));
        Reject("field cannot bind another field source", () => Execute(db, null, "UPDATE media_fields SET source_id=$s,source_revision=source_revision+1 WHERE media_id=$m AND field='Name'", ("$m", media), ("$s", empty.SourceId)));
        Reject("selection cannot cross language", () => Execute(db, null, "INSERT INTO translation_selections VALUES($s,'en',$t,1)", ("$s", a.SourceId), ("$t", manual.Id)));
        Reject("media deletion cannot cascade valuable history", () => Execute(db, null, "DELETE FROM media_items WHERE id=$m", ("$m", media)));
        Execute(db, null, "INSERT INTO connection_versions(id,revision,endpoint,model,timeout_ms,requests_per_minute,created_utc) VALUES('c',1,'https://example.invalid/v1','fixture',1000,60,$n)", ("$n", Now()));
        Execute(db, null, "INSERT INTO generation_inputs(id,source_id,language,prompt_version,fingerprint,created_utc) VALUES('input',$s,'zh-Hans','v1','fingerprint',$n)", ("$s", a.SourceId), ("$n", Now()));
        void Work(string id, string generation) => Execute(db, null, """
            INSERT INTO work_items(id,submission_key,target_kind,media_id,field,source_id,source_revision,control_revision,language,target_epoch,input_id,generation_key,state,step,created_utc,updated_utc)
            VALUES($id,$id,'media',$m,'Name',$s,1,0,'zh-Hans',0,'input',$g,'queued','source',$n,$n)
            """, ("$id", id), ("$m", media), ("$s", a.SourceId), ("$g", generation), ("$n", Now()));
        Work("w1", "reuse"); Reject("active same-target work deduplicates", () => Work("w2", "reuse")); Work("w2", "explicit-new-generation");
        void Attempt(string id, string work, long number) => Execute(db, null, "INSERT INTO request_attempts(id,work_id,sequence,input_id,connection_id,state,started_utc) VALUES($id,$w,$n,'input','c','prepared',$utc)",
            ("$id", id), ("$w", work), ("$n", number), ("$utc", Now()));
        Attempt("r1", "w1", 1);
        Reject("same input cannot send concurrently from another work", () => Attempt("r2", "w2", 1));
        Execute(db, null, "UPDATE request_attempts SET state='sending' WHERE id='r1'");
        Reject("sending attempt cannot reset to prepared", () => Execute(db, null, "UPDATE request_attempts SET state='prepared' WHERE id='r1'"));
        Reject("generated version cannot claim another input language", () => Execute(db, null,
            "INSERT INTO translation_versions(id,source_id,language,text,text_hash,origin,input_id,created_utc) VALUES('wrong',$s,'en','text','hash','model','input',$n)", ("$s", a.SourceId), ("$n", Now())));
        Execute(db, null, "UPDATE request_attempts SET state='unknown',error_code='network_uncertain',elapsed_ms=123 WHERE id='r1'");
        Reject("unknown result blocks automatic duplicate input", () => Attempt("r2", "w2", 1));
        Reject("unknown diagnostics cannot be rewritten", () => Execute(db, null, "UPDATE request_attempts SET error_code='different' WHERE id='r1'"));
        Execute(db, null, "UPDATE request_attempts SET uncertainty_acknowledged=1 WHERE id='r1'");
        Attempt("r2", "w2", 1); Execute(db, null, "UPDATE request_attempts SET state='sending' WHERE id='r2'");
        Execute(db, null, "UPDATE request_attempts SET state='failed',error_code='http_failure',elapsed_ms=456 WHERE id='r2'");
        Reject("terminal failure history cannot be overwritten", () => Execute(db, null, "UPDATE request_attempts SET elapsed_ms=0 WHERE id='r2'"));
        Attempt("r3", "w2", 2); Execute(db, null, "UPDATE request_attempts SET state='not_sent' WHERE id='r3'");
        Check("retry adds a row and preserves original error and duration", Count("SELECT COUNT(*) FROM request_attempts") == 3 && Count("SELECT elapsed_ms FROM request_attempts WHERE id='r2'") == 456);
        Reject("attempt sequence unique within work", () => Attempt("r4", "w2", 2));
        Reject("request history cannot be deleted", () => Execute(db, null, "DELETE FROM request_attempts WHERE id='r2'"));
        Check("all foreign key references valid", Count("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0);
        Check("SQLite integrity valid", Scalar<string>(db, null, "PRAGMA integrity_check") == "ok");
    }
    var preference = content.ReadField(media, "Name")!; content.KeepOriginal(preference, "host current");
    using (var db = database.Open()) Check("keep-original is distinct from merely displaying original", Scalar<long>(db, null, "SELECT keep_original FROM display_intents WHERE field='Name'") == 1);
    preference = content.ReadField(media, "Name")!; content.RestoreOriginal(preference, "host current");
    using (var db = database.Open()) Check("restoring original preserves an existing keep-original choice", Scalar<long>(db, null, "SELECT keep_original FROM display_intents WHERE field='Name'") == 1);
    preference = content.ReadField(media, "Name")!; content.FollowGlobal(preference);
    using (var db = database.Open())
    {
        Check("explicit follow-global clears the persistent original choice", Scalar<long>(db, null, "SELECT keep_original FROM display_intents WHERE field='Name'") == 0);
        Execute(db, null, "UPDATE runtime_state SET value='unsupported-definition' WHERE key='schema_definition'");
    }
    Reject("same schema version with a different definition fails closed", () => _ = new UnifiedDatabase(root));
    using (var db = database.Open()) Execute(db, null, "UPDATE runtime_state SET value=$h WHERE key='schema_definition'", ("$h", SchemaHash));
    content.MarkRemoved(media);
    Check("removal retains translation history", content.History(a.SourceId, "zh-Hans", 0, 100).Length == 2);
    using (var db = database.Open())
        Check("removed media stops queued work", Scalar<long>(db, null, "SELECT COUNT(*) FROM work_items WHERE state='expired'") == 2);
    Check("restart uses same schema and data", new ContentStore(new UnifiedDatabase(root)).Selected(a.SourceId, "zh-Hans")?.Id == manual.Id);
    SettingsChecks.Run(Path.Combine(root, "settings"));
    var report = new { status = "passed", checks, productionWrites = 0, networkRequests = 0 };
    File.WriteAllText(Path.Combine(root, "checks.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Passed {checks.Count} checks.");
}
catch (Exception error)
{
    File.WriteAllText(Path.Combine(root, "checks.json"), JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
    throw;
}
