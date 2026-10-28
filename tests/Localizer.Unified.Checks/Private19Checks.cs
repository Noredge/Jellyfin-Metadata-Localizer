using System.Reflection;
using System.Text;
using System.Text.Json;
using Localizer.Unified;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

internal static class Private19Checks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        foreach (var manual in new[] { false, true })
        using (var f = new ExecutorFixture(Path.Combine(root, manual ? "p19-manual-term" : "p19-completed-term")))
        {
            const string term = "repaired-term"; var media = f.Add("repair", "overview", term);
            string SubmitTerm() => f.Work.SubmitTerms(Id(), "dictionary", f.Settings.Read().Revision, [term]).WorkIds.Single();
            var child = SubmitTerm(); Fail(f, child);
            var parent = f.Submit([media], ["Genres"]).WorkIds.Single(); await f.Executor.RunNextAsync();
            check("repair fixture starts with a failed parent " + manual, f.Work.Find(parent)!.Error == "dependency_failed");
            if (manual) new Localizer.Unified.ConfigurationStore(f.Database).SaveTerm(term, "zh-Hans", "人工分类", 0, Id());
            else { SubmitTerm(); await f.Drain(); }
            var calls = f.Transport.Calls; f.Work.Retry(parent); await f.Drain();
            check("old parent uses already repaired selected term " + manual, f.Work.Find(parent)!.State == "completed" && f.Transport.Calls == calls);
            check("repair retains old failed work and dependency history " + manual, f.Work.Find(child)!.State == "failed" && f.Work.Dependencies(parent).Single().Id == child);
            var selected = new Localizer.Unified.ConfigurationStore(f.Database).Terms(term).Single().Chinese;
            check("display uses the current dictionary selection " + manual, JsonSerializer.Deserialize<string[]>(f.Host.Get(f.Key(media), "Genres").Display)!.Single() == selected);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p19-future-obsolete-child")))
        {
            var media = f.Add("future", "overview", "future-term"); var parent = f.Submit([media], ["Genres"]).WorkIds.Single(); await f.Executor.RunNextAsync();
            var child = f.Work.Dependencies(parent).Single();
            f.Database.Write((db, tx) => Execute(db, tx, "UPDATE work_items SET state='waiting',next_run_utc=$next WHERE id=$id", ("$next", f.Clock.AddHours(1).ToString("O")), ("$id", child.Id)));
            new Localizer.Unified.ConfigurationStore(f.Database).SaveTerm("future-term", "zh-Hans", "当前人工词", 0, Id());
            check("resolved dependency does not hide the parent's due time", f.Work.NextWakeDelay(TimeSpan.FromSeconds(8)) == TimeSpan.Zero);
            await f.Drain();
            check("future obsolete dependency cannot delay cached parent display", f.Work.Find(parent)!.State == "completed" && f.Transport.Calls == 0 && f.Work.Dependencies(parent).Single().Id == child.Id);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p19-forced-unknown")))
        {
            const string term = "forced-term"; var media = f.Add("forced", "overview", term);
            var config = new Localizer.Unified.ConfigurationStore(f.Database); config.SaveTerm(term, "zh-Hans", "已有旧词", 0, Id());
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "network_uncertain", OutcomeUnknown: true)); };
            var parent = f.Submit([media], ["Genres"], force: true).WorkIds.Single(); await f.Drain();
            bool consent = false; try { f.Work.Retry(parent); } catch (InvalidOperationException e) when (e.Message == "UnknownOutcomeConsentRequired") { consent = true; }
            check("old cached selection cannot bypass forced unknown consent", consent && f.Work.Find(parent)!.State == "failed" && f.Transport.Calls == 1);
            var history = f.Text("SELECT state||':'||uncertainty_acknowledged FROM request_attempts");
            config.SaveTerm(term, "zh-Hans", "新人工词", 1, Id()); f.Work.Retry(parent); await f.Drain();
            check("new manual control resolves old forced failure without generation", f.Work.Find(parent)!.State == "completed" && f.Transport.Calls == 1 && JsonSerializer.Deserialize<string[]>(f.Host.Get(f.Key(media), "Genres").Display)!.Single() == "新人工词");
            check("manual recovery preserves unknown request evidence and consent", history == "unknown:0" && f.Text("SELECT state||':'||uncertainty_acknowledged FROM request_attempts") == history);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p19-forced-failed-cache")))
        {
            var media = f.Add("force-failure", "overview", "force-retry"); new Localizer.Unified.ConfigurationStore(f.Database).SaveTerm("force-retry", "zh-Hans", "旧选用", 0, Id());
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "invalid_output")); };
            var parent = f.Submit([media], ["Genres"], force: true).WorkIds.Single(); await f.Drain();
            f.Transport.Handler = null; f.Work.Retry(parent); await f.Drain();
            check("explicit forced retry still requests a new result despite older cache", f.Work.Find(parent)!.State == "completed" && f.Transport.Calls == 2);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p19-issue-filter")))
        {
            var media = f.Add("issues"); var read = new ReadModel(f.Database);
            f.Content.ProtectExternal(f.Content.ReadField(media, "Overview")!, "external display");
            check("external display issue appears in movie problem filter", read.Issues("display", "external_display_preserved", 0, 30).Total == 1 && read.Movies("", null, "problem", 0, 30).Items.Single()["id"] as string == media);
            var settings = f.Settings.Read(); f.Settings.Save(settings.Revision, settings.Language, new(true, false, true), settings.ManagedLibraries, true);
            f.Content.ObserveUnavailable(media, "Overview", "probe_unreadable");
            check("disabled field is excluded from issue filter and source badge", read.Movies("", null, "problem", 0, 30).Total == 0 && Convert.ToInt64(read.Movies("", null, "all", 0, 30).Items.Single()["source_problems"]) == 0);
            settings = f.Settings.Read(); f.Settings.Save(settings.Revision, settings.Language, new(true, true, true), settings.ManagedLibraries, true);
            check("reenabled unreadable source agrees across all issue surfaces", read.Issues("source", "probe_unreadable", 0, 30).Total == 1 && read.Movies("", null, "problem", 0, 30).Total == 1 && Convert.ToInt64(read.Movies("", null, "all", 0, 30).Items.Single()["source_problems"]) == 1);
            settings = f.Settings.Read(); f.Settings.Save(settings.Revision, settings.Language, settings.Fields, [], false);
            check("unmanaged source remains browseable without an actionable badge", read.Movies("", null, "all", 0, 30).Total == 1 && read.Movies("", null, "problem", 0, 30).Total == 0 && Convert.ToInt64(read.Movies("", null, "all", 0, 30).Items.Single()["source_problems"]) == 0);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p19-diagnostics")))
        {
            var media = f.Add("diagnostic"); var id = f.Submit([media], ["Overview"]).WorkIds.Single(); ExecutionDiagnostic? invocation = null;
            f.Host.BeforeReadReturn = _ => Task.FromException(new SqliteException("secret request body", 5, 517));
            try { await f.Executor.RunNextAsync(default, value => invocation = value); } catch (SqliteException) { }
            check("invocation callback captures this failure stage work and code", invocation is { Stage: "source_read", ErrorCode: 5, SqliteExtendedCode: 517 } && invocation.WorkId == id);
            f.Host.BeforeReadReturn = null; f.Clock = f.Clock.AddSeconds(2); invocation = null;
            while (await f.Executor.RunNextAsync(default, value => invocation = value)) { }
            check("successful recovery does not reuse historical diagnostic as current", invocation is null && f.Executor.LastFailure?.WorkId == id && f.Work.Find(id)!.State == "completed");
            ExecutionDiagnostic? queueError = null;
            f.Database.ReleaseReadPool(); // Close idle readers before injecting an exclusive OS file lock.
            using (var locked = new FileStream(f.Database.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { f.Work.NextWakeDelay(TimeSpan.FromSeconds(8)); }
                catch (SqliteException error) { queueError = invocation ?? ExecutionDiagnostic.Capture("queue_deadline", null, error); }
            }
            check("later queue read reports its own error with no stale work", queueError is { Stage: "queue_deadline", WorkId: null, ErrorCode: 14 } && !JsonSerializer.Serialize(queueError).Contains("secret", StringComparison.Ordinal));
            check("queue diagnostic fault leaves durable state intact", f.Text("PRAGMA integrity_check") == "ok" && f.Count("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p19-work-reads")))
        {
            var original = string.Concat(Enumerable.Repeat("🧭漢", 120)); var result = new string('译', 700);
            var media = f.Add("long-work", original); f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(result, HttpStatus: 200)); };
            var batch = f.Submit([media], ["Overview"]); await f.Drain(); var id = batch.WorkIds.Single(); var read = new ReadModel(f.Database);
            var row = read.Work(null, 0, 30, id).Single(); var detail = read.WorkDetail(id)!;
            check("work lists return bounded Unicode summaries and truncation flags", ((string)row["original"]!).EnumerateRunes().Count() == 180 && ((string)row["result_text"]!).Length == 180 && Convert.ToInt64(row["original_truncated"]) == 1 && Convert.ToInt64(row["result_truncated"]) == 1);
            check("lazy exact detail preserves the complete original and saved result", (string)detail["original"]! == original && (string)detail["result_text"]! == result && read.WorkDetail("missing") is null);
            check("exact filters retain batch membership and pagination semantics", read.Work(batch.Id, 0, 30, id).Length == 1 && read.Work("wrong", 0, 30, id).Length == 0 && read.Work(null, 1, 30, id).Length == 0);
            var sql = (string)typeof(ReadModel).GetMethod("WorkSql", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [true, false, false])!;
            using var db = f.Database.Open(); using var cmd = Command(db, null, "EXPLAIN QUERY PLAN " + sql, ("$work", id), ("$limit", 30), ("$offset", 0));
            using var plan = cmd.ExecuteReader(); var steps = new List<string>(); while (plan.Read()) steps.Add(plan.GetString(3));
            check("exact work lookup uses its primary index without scan or sorting", steps.Any(x => x.Contains("SEARCH w USING INDEX", StringComparison.Ordinal)) && !steps.Any(x => x.Contains("SCAN w", StringComparison.Ordinal) || x.Contains("TEMP B-TREE", StringComparison.Ordinal)));
            check("summary reads cannot create work requests or change results", f.Transport.Calls == 1 && f.Count("SELECT COUNT(*) FROM request_attempts") == 1 && f.Content.Selected(f.Content.ReadField(media, "Overview")!.SourceId, "zh-Hans")!.Text == result);
        }
    }
    private static void Fail(ExecutorFixture f, string id) => f.Database.Write((db, tx) => Execute(db, tx,
        "UPDATE work_items SET state='failed',issue_kind='translation',error_code='synthetic_failure' WHERE id=$id", ("$id", id)));
}
