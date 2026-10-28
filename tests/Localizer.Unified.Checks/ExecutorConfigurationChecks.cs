using System.Text.Json;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class ExecutorConfigurationChecks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        using (var f = new ExecutorFixture(Path.Combine(root, "genre-display-order")))
        {
            var m = f.Add("genre-order", "", "第一词", "第二词");
            f.Submit([m], ["Genres"]); await f.Drain(); var calls = f.Transport.Calls; var writes = f.Host.Writes;
            var display = f.Host.Get(f.Key(m), "Genres").Display;
            f.Host.Change(f.Key(m), "Genres", x => x with { Original = "[\"第二词\",\"第一词\",\"第二词\"]" });
            await f.Observer.ObserveAllAsync(); await f.Drain();
            check("genre reorder and duplicates do not request or rewrite an equivalent displayed set", f.Transport.Calls == calls && f.Host.Writes == writes && f.Host.Get(f.Key(m), "Genres").Display == display);
            check("genre render reconciliation completes without a false external edit", f.Count("SELECT COUNT(*) FROM work_items WHERE state='failed'") == 0 && f.Content.ReadField(m, "Genres")!.ControlRevision == 0);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "preview-replay")))
        {
            var m = f.Add("preview"); var source = f.Content.ReadField(m, "Name")!; var revision = f.Settings.Read().Revision;
            var receipt = f.Work.Submit("preview-command", "retranslate", revision, [new(source, f.Host.Get(f.Key(m), "Name").Display)], true);
            await f.Drain();
            var replay = f.Work.TryReplay("preview-command", "retranslate", revision, [(m, "Name")], true);
            check("forced preview replay survives changed control and displayed value without another request", replay?.WorkIds.SequenceEqual(receipt.WorkIds) == true && f.Transport.Calls == 1 && f.Content.ReadField(m, "Name")!.ControlRevision > source.ControlRevision);
            var rejected = false;
            try { f.Work.TryReplay("preview-command", "retranslate", revision, [(m, "Overview")], true); }
            catch (InvalidOperationException e) when (e.Message == "SubmissionConflict") { rejected = true; }
            check("same preview command cannot be reused for a different field", rejected && f.Work.TryReplay("unused-command", "fill", revision, [(m, "Name")]) is null);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "manual-admission")))
        {
            var m = f.Add("admit"); var original = f.Content.ReadField(m, "Overview")!;
            f.Content.SaveManual(original, "zh-Hans", "重启后自动续接人工显示", true, original.Content, "manual-before-crash");
            f.Restart(); await f.Drain();
            check("manual save-and-show survives the gap before queue admission", f.Transport.Calls == 0 && f.Host.Get(f.Key(m), "Overview").Display == "重启后自动续接人工显示" && f.Count("SELECT COUNT(*) FROM work_items") == 1);
            f.Restart(); await f.Drain(); check("recovery admission is idempotent for a displayed manual intent", f.Host.Writes == 1 && f.Count("SELECT COUNT(*) FROM work_items") == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "field-only-refresh")))
        {
            var m = f.Add("refresh", "旧简介", "旧词"); f.Submit([m]); await f.Drain(); var initial = f.Transport.Calls;
            f.Host.Change(f.Key(m), "Overview", x => x with { Original = "新的简介", Display = "新的简介" });
            foreach (var field in new[] { "Name", "Genres" }) f.Host.Change(f.Key(m), field, x => x with { Display = x.Field == "Name" ? x.Prefix + x.Original : x.Original! });
            await f.Observer.ObserveAllAsync(); await f.Drain();
            check("overview-only source change only requests the new overview", f.Transport.Calls == initial + 1 && f.Transport.Inputs.Last().Field == "Overview" && f.Host.Get(f.Key(m), "Name").Display.StartsWith("LAB · 中文 ", StringComparison.Ordinal));
            var count = f.Count("SELECT COUNT(*) FROM work_items");
            await f.Observer.ObserveAllAsync(); await f.Observer.ObserveAllAsync(); await f.Drain();
            check("plugin writes and repeated observations do not loop or create work", f.Transport.Calls == initial + 1 && f.Count("SELECT COUNT(*) FROM work_items") == count);
            f.Host.Change(f.Key(m), "Genres", x => x with { Original = "[\"旧词\",\"新增词\"]", Display = "[\"旧词\",\"新增词\"]" });
            await f.Observer.ObserveAllAsync(); await f.Drain();
            check("one added genre only requests the missing shared term", f.Transport.Calls == initial + 2 && f.Transport.Inputs.Last().Field == "Term" && f.Transport.Inputs.Last().Text == "新增词");
            count = f.Count("SELECT COUNT(*) FROM work_items"); f.Host.Change(f.Key(m), "Overview", x => x with { EvidenceJson = "{\"unrelated_xml\":true}" });
            await f.Observer.ObserveAllAsync(); await f.Drain(); check("unrelated source evidence does not invalidate content", f.Count("SELECT COUNT(*) FROM work_items") == count && f.Transport.Calls == initial + 2);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "blank-then-filled")))
        {
            var m = f.Add("blank", ""); await f.Observer.ObserveAllAsync(); await f.Drain();
            check("normal blank overview has no waiting work or model request", f.Transport.Calls == 0 && f.Count("SELECT COUNT(*) FROM work_items") == 0);
            f.Host.Change(f.Key(m), "Overview", x => x with { Original = "后来补充", Display = "后来补充" }); await f.Observer.ObserveAllAsync(); await f.Drain();
            check("later source addition enters the same automatic flow", f.Transport.Calls == 1 && f.Host.Get(f.Key(m), "Overview").Display == "中文 后来补充");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "people-rules")))
        {
            var m = f.Add("Alice"); var config = new ConfigurationStore(f.Database); var id = Id(); var rule = config.SavePerson(id, 0, "Alice", "阿莉", "Alice", ["ALICE"]);
            f.Host.Change(f.Key(m), "Name", x => x with { PersonNames = ["Alice", "Unrelated Actor"] });
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply("阿莉的旅行", HttpStatus: 200)); };
            f.Submit([m], ["Name"]); await f.Drain(); var source = f.Content.ReadField(m, "Name")!; var saved = f.Content.Selected(source.SourceId, "zh-Hans")!;
            check("request input keeps only needed people and protection names", f.Transport.Inputs.Single().NamesJson.Contains("Alice", StringComparison.Ordinal) && !f.Transport.Inputs.Single().ContextJson.Contains("Unrelated Actor", StringComparison.Ordinal));
            config.SavePerson(id, rule.Revision, "Alice", "阿力", "Alice", ["ALICE"]); f.Submit([m], ["Name"]); await f.Drain();
            check("new person mapping rerenders cached model text without HTTP or history mutation", f.Transport.Calls == 1 && f.Host.Get(f.Key(m), "Name").Display == "LAB · 阿力的旅行" && f.Content.Selected(source.SourceId, "zh-Hans")!.Text == saved.Text);
            var current = f.Content.ReadField(m, "Name")!; f.Content.SaveManual(current, "zh-Hans", "人工保留阿莉", true, f.Host.Get(f.Key(m), "Name").Display, "people-manual"); await f.Drain();
            check("person rendering preserves exact manually edited text", f.Host.Get(f.Key(m), "Name").Display == "LAB · 人工保留阿莉" && f.Transport.Calls == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "manual-term-race")))
        {
            var m = f.Add("term-race", "", "共享词"); var config = new ConfigurationStore(f.Database); var batch = f.Submit([m], ["Genres"], force: true);
            await f.Executor.RunNextAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var response = new TaskCompletionSource<GenerationReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Handler = async (_, send, _) => { send(); entered.SetResult(); return await response.Task; };
            var running = f.Executor.RunNextAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            config.SaveTerm("共享词", "zh-Hans", "人工词条", 0, "term-manual"); response.SetResult(new("晚到词条", HttpStatus: 200)); await running; await f.Drain();
            check("manual dictionary edit wins over an in-flight forced term generation", f.Transport.Calls == 1 && config.Terms("共享词").Single().Chinese == "人工词条" && f.Work.Find(batch.WorkIds[0])!.State == "completed" && JsonSerializer.Deserialize<string[]>(f.Host.Get(f.Key(m), "Genres").Display)!.SequenceEqual(new[] { "人工词条" }));
            var other = f.Add("other-film", "", "共享词"); f.Submit([other], ["Genres"]); await f.Drain(); var writes = f.Host.Writes;
            config.SaveTerm("共享词", "zh-Hans", "下一版词条", config.Terms("共享词").Single().ControlRevision, "term-next");
            await f.Drain(); check("dictionary edits do not rewrite all existing films immediately", f.Host.Writes == writes && f.Transport.Calls == 1);
            f.Submit([m], ["Genres"]); await f.Drain(); check("explicit synchronization uses the latest dictionary mapping", f.Transport.Calls == 1 && f.Host.Writes == writes + 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "builtin-seeding")))
        {
            var config = new ConfigurationStore(f.Database); config.SeedBuiltin(); var versions = f.Count("SELECT COUNT(*) FROM translation_versions"); config.SeedBuiltin();
            check("builtin dictionary seeds idempotently inside the unified database", versions > 0 && f.Count("SELECT COUNT(*) FROM translation_versions") == versions && f.Transport.Calls == 0);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "baseline-selected-but-original")))
        {
            var m = f.Add("original-baseline"); var field = f.Content.ReadField(m, "Overview")!;
            f.Content.SaveManual(field, "zh-Hans", "历史已选译文", true, field.Content, "old-selection");
            f.Database.Write((db, tx) =>
            {
                Execute(db, tx, "UPDATE settings SET baseline_established=0; UPDATE managed_libraries SET baseline_established=0,imported_baseline=1; UPDATE display_intents SET state='applied',mode='original',desired_value=expected_value; INSERT INTO import_runs(id,manifest_hash,state,created_utc) VALUES('synthetic','synthetic','completed','2026-10-26T00:00:00Z')"); return true;
            });
            await f.Observer.ObserveAllAsync(); await f.Observer.ObserveAllAsync(); await f.Observer.ObserveAllAsync(); await f.Drain();
            check("unchanged historical original display stays baseline on later reconciliation", f.Host.Writes == 0 && f.Transport.Calls == 0 && f.Count("SELECT COUNT(*) FROM work_items") == 0);
            f.Submit([m], ["Overview"]); await f.Drain(); check("explicit fill may still reuse a historical selection after baseline", f.Host.Get(f.Key(m), "Overview").Display == "历史已选译文" && f.Transport.Calls == 0);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "new-library-baseline")))
        {
            var old = f.Add("old-scope"); var addedLibrary = Id(); var key = new MediaIdentity("synthetic-server", addedLibrary, Id());
            f.Host.Add(new(key, "C:\\fixture\\library-old.mp4", "Name", "新范围历史标题", "新范围历史标题"));
            f.Host.Add(new(key, "C:\\fixture\\library-old.mp4", "Overview", "新范围历史简介", "新范围历史简介"));
            f.Host.Add(new(key, "C:\\fixture\\library-old.mp4", "Genres", "[]", "[]"));
            var s = f.Settings.Read(); f.Settings.Save(s.Revision, s.Language, s.Fields, [f.Library, addedLibrary], true);
            f.Host.Change(f.Key(old), "Overview", x => x with { Original = "原范围同期新简介", Display = "原范围同期新简介" });
            await f.Observer.ObserveAllAsync(); await f.Drain();
            check("adding a managed library establishes its baseline without historical backfill", f.Transport.Calls == 1 && f.Host.Get(key, "Overview").Display == "新范围历史简介");
            check("new library baseline does not lose simultaneous changes in an existing scope", f.Host.Get(f.Key(old), "Overview").Display == "中文 原范围同期新简介");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "empty-overview-manual")))
        {
            var m = f.Add("empty-manual", ""); var empty = f.Content.ReadField(m, "Overview")!;
            f.Content.SaveManual(empty, "zh-Hans", "明确人工编写的简介", true, "", "empty-manual-show"); await f.Drain();
            check("empty synopsis still permits an explicit manual version and guarded display", f.Host.Get(f.Key(m), "Overview").Display == "明确人工编写的简介" && f.Transport.Calls == 0);
            f.Submit([m], ["Overview"]); await f.Drain();
            check("automatic empty-source skipping retains explicit manual display without model work", f.Host.Get(f.Key(m), "Overview").Display == "明确人工编写的简介" && f.Transport.Calls == 0 && f.Content.ReadField(m, "Overview")!.Availability == "empty");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "direct-term-work")))
        {
            f.Add("untouched-film"); var settings = f.Settings.Read();
            var first = f.Work.SubmitTerms("direct-a", "dictionary", settings.Revision, ["独立词条"]);
            var shared = f.Work.SubmitTerms("direct-b", "dictionary", settings.Revision, ["独立词条"]);
            check("direct dictionary submissions share the same active term work", first.WorkIds.Single() == shared.WorkIds.Single());
            f.Work.CancelBatch(first.Id); await f.Drain();
            check("standalone dictionary demand survives another batch cancellation without host writes", f.Transport.Calls == 1 && f.Host.Writes == 0 && f.Work.Find(shared.WorkIds.Single())!.State == "completed");
            f.Work.SubmitTerms("direct-c", "dictionary", settings.Revision, ["独立词条"]); await f.Drain();
            check("standalone dictionary reuse sends no second request", f.Transport.Calls == 1 && f.Host.Writes == 0);
            var test = f.Work.SubmitTerms("test-a", "connection_test", settings.Revision, ["接続確認"], true); await f.Drain();
            var replay = f.Work.SubmitTerms("test-a", "connection_test", settings.Revision, ["接続確認"], true); await f.Drain();
            check("connection test is a durable idempotent single attempt in the only queue", f.Transport.Calls == 2 && f.Host.Writes == 0 && test.WorkIds.Single() == replay.WorkIds.Single());
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "observe-stale-owned-snapshot")))
        {
            var m = f.Add("stale-read", "", "共享词"); f.Submit([m], ["Genres"]); await f.Drain(); await f.Observer.ObserveAllAsync();
            var s = f.Settings.Read(); f.Settings.Save(s.Revision, "en", s.Fields, s.ManagedLibraries, true); var batch = f.Submit([m], ["Genres"]);
            f.Host.BeforeReadReturn = async snapshot =>
            {
                if (snapshot.Field != "Genres") return;
                f.Host.BeforeReadReturn = null; await f.Drain();
            };
            await f.Observer.ObserveAllAsync();
            check("stale host snapshot of our verified predecessor cannot revoke a completed replacement", f.Work.Find(batch.WorkIds.Single())!.State == "completed" && f.Content.ReadField(m, "Genres")!.ControlRevision == 0 && f.Text("SELECT mode FROM display_intents WHERE field='Genres'") == "translated");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "observe-prepared-replacement")))
        {
            var m = f.Add("replacement-gap", "原简介", "共享词"); f.Submit([m]); await f.Drain();
            await f.Observer.ObserveAllAsync(); var s = f.Settings.Read(); f.Settings.Save(s.Revision, "en", s.Fields, s.ManagedLibraries, true);
            f.Host.BeforeWrite = _ => f.Observer.ObserveAllAsync(); var batch = f.Submit([m]); await f.Drain();
            check("observer preserves verified predecessor display between new intent and host write", batch.WorkIds.All(id => f.Work.Find(id)!.State == "completed") && f.Count("SELECT SUM(control_revision) FROM media_fields") == 0);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "genre-A-B-A")))
        {
            var m = f.Add("genre-source", "", "词A"); var original = f.Content.ReadField(m, "Genres")!; var batch = f.Submit([m], ["Genres"]);
            await f.Executor.RunNextAsync(); var child = f.Work.Dependencies(batch.WorkIds[0]).Single();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var response = new TaskCompletionSource<GenerationReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Handler = async (_, send, _) => { send(); entered.SetResult(); return await response.Task; };
            var running = f.Executor.RunNextAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Host.Change(f.Key(m), "Genres", x => x with { Original = "[\"词B\"]", Display = "[\"词B\"]" }); await f.Observer.ObserveAllAsync();
            f.Host.Change(f.Key(m), "Genres", x => x with { Original = "[\"词A\"]", Display = "[\"词A\"]" }); await f.Observer.ObserveAllAsync();
            response.SetResult(new("词A译文", HttpStatus: 200)); await running;
            check("orphaned in-flight term result is saved as history under revoked demand", f.Work.Find(child.Id)!.State == "completed" && f.Text("SELECT completion_reason FROM work_items WHERE id='" + child.Id + "'") == "history_only" && f.Host.Writes == 0);
            await f.Drain(); check("new genre A demand reuses term history under new execution authority", f.Transport.Calls == 1 && f.Content.ReadField(m, "Genres")!.SourceId == original.SourceId && JsonSerializer.Deserialize<string[]>(f.Host.Get(f.Key(m), "Genres").Display)!.Single() == "词A译文");
        }
    }
}
