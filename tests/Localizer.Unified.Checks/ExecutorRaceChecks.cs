using System.Text.Json;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class ExecutorRaceChecks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        using (var f = new ExecutorFixture(Path.Combine(root, "save-only-race")))
        {
            var m = f.Add("manual"); var batch = f.Submit([m], ["Overview"]); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reply = new TaskCompletionSource<GenerationReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Handler = async (input, send, _) => { send(); entered.SetResult(); return await reply.Task; };
            var run = f.Executor.RunNextAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = f.Content.ReadField(m, "Overview")!; var manual = f.Content.SaveManual(before, "zh-Hans", "只保存的人工作品", false, before.Content, "manual-race");
            reply.SetResult(new("晚到的模型译文", HttpStatus: 200)); await run; await f.Drain();
            check("in-flight save-only preserves display and does not select the late model", f.Host.Writes == 0 && f.Content.Selected(before.SourceId, "zh-Hans") is null && f.Host.Get(f.Key(m), "Overview").Display == before.Content);
            check("late usable result remains in immutable history", f.Content.History(before.SourceId, "zh-Hans", 0, 100).Length == 2 && f.Work.Find(batch.WorkIds[0])!.State == "completed" && f.Text("SELECT completion_reason FROM work_items LIMIT 1") == "history_only");
            var current = f.Content.ReadField(m, "Overview")!; f.Content.Choose(current, "zh-Hans", manual.Id, f.Host.Get(f.Key(m), "Overview").Display);
            f.Submit([m], ["Overview"], operation: "display"); await f.Drain();
            check("choosing a saved manual version uses the same executor with no HTTP", f.Transport.Calls == 1 && f.Host.Get(f.Key(m), "Overview").Display == manual.Text);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "source-A-B-A")))
        {
            var m = f.Add("source"); var a = f.Content.ReadField(m, "Overview")!; var batch = f.Submit([m], ["Overview"]);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var response = new TaskCompletionSource<GenerationReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Handler = async (_, send, _) => { send(); entered.SetResult(); return await response.Task; };
            var running = f.Executor.RunNextAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Host.Change(f.Key(m), "Overview", x => x with { Original = "原文 B", Display = "原文 B" }); await f.Observer.ObserveAllAsync();
            f.Host.Change(f.Key(m), "Overview", x => x with { Original = a.Content, Display = a.Content }); await f.Observer.ObserveAllAsync();
            var back = f.Content.ReadField(m, "Overview")!; response.SetResult(new("有效的 A 译文", HttpStatus: 200)); await running;
            check("A B A keeps monotonically newer authority and old response history-only", a.SourceId == back.SourceId && back.SourceRevision == a.SourceRevision + 2 && f.Host.Writes == 0 && f.Work.Find(batch.WorkIds[0])!.State == "completed");
            await f.Drain(); check("new A permission can reuse the saved A result without another request", f.Transport.Calls == 1 && f.Host.Get(f.Key(m), "Overview").Display == "有效的 A 译文" && f.Content.Selected(a.SourceId, "zh-Hans") is not null);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "language-race")))
        {
            var m = f.Add("language"); var first = f.Submit([m], ["Name"]); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var response = new TaskCompletionSource<GenerationReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Handler = async (_, send, _) => { send(); entered.SetResult(); return await response.Task; };
            var running = f.Executor.RunNextAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var s = f.Settings.Read(); f.Settings.Save(s.Revision, "en", s.Fields, s.ManagedLibraries, true); var en = f.Submit([m], ["Name"]);
            s = f.Settings.Read(); f.Settings.Save(s.Revision, "zh-Hans", s.Fields, s.ManagedLibraries, true); var zh = f.Submit([m], ["Name"]);
            response.SetResult(new("最后中文", HttpStatus: 200)); await running;
            check("rapid language switches never revive the first target generation", f.Host.Writes == 0 && f.Work.Find(en.WorkIds[0])!.State == "expired" && f.Text("SELECT completion_reason FROM work_items WHERE id='" + first.WorkIds[0] + "'") == "history_only");
            await f.Drain(); check("latest language work adopts cache under its own target generation", f.Work.Find(zh.WorkIds[0])!.State == "completed" && f.Transport.Calls == 1 && f.Host.Get(f.Key(m), "Name").Display == "LAB · 最后中文");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "external-edit-race")))
        {
            var m = f.Add("external"); var batch = f.Submit([m], ["Overview"]); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var response = new TaskCompletionSource<GenerationReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Handler = async (_, send, _) => { send(); entered.SetResult(); return await response.Task; };
            var running = f.Executor.RunNextAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Host.Change(f.Key(m), "Overview", x => x with { Display = "宿主人工编辑" }); await f.Observer.ObserveAllAsync();
            response.SetResult(new("晚到译文", HttpStatus: 200)); await running; await f.Drain();
            check("observed external edit advances control and blocks in-flight adoption", f.Host.Writes == 0 && f.Host.Get(f.Key(m), "Overview").Display == "宿主人工编辑" && f.Content.Selected(f.Content.ReadField(m, "Overview")!.SourceId, "zh-Hans") is null);
            check("external edit protection stays separate from translation review", f.Text("SELECT state FROM display_intents WHERE field='Overview'") == "protected" && f.Work.Find(batch.WorkIds[0])!.State == "completed");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "write-boundary-race")))
        {
            var m = f.Add("write"); var batch = f.Submit([m], ["Overview"]);
            f.Host.BeforeWrite = command => { f.Host.Change(command.Key, command.Field, x => x with { Original = "原文 B", Display = "原文 B" }); return Task.CompletedTask; };
            await f.Drain(); check("source change inside host write boundary prevents stale display", f.Host.Writes == 0 && f.Host.Get(f.Key(m), "Overview").Display == "原文 B" && f.Work.Find(batch.WorkIds[0])!.State == "expired");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "shared-batch-cancel")))
        {
            var m = f.Add("shared"); var one = f.Submit([m], ["Overview"]); var two = f.Submit([m], ["Overview"]);
            check("two batches reference one active field work", one.WorkIds[0] == two.WorkIds[0]);
            f.Work.CancelBatch(one.Id); await f.Drain(); check("canceling one batch preserves another batch demand", f.Transport.Calls == 1 && f.Work.Find(two.WorkIds[0])!.State == "completed");
            var more = f.Add("cancelled"); var cancelled = f.Submit([more], ["Overview"]); f.Work.CancelBatch(cancelled.Id); await f.Drain();
            check("canceling the last demand prevents any request", f.Transport.Calls == 1 && f.Work.Find(cancelled.WorkIds[0])!.State == "cancelled");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "original-restore")))
        {
            var m = f.Add("restore"); f.Submit([m], ["Overview"]); await f.Drain();
            var field = f.Content.ReadField(m, "Overview")!; var translated = f.Host.Get(f.Key(m), "Overview").Display;
            f.Content.RestoreOriginal(field, translated); var restored = f.Submit([m], ["Overview"], operation: "restore"); await f.Drain();
            check("restore original uses durable display writes and an effective restore link", f.Host.Get(f.Key(m), "Overview").Display == field.Content && f.Count("SELECT COUNT(*) FROM display_writes WHERE action='restore' AND restores_write_id IS NOT NULL AND state='verified'") == 1 && f.Transport.Calls == 1);
            check("one-shot restore does not imply permanent keep-original preference", f.Count("SELECT keep_original FROM display_intents WHERE field='Overview'") == 0 && f.Work.Find(restored.WorkIds[0])!.State == "completed");
            f.Submit([m], ["Overview"]); await f.Drain(); check("explicit fill after one-shot restore reuses the selected translation", f.Transport.Calls == 1 && f.Host.Get(f.Key(m), "Overview").Display == translated);
            f.Submit([m], ["Overview"], force: true); await f.Drain(); check("explicit retranslation adds a new version through the same flow", f.Transport.Calls == 2 && f.Content.History(field.SourceId, "zh-Hans", 0, 100).Length == 2);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "first-baseline")))
        {
            var old = f.Add("historical"); f.AddHostOnly("unregistered-historical", "历史简介", "历史词");
            f.Database.Write((db, tx) => { Execute(db, tx, "UPDATE settings SET baseline_established=0; UPDATE managed_libraries SET baseline_established=0"); return true; });
            await f.Observer.ObserveAllAsync(); await f.Drain();
            check("first observation creates no historical backfill", f.Settings.Read().BaselineEstablished && f.Transport.Calls == 0 && f.Count("SELECT COUNT(*) FROM work_items") == 0);
            var added = f.AddHostOnly("new", "新简介", "新词"); await f.Observer.ObserveAllAsync(); await f.Drain();
            check("new item admission uses the same queue without backfilling old missing items", f.Transport.Calls == 3 && f.Host.Get(added.Key, "Overview").Display == "中文 新简介" && f.Host.Get(f.Key(old), "Overview").Display == "山を歩く。");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "cutover-reconciliation")))
        {
            var m = f.Add("cutover");
            f.Database.Write((db, tx) => { Execute(db, tx, "UPDATE settings SET baseline_established=0; UPDATE managed_libraries SET baseline_established=0,imported_baseline=1; INSERT INTO import_runs(id,manifest_hash,state,created_utc) VALUES('synthetic','synthetic','completed','2026-10-26T00:00:00Z')"); return true; });
            f.Host.Change(f.Key(m), "Overview", x => x with { Original = "切换期间真实新简介", Display = "切换期间真实新简介" });
            await f.Observer.ObserveMediaAsync(new(f.Key(m), f.Host.Get(f.Key(m), "Overview").MediaPath)); await f.Drain();
            check("cutover changes are durable while the first full baseline is pending", f.Transport.Calls == 0 && f.Count("SELECT COUNT(*) FROM work_items WHERE state='waiting'") == 1);
            await f.Observer.ObserveAllAsync(); f.Clock = f.Clock.AddSeconds(2); await f.Drain();
            check("cutover reconciliation processes only changed content after baseline", f.Transport.Calls == 1 && f.Host.Get(f.Key(m), "Overview").Display == "中文 切换期间真实新简介" && f.Host.Get(f.Key(m), "Name").Display.StartsWith("LAB · 山の旅", StringComparison.Ordinal));
        }
    }
}
