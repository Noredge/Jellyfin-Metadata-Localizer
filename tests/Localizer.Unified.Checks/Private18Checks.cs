using System.Text.Json;
using Localizer.Core;
using Localizer.Unified;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

internal static class Private18Checks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        using (var f = new ExecutorFixture(Path.Combine(root, "p18-media-retry")))
        {
            var media = f.Add("retry"); var old = f.Submit([media], ["Overview"]).WorkIds.Single();
            Fail(f, old); var replacement = f.Submit([media], ["Overview"]).WorkIds.Single();
            var version = f.Database.WorkAvailable.Version;
            check("old media retry reuses the replacement without uniqueness failure", f.Work.Retry(old) == replacement && f.Work.Find(old)!.State == "failed");
            check("repeated retry is read only and cannot create wake tokens", f.Work.Retry(old) == replacement && version == f.Database.WorkAvailable.Version && f.Count("SELECT COUNT(*) FROM work_items") == 2);
            await f.Drain();
            check("replacement retry sends exactly one synthetic request", f.Transport.Calls == 1 && f.Work.Find(replacement)!.State == "completed");
            var read = new ReadModel(f.Database);
            check("old history retains unmeasured save timing", Convert.ToInt64(read.Work(null, 0, 30, old).Single()["save_timing_measured"]) == 0);
            check("new response save includes committed transaction time", f.Count("SELECT save_ms FROM work_items WHERE id='" + replacement + "'") > 0 && Convert.ToInt64(read.Work(null, 0, 30, replacement).Single()["save_timing_measured"]) == 1);
            var attempts = f.Count("SELECT COUNT(*) FROM request_attempts");
            var field = f.Content.ReadField(media, "Overview")!;
            f.Content.Choose(field, "zh-Hans", f.Content.Selected(field.SourceId, "zh-Hans")!.Id, f.Host.Get(f.Key(media), "Overview").Display);
            var cached = f.Submit([media], ["Overview"], operation: "display"); await f.Drain();
            check("cached display never sends a second request", f.Count("SELECT COUNT(*) FROM request_attempts") == attempts && f.Work.Find(cached.WorkIds.Single())!.State == "completed");
            bool expired = false; try { f.Work.Retry(old); } catch (InvalidOperationException e) when (e.Message == "RetryIntentExpired") { expired = true; }
            check("obsolete history retry remains explicitly expired", expired);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p18-term-retry")))
        {
            string Submit() => f.Work.SubmitTerms(Id(), "dictionary", f.Settings.Read().Revision, ["isolated-term"]).WorkIds.Single();
            var old = Submit(); Fail(f, old); var replacement = Submit();
            check("old term retry also reuses the existing active target", f.Work.Retry(old) == replacement && f.Work.Find(old)!.State == "failed");
            await f.Drain(); check("shared term replacement sends once", f.Transport.Calls == 1 && f.Work.Find(replacement)!.State == "completed");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p18-parent-retry")))
        {
            var media = f.Add("parent", "overview", "retry-shared");
            string SubmitTerm() => f.Work.SubmitTerms(Id(), "dictionary", f.Settings.Read().Revision, ["retry-shared"]).WorkIds.Single();
            var oldChild = SubmitTerm(); Fail(f, oldChild);
            var parent = f.Submit([media], ["Genres"]).WorkIds.Single(); await f.Executor.RunNextAsync();
            var newChild = SubmitTerm(); f.Work.Retry(parent);
            check("parent retry reconnects a reused active child without a stale failed dependency", f.Work.Dependencies(parent).Single().Id == newChild && f.Work.Find(oldChild)!.State == "failed");
            await f.Drain(); check("reconnected parent completes with one shared request", f.Work.Find(parent)!.State == "completed" && f.Transport.Calls == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p18-execution-diagnostic")))
        {
            var media = f.Add("diagnostic"); var id = f.Submit([media], ["Overview"]).WorkIds.Single();
            f.Host.BeforeReadReturn = _ => throw new SqliteException("secret-Key request contents", 5, 517);
            try { await f.Executor.RunNextAsync(); } catch (SqliteException) { }
            var detail = f.Executor.LastFailure;
            check("real execution failure captures the exact stage work and storage codes", detail is { Stage: "source_read", ErrorCode: 5, SqliteExtendedCode: 517 } && detail.WorkId == id && !JsonSerializer.Serialize(detail).Contains("secret", StringComparison.Ordinal));
            f.Host.BeforeReadReturn = null; f.Clock = f.Clock.AddSeconds(2); await f.Drain();
            check("diagnosed interruption recovers without an extra generation", f.Work.Find(id)!.State == "completed" && f.Transport.Calls == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p18-independent-issues")))
        {
            var receipt = f.Work.SubmitTerms(Id(), "connection_test", f.Settings.Read().Revision, ["接続確認"]); Fail(f, receipt.WorkIds.Single());
            var read = new ReadModel(f.Database); var page = read.Issues("translation", "synthetic_failure", 0, 30);
            check("independent connection failure has an actionable term task", page.Total == 1 && (string)page.Items.Single()["target_kind"]! == "term" && page.Items.Single()["media_id"] is null && (string)page.Items.Single()["work_id"]! == receipt.WorkIds.Single());
            CheckSummary(read, "standalone term summary and details agree", check);
            check("direct work filter reaches only the requested term", read.Work(null, 0, 30, receipt.WorkIds.Single()).Length == 1 && read.Work(null, 0, 30, "missing").Length == 0);
            f.Work.Retry(receipt.WorkIds.Single()); await f.Drain();
            check("successful term retry clears the current issue without deleting history", read.Issues("translation", "synthetic_failure", 0, 30).Total == 0 && f.Count("SELECT COUNT(*) FROM work_items") == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p18-linked-issues")))
        {
            var a = f.Add("a", "overview", "shared-failed"); var b = f.Add("b", "overview", "shared-failed");
            var batch = f.Submit([a, b], ["Genres"]); await f.Executor.RunNextAsync(); await f.Executor.RunNextAsync();
            var child = f.Work.Dependencies(batch.WorkIds[0]).Single(); Fail(f, child.Id);
            var read = new ReadModel(f.Database); var page = read.Issues("translation", "synthetic_failure", 0, 30);
            check("one failed shared term links to both affected movies", page.Total == 2 && page.Items.All(x => (string)x["target_kind"]! == "media"));
            CheckSummary(read, "shared term summary counts the same affected entries", check);
            f.Content.MarkRemoved(a); f.Content.MarkRemoved(b);
            check("term with no remaining movie relation falls back to its task", read.Issues("translation", "synthetic_failure", 0, 30).Items.All(x => (string)x["target_kind"]! == "term"));
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p18-query-and-wake")))
        {
            var version = f.Database.WorkAvailable.Version;
            var media = f.Add("registration"); // Source observations retain readiness notifications.
            version = f.Database.WorkAvailable.Version;
            f.Content.RegisterMedia(new("s", f.Library, "unobserved"), "C:\\fixture\\quiet.mp4", "{}");
            var config = new Localizer.Unified.ConfigurationStore(f.Database); var personId = Id();
            config.SavePerson(personId, 0, "原名", "译名", null, []); config.DeletePerson(personId, 1); config.SeedBuiltin();
            f.Database.Write((db, tx) => Execute(db, tx, "INSERT INTO previews VALUES('quiet','hash',$n,'{}')", ("$n", Now())), wakeExecutor: false);
            check("registration person rules and previews cannot interrupt idle", version == f.Database.WorkAvailable.Version);
            var batch = f.Submit([media], ["Name"]);
            check("new work still wakes after quiet mutations", f.Database.WorkAvailable.Version > version);
            await f.Drain(); var before = f.Count("SELECT COUNT(*) FROM work_items");
            var manual = f.Content.SaveManual(f.Content.ReadField(media, "Name")!, "zh-Hans", "人工显示", true, f.Host.Get(f.Key(media), "Name").Display, Id());
            var pendingVersion = f.Database.WorkAvailable.Version;
            check("manual display intentions retain commit notifications", pendingVersion > version && manual.Id.Length > 0);
            await f.Drain(); check("pending manual display is admitted and applied", f.Count("SELECT COUNT(*) FROM work_items") > before && f.Host.Get(f.Key(media), "Name").Display.EndsWith("人工显示", StringComparison.Ordinal));
            var read = new ReadModel(f.Database);
            using var light = JsonDocument.Parse(JsonSerializer.Serialize(read.Overview(false)));
            using var full = JsonDocument.Parse(JsonSerializer.Serialize(read.Overview(true)));
            check("light overview skips history while explicit history stays available", light.RootElement.GetProperty("Work").ValueKind == JsonValueKind.Null && full.RootElement.GetProperty("Work").GetArrayLength() > 0);
            using (var db = f.Database.Open())
            {
                for (var i=0;i<40;i++) Execute(db, null, "INSERT INTO batches(id,submission_key,kind,language,target_epoch,created_utc) VALUES($id,$id,'fill','zh-Hans',0,$utc)", ("$id", "recent-"+i), ("$utc", DateTimeOffset.UtcNow.AddSeconds(i).ToString("O")));
            }
            using var latest = JsonDocument.Parse(JsonSerializer.Serialize(read.Overview(false)));
            check("recent query limits before aggregation while retaining old batches", latest.RootElement.GetProperty("Batches").EnumerateArray().Select(x => x.GetProperty("id").GetString()).SequenceEqual(new[] { "recent-39", "recent-38", "recent-37" }) && f.Count("SELECT COUNT(*) FROM batches") >= 41);
            check("new diagnostics do not change schema or data integrity", f.Text("PRAGMA integrity_check") == "ok" && f.Count("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0);
        }
        var directory = Path.Combine(root, "p18-nfo"); Directory.CreateDirectory(directory);
        var primary = Path.Combine(directory, "primary.mp4"); File.WriteAllBytes(primary, []);
        File.WriteAllText(Path.Combine(directory, "movie.nfo"), "<movie><plot>隔离简介</plot><genre>Drama</genre></movie>");
        foreach (var extension in new[] { ".3gp", ".ASF", ".mxf", ".ogv", ".divx", ".mkv" })
        {
            var other = Path.Combine(directory, "unconfirmed" + extension); File.WriteAllBytes(other, []);
            check("both NFO readers reject unconfirmed " + extension, NfoOverview.Read(primary).Error == "overview_nfo_ambiguous" && NfoGenres.Read(primary).Error == "genre_nfo_ambiguous");
            check("only host-confirmed additional part permits " + extension, NfoOverview.Read(primary, [other]).Error is null && NfoGenres.Read(primary, [other]).Error is null);
            File.Delete(other);
        }
        File.WriteAllText(Path.ChangeExtension(primary, ".nfo"), "<movie><plot>精确简介</plot><genre>Drama</genre></movie>");
        check("exact versus competing folder NFO policies remain intact", NfoOverview.Read(primary).Text == "精确简介" && NfoGenres.Read(primary).Error == "genre_nfo_ambiguous");
        var diagnostic = ExecutionDiagnostic.Capture("response_save", "work-fixture", new SqliteException("secret-Key prompt /private/path", 5, 517));
        var serialized = JsonSerializer.Serialize(diagnostic);
        check("diagnostics retain stage work and SQLite codes", diagnostic.WorkId == "work-fixture" && diagnostic.ErrorCode == 5 && diagnostic.SqliteExtendedCode == 517);
        check("diagnostics exclude raw exception secrets and paths", !serialized.Contains("secret", StringComparison.Ordinal) && !serialized.Contains("/private", StringComparison.Ordinal));
    }
    private static void Fail(ExecutorFixture f, string id) => f.Database.Write((db, tx) => Execute(db, tx,
        "UPDATE work_items SET state='failed',issue_kind='translation',error_code='synthetic_failure',lease_owner=NULL,lease_until_utc=NULL WHERE id=$id", ("$id", id)));
    private static void CheckSummary(ReadModel read, string name, Action<string, bool> check)
    {
        using var overview = JsonDocument.Parse(JsonSerializer.Serialize(read.Overview(false)));
        var issue = overview.RootElement.GetProperty("Issues").EnumerateArray().Single(x => x.GetProperty("error_code").GetString() == "synthetic_failure");
        check(name, issue.GetProperty("count").GetInt64() == read.Issues("translation", "synthetic_failure", 0, 30).Total);
    }
}
