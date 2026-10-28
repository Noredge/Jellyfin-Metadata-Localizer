using System.Reflection;
using System.Text.Json;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class Private20Checks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        foreach (var force in new[] { false, true })
        using (var f = new ExecutorFixture(Path.Combine(root, "p20-replacement-" + force)))
        {
            const string term = "replacement-term"; var media = f.Add("replacement", "overview", term);
            if (force) new Localizer.Unified.ConfigurationStore(f.Database).SaveTerm(term, "zh-Hans", "旧选用词", 0, Id());
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "invalid_output")); };
            var parent = f.Submit([media], ["Genres"], force).WorkIds.Single(); await f.Drain();
            var old = f.Work.Dependencies(parent).Single();
            check("replacement fixture fails on its original term " + force, f.Work.Find(parent)!.Error == "dependency_failed");
            var replacement = f.Work.SubmitTerms(Id(), "dictionary", f.Settings.Read().Revision, [term], force: true).WorkIds.Single();
            f.Database.Write((db, tx) => Execute(db, tx, "UPDATE work_items SET state='waiting',next_run_utc=$n WHERE id=$id", ("$n", f.Clock.AddMinutes(1).ToString("O")), ("$id", replacement)));
            f.Transport.Handler = null; var calls = f.Transport.Calls;
            f.Work.Retry(parent);
            check("retry links current replacement and retains old dependency " + force, f.Work.Dependencies(parent).Select(x => x.Id).ToHashSet().SetEquals([old.Id, replacement]));
            check("parent waits for future replacement without a duplicate send " + force, !await f.Executor.RunNextAsync() && f.Transport.Calls == calls && f.Work.Find(parent)!.State == "queued" && f.Work.NextWakeDelay(TimeSpan.FromSeconds(8)) == TimeSpan.FromSeconds(8));
            f.Clock = f.Clock.AddMinutes(1); await f.Drain();
            check("replacement completion wakes and completes old parent " + force, f.Work.Find(parent)!.State == "completed" && f.Transport.Calls == calls + 1);
            check("old failure remains historical after replacement " + force, f.Work.Find(old.Id)!.State == "failed" && f.Work.Dependencies(parent).Length == 2);
            check("replacement value is displayed without another explicit retry " + force, JsonSerializer.Deserialize<string[]>(f.Host.Get(f.Key(media), "Genres").Display)!.Single() == "中文 " + term);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p20-unknown-replacement")))
        {
            const string term = "unknown-replacement"; var media = f.Add("unknown", "overview", term);
            new Localizer.Unified.ConfigurationStore(f.Database).SaveTerm(term, "zh-Hans", "旧选用词", 0, Id());
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "invalid_output")); };
            var parent = f.Submit([media], ["Genres"], true).WorkIds.Single(); await f.Drain();
            var replacement = f.Work.SubmitTerms(Id(), "dictionary", f.Settings.Read().Revision, [term], true).WorkIds.Single();
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "network_uncertain", OutcomeUnknown: true)); }; await f.Drain();
            var denied = false; try { f.Work.Retry(parent); } catch (InvalidOperationException e) when (e.Message == "UnknownOutcomeConsentRequired") { denied = true; }
            check("new replacement unknown outcome still requires explicit consent", denied && f.Work.Find(parent)!.State == "failed" && f.Work.Find(replacement)!.State == "paused" && f.Transport.Calls == 2);
            check("denied replacement retry rolls back new dependency and acknowledgement", f.Work.Dependencies(parent).Length == 1 && f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='unknown' AND uncertainty_acknowledged=0") == 1);
            f.Transport.Handler = null; f.Work.Retry(parent, acceptUnknown: true); await f.Drain();
            check("explicit consent retries only the current replacement", f.Work.Find(parent)!.State == "completed" && f.Transport.Calls == 3 && f.Work.Find(replacement)!.Attempts == 2);
            check("acknowledged unknown and obsolete failure evidence retained", f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='unknown' AND uncertainty_acknowledged=1") == 1 && f.Work.Dependencies(parent).Length == 2);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p20-missing-scope")))
        {
            var media = f.Add("scope"); var read = new ReadModel(f.Database); f.Submit([media], ["Name", "Genres"]); await f.Drain();
            check("enabled missing synopsis appears in scoped missing filter", read.Movies("", null, "missing", 0, 30).Total == 1);
            var settings = f.Settings.Read(); f.Settings.Save(settings.Revision, settings.Language, new(true, false, true), settings.ManagedLibraries, true);
            check("disabled missing field agrees with overview and missing filter", read.Movies("", null, "missing", 0, 30).Total == 0 && !JsonSerializer.Serialize(read.Overview()).Contains("\"field\":\"Overview\"", StringComparison.Ordinal));
            settings = f.Settings.Read(); f.Settings.Save(settings.Revision, settings.Language, new(true, true, true), settings.ManagedLibraries, true);
            check("reenabled missing field returns without generation", read.Movies("", null, "missing", 0, 30).Total == 1 && f.Transport.Calls == 1);
            f.Content.KeepOriginal(f.Content.ReadField(media, "Overview")!, "overview");
            check("keep original is excluded consistently from missing filter", read.Movies("", null, "missing", 0, 30).Total == 0);
            f.Content.FollowGlobal(f.Content.ReadField(media, "Overview")!);
            settings = f.Settings.Read(); f.Settings.Save(settings.Revision, settings.Language, settings.Fields, [], false);
            check("unmanaged missing film stays browseable without missing badge", read.Movies("", null, "all", 0, 30).Total == 1 && read.Movies("", null, "missing", 0, 30).Total == 0);
            settings = f.Settings.Read(); f.Settings.Save(settings.Revision, settings.Language, settings.Fields, [f.Library], true);
            check("restored managed scope reveals existing missing source", read.Movies("", f.Library, "missing", 0, 30).Total == 1 && f.Transport.Calls == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p20-history-index")))
        {
            var media = f.Add("history-index"); var batch = f.Submit([media], ["Name", "Overview"]); await f.Drain();
            var read = new ReadModel(f.Database); var before = JsonSerializer.Serialize(read.Work(null, 0, 100));
            f.Database.Write((db, tx) => Execute(db, tx, "DROP INDEX work_read_order"), wakeExecutor: false);
            var schema = f.Text("SELECT value FROM runtime_state WHERE key='schema_definition'");
            _ = new UnifiedDatabase(f.Root); _ = new UnifiedDatabase(f.Root);
            check("read index upgrades an existing schema-one database idempotently", f.Count("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='work_read_order'") == 1 && f.Text("PRAGMA user_version") == "1" && f.Text("SELECT value FROM runtime_state WHERE key='schema_definition'") == schema);
            check("index installation preserves exact historical rows and selection", JsonSerializer.Serialize(read.Work(null, 0, 100)) == before && f.Transport.Calls == 2);
            var sql = typeof(ReadModel).GetMethod("WorkSql", BindingFlags.NonPublic | BindingFlags.Static)!;
            string[] Plan(bool exact)
            {
                using var db = f.Database.Open(); using var cmd = Command(db, null, "EXPLAIN QUERY PLAN " + (string)sql.Invoke(null, [exact, false, false])!, ("$work", batch.WorkIds[0]), ("$limit", 30), ("$offset", 0));
                using var rows = cmd.ExecuteReader(); var values = new List<string>(); while (rows.Read()) values.Add(rows.GetString(3)); return [.. values];
            }
            check("whole work history streams from its ordering index", Plan(false).Any(x => x.Contains("USING INDEX work_read_order", StringComparison.Ordinal)) && !Plan(false).Any(x => x.Contains("TEMP B-TREE", StringComparison.Ordinal)));
            check("exact work lookup retains primary key plan without sorting", Plan(true).Any(x => x.Contains("SEARCH w USING INDEX sqlite_autoindex_work_items_1", StringComparison.Ordinal)) && !Plan(true).Any(x => x.Contains("TEMP B-TREE", StringComparison.Ordinal)));
            check("indexed pagination preserves stable membership and order", read.Work(batch.Id, 0, 1).Concat(read.Work(batch.Id, 1, 1)).Select(x => (string)x["id"]!).SequenceEqual(read.Work(batch.Id, 0, 100).Select(x => (string)x["id"]!)));
            check("index upgrade leaves integrity and foreign keys valid", f.Text("PRAGMA integrity_check") == "ok" && f.Count("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0);
        }
    }
}
