using System.Reflection;
using System.Text.Json;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class Private21Checks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        foreach (var change in new[] { "manual", "disabled", "library", "removed", "source", "cancelled", "language", "automatic" })
        using (var f = new ExecutorFixture(Path.Combine(root, "p21-authority-" + change)))
        {
            var media = f.Add("authority", "overview", "p21-term");
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "invalid_output")); };
            string parent; string? batch = null;
            if (change == "automatic") parent = f.Work.EnqueueAutomatic("nfo", f.Seed(media, "Genres"))!;
            else { var receipt = f.Submit([media], ["Genres"]); parent = receipt.WorkIds.Single(); batch = receipt.Id; }
            await f.Drain(); var child = f.Work.Dependencies(parent).Single(); var read = new ReadModel(f.Database);
            check("current inherited failure is actionable before " + change, read.Issues("translation", "invalid_output", 0, 30).Total == 1 && f.Work.Find(parent)!.Error == "dependency_failed");
            var settings = f.Settings.Read();
            switch (change)
            {
                case "manual": f.Content.SaveManual(f.Content.ReadField(media, "Genres")!, "zh-Hans", "[\"人工分类\"]", true, f.Host.Get(f.Key(media), "Genres").Display, Id()); break;
                case "disabled": f.Settings.Save(settings.Revision, settings.Language, new(true, true, false), settings.ManagedLibraries, true); break;
                case "library": f.Settings.Save(settings.Revision, settings.Language, settings.Fields, [], true); break;
                case "removed": f.Content.MarkRemoved(media); break;
                case "source": f.Host.Change(f.Key(media), "Genres", row => row with { Original = "[\"new source term\"]" }); f.Observe(media, "Genres"); break;
                case "cancelled": f.Work.CancelBatch(batch!); break;
                case "language": f.Settings.Save(settings.Revision, "en", settings.Fields, settings.ManagedLibraries, true); break;
                case "automatic": f.Settings.Save(settings.Revision, settings.Language, settings.Fields, settings.ManagedLibraries, false); break;
            }
            check("orphan term is excluded from current issues after " + change, read.Issues("translation", "invalid_output", 0, 30).Total == 0);
            var denied = false; try { f.Work.Retry(child.Id); } catch (InvalidOperationException error) when (error.Message == "RetryIntentExpired") { denied = true; }
            check("obsolete inherited term cannot be retried after " + change, denied && f.Transport.Calls == 1 && f.Work.Find(child.Id)!.State == "failed" && f.Count("SELECT COUNT(*) FROM request_attempts") == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p21-shared-demand")))
        {
            var a = f.Add("shared-a", "overview", "shared-term"); var b = f.Add("shared-b", "overview", "shared-term");
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "invalid_output")); };
            var receipt = f.Submit([a, b], ["Genres"]); await f.Drain(); var child = f.Work.Dependencies(receipt.WorkIds[0]).Single();
            f.Content.SaveManual(f.Content.ReadField(a, "Genres")!, "zh-Hans", "[\"人工分类\"]", true, f.Host.Get(f.Key(a), "Genres").Display, Id());
            var issues = new ReadModel(f.Database).Issues("translation", "invalid_output", 0, 30);
            check("shared term keeps only its remaining valid parent issue", issues.Total == 1 && (string)issues.Items.Single()["media_id"]! == b);
            f.Transport.Handler = null; f.Work.Retry(child.Id); await f.Drain();
            check("one valid parent can still retry and reuse a shared term", f.Work.Find(receipt.WorkIds[1])!.State == "completed" && f.Transport.Calls == 2);
            check("shared recovery preserves the other film's manual display", JsonSerializer.Deserialize<string[]>(f.Host.Get(f.Key(a), "Genres").Display)!.Single() == "人工分类");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p21-independent-demand")))
        {
            const string term = "independent-term"; var media = f.Add("independent", "overview", term);
            var child = f.Work.SubmitTerms(Id(), "dictionary", f.Settings.Read().Revision, [term]).WorkIds.Single();
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "invalid_output")); }; await f.Drain();
            f.Submit([media], ["Genres"]); await f.Drain();
            f.Content.SaveManual(f.Content.ReadField(media, "Genres")!, "zh-Hans", "[\"人工分类\"]", true, f.Host.Get(f.Key(media), "Genres").Display, Id());
            var settings = f.Settings.Read(); f.Settings.Save(settings.Revision, settings.Language, new(true, true, false), [], false);
            var issues = new ReadModel(f.Database).Issues("translation", "invalid_output", 0, 30);
            check("independent dictionary failure stays actionable outside movie scope", issues.Total == 1 && (string)issues.Items.Single()["target_kind"]! == "term");
            f.Transport.Handler = null; f.Work.Retry(child); await f.Drain();
            check("independent dictionary retry needs no managed film or automatic mode", f.Work.Find(child)!.State == "completed" && f.Transport.Calls == 2);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p21-confirm-version")))
        {
            var media = f.Add("unknown"); f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "network_uncertain", OutcomeUnknown: true)); };
            var id = f.Submit([media], ["Overview"]).WorkIds.Single(); await f.Drain();
            var version = f.Text("SELECT updated_utc||'|'||state||'|'||attempt_count FROM work_items WHERE id='" + id + "'")!;
            var denied = false; try { f.Work.Retry(id, true, version + "stale"); } catch (InvalidOperationException error) when (error.Message == "RevisionConflict") { denied = true; }
            check("stale confirmation version rejects without acknowledging unknown", denied && f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='unknown' AND uncertainty_acknowledged=0") == 1 && f.Work.Find(id)!.State == "paused");
            f.Transport.Handler = null; f.Work.Retry(id, true, version); await f.Drain();
            check("current version confirmation retries exactly once and retains evidence", f.Transport.Calls == 2 && f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='unknown' AND uncertainty_acknowledged=1") == 1 && f.Work.Find(id)!.State == "completed");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p21-issue-index")))
        {
            var media = f.Add("index"); f.Submit([media], ["Overview"]); await f.Drain();
            var before = f.Text("SELECT group_concat(id||':'||state,',') FROM work_items"); var schema = f.Text("SELECT value FROM runtime_state WHERE key='schema_definition'");
            f.Database.Write((db, tx) => Execute(db, tx, "DROP INDEX work_issue_candidates; DROP INDEX work_term_history; DROP INDEX work_media_history"), wakeExecutor: false);
            _ = new UnifiedDatabase(f.Root); _ = new UnifiedDatabase(f.Root);
            check("three issue indexes upgrade an existing database idempotently", f.Count("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN('work_issue_candidates','work_term_history','work_media_history')") == 3 && f.Text("PRAGMA user_version") == "1" && schema == f.Text("SELECT value FROM runtime_state WHERE key='schema_definition'"));
            check("issue index installation retains history and model accounting", before == f.Text("SELECT group_concat(id||':'||state,',') FROM work_items") && f.Transport.Calls == 1);
            var sql = (string)typeof(ReadModel).GetField("CurrentIssues", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            using var db = f.Database.Open(); using var cmd = Command(db, null, "EXPLAIN QUERY PLAN " + sql + " SELECT COUNT(*) FROM current_issues"); using var r = cmd.ExecuteReader(); var plan = new List<string>(); while (r.Read()) plan.Add(r.GetString(3));
            check("issue candidates use partial index without full work table scans", plan.Count(x => x.Contains("work_issue_candidates", StringComparison.Ordinal)) >= 2 && !plan.Any(x => x == "SCAN w"));
            check("latest term and media comparisons use history indexes", plan.Any(x => x.Contains("work_term_history", StringComparison.Ordinal)) && plan.Any(x => x.Contains("work_media_history", StringComparison.Ordinal)));
            check("issue query indexes preserve integrity and foreign keys", f.Text("PRAGMA integrity_check") == "ok" && f.Count("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0);
        }
    }
}
