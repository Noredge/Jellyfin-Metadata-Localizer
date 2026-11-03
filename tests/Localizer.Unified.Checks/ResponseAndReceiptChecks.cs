using System.Text.Json;
using Localizer.Core;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class ResponseAndReceiptChecks
{
    private static async Task Until(Func<bool> condition, int milliseconds = 12000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition()) { if (DateTime.UtcNow >= end) throw new TimeoutException("response persistence condition"); await Task.Delay(25); }
    }
    internal static async Task Run(string root, Action<string, bool> check)
    {
        using (var f = new ExecutorFixture(Path.Combine(root, "response-name-slots")))
        {
            var media = f.Add("Ai", "Aiの物語"); var config = new ConfigurationStore(f.Database); var id = Id();
            config.SavePerson(id, 0, "Ai", "爱", null, []);
            var template = new PersonTextTemplate(1, "关于爱情的故事，__JML_PERSON_A__归来", [new("__JML_PERSON_A__", "Ai", "爱", id)]);
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(template.Frozen(), PersonTemplate: template)); };
            f.Submit([media], ["Overview"]); await f.Drain();
            var version = f.Content.Selected(f.Content.ReadField(media, "Overview")!.SourceId, "zh-Hans")!;
            config.SavePerson(id, 1, "Ai", "小爱", null, []); f.Submit([media], ["Overview"]); await f.Drain();
            check("name slots change a person without corrupting an ordinary word", f.Host.Get(f.Key(media), "Overview").Display == "关于爱情的故事，小爱归来" && f.Transport.Calls == 1);
            check("name template rendering retains immutable original translation", f.Content.Selected(version.SourceId, "zh-Hans")!.Text == "关于爱情的故事，爱归来" && f.Count("SELECT COUNT(*) FROM translation_versions") == 1);
            config.DeletePerson(id, 2); f.Submit([media], ["Overview"]); await f.Drain();
            check("deleted person rule falls back only inside its protected slot", f.Host.Get(f.Key(media), "Overview").Display == "关于爱情的故事，Ai归来" && f.Transport.Calls == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "response-legacy-name")))
        {
            var media = f.Add("Ai", "Aiの物語"); var config = new ConfigurationStore(f.Database); var id = Id();
            config.SavePerson(id, 0, "Ai", "爱", null, []);
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply("关于爱情的故事，爱归来")); };
            f.Submit([media], ["Overview"]); await f.Drain();
            config.SavePerson(id, 1, "Ai", "小爱", null, []);
            var work = f.Submit([media], ["Overview"]).WorkIds.Single(); await f.Drain();
            check("legacy plain text remains intact and requests explicit repair after a name edit", f.Host.Get(f.Key(media), "Overview").Display == "关于爱情的故事，爱归来"
                && f.Work.Find(work) is { State: "failed", Error: "person_template_required" } && f.Transport.Calls == 1 && f.Count("SELECT COUNT(*) FROM translation_versions") == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "response-identical-names")))
        {
            var media = f.Add("Alice Bob"); var config = new ConfigurationStore(f.Database); var alice = Id(); var bob = Id();
            config.SavePerson(alice, 0, "Alice", "同名", null, []); config.SavePerson(bob, 0, "Bob", "同名", null, []);
            var template = new PersonTextTemplate(1, "__JML_PERSON_A__与__JML_PERSON_B__同游", [new("__JML_PERSON_A__", "Alice", "同名", alice), new("__JML_PERSON_B__", "Bob", "同名", bob)]);
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(template.Frozen(), PersonTemplate: template)); };
            f.Submit([media], ["Name"]); await f.Drain();
            config.SavePerson(alice, 1, "Alice", "甲", null, []); config.SavePerson(bob, 1, "Bob", "乙", null, []);
            f.Submit([media], ["Name"]); await f.Drain();
            check("identical frozen names retain separate identities in verified slots", f.Host.Get(f.Key(media), "Name").Display == "LAB · 甲与乙同游" && f.Transport.Calls == 1);
            config.SavePerson(alice, 2, "Alicia", "丙", null, ["Alice"]); f.Submit([media], ["Name"]); await f.Drain();
            check("renamed rule resolves by frozen person identity", f.Host.Get(f.Key(media), "Name").Display == "LAB · 丙与乙同游" && f.Transport.Calls == 1);
        }
        foreach (var template in new[] {
            new PersonTextTemplate(1, "__JML_PERSON_A____JML_PERSON_A__", [new("__JML_PERSON_A__", "Ai", "爱")]),
            new PersonTextTemplate(1, "__JML_PERSON_B__", [new("__JML_PERSON_A__", "Ai", "爱")]),
            new PersonTextTemplate(1, "ordinary", [new("__JML_PERSON_A__", "Ai", "爱")]) })
        {
            var denied = false; try { template.Frozen(); } catch (PersonTemplateException) { denied = true; }
            check("invalid missing duplicated or mismatched slots fail closed: " + template.Text, denied);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "response-preview-receipt")))
        {
            var media = f.Add("receipt"); var command = Id(); var preview = Id(); var revision = f.Settings.Read().Revision;
            var receipt = f.Work.Submit(command, "fill", revision, [f.Seed(media, "Name")], previewId: preview);
            var count = f.Count("SELECT COUNT(*) FROM work_items");
            f.Database.Write((db, tx) => Execute(db, tx, "DELETE FROM previews"));
            var settings = f.Settings.Read(); f.Settings.Save(settings.Revision, "en", settings.Fields, settings.ManagedLibraries, true);
            f.Work.CancelBatch(receipt.Id); f.Restart(); var wake = f.Database.WorkAvailable.Version;
            var replay = f.Work.TryReplayPreview(command, preview)!;
            check("committed receipt survives preview deletion restart settings change and cancellation", replay.Id == receipt.Id && replay.Cancelled && replay.WorkIds.SequenceEqual(receipt.WorkIds) && f.Count("SELECT COUNT(*) FROM work_items") == count);
            check("read-only receipt replay does not wake executor", f.Database.WorkAvailable.Version == wake);
            var conflict = false; try { f.Work.TryReplayPreview(command, Id()); } catch (InvalidOperationException e) when (e.Message == "SubmissionConflict") { conflict = true; }
            check("command cannot be rebound to another preview", conflict);
            var bad = Id(); var denied = false;
            try { f.Work.Submit(bad, "fill", revision, [f.Seed(media, "Name")], previewId: Id()); } catch (InvalidOperationException e) when (e.Message == "RevisionConflict") { denied = true; }
            check("failed submission commits neither receipt nor preview binding", denied && f.Work.TryReplayPreview(bad, Id()) is null);
            var atomic = Id(); var atomicPreview = Id(); var seed = f.Seed(media, "Name"); var batchCount = f.Count("SELECT COUNT(*) FROM batches");
            denied = false;
            try { f.Work.Submit(atomic, "manual", f.Settings.Read().Revision, [seed with { Field = seed.Field with { SourceRevision = seed.Field.SourceRevision + 1 } }], previewId: atomicPreview); }
            catch (InvalidOperationException e) when (e.Message == "RevisionConflict") { denied = true; }
            check("late submission failure rolls back batch work and preview binding atomically", denied && f.Work.TryReplayPreview(atomic, atomicPreview) is null && f.Count("SELECT COUNT(*) FROM batches") == batchCount);
            var legacy = Id(); var current = f.Settings.Read().Revision;
            var old = f.Work.Submit(legacy, "fill", current, [f.Seed(media, "Overview")]); var legacyPreview = Id();
            var bound = f.Work.TryReplay(legacy, "fill", current, [(media, "Overview")], previewId: legacyPreview);
            check("legacy receipt can atomically gain a verified preview binding", bound?.Id == old.Id && f.Work.TryReplayPreview(legacy, legacyPreview)?.Id == old.Id);
        }
        foreach (var restart in new[] { false, true })
        using (var f = new ExecutorFixture(Path.Combine(root, "response-late-storage-" + restart), timeout: 1000))
        {
            var media = f.Add("late"); var result = new TaskCompletionSource<GenerationReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Handler = (_, send, _) => { send(); return result.Task; };
            f.Submit([media], ["Name"]); await f.Executor.RunNextAsync();
            var before = f.Text("SELECT id||'|'||state||'|'||error_code FROM request_attempts");
            using var writer = f.Database.Open(); using var locked = writer.BeginTransaction(deferred: false);
            result.SetResult(new("晚到结果", Diagnostics: new("content", "usable"), Usage: new(1, 2, 3)));
            var directory = Path.Combine(f.Root, "late-responses");
            await Until(() => Directory.Exists(directory) && Directory.GetFiles(directory, "*.json").Length == 1);
            await Until(() => f.Executor.BackgroundError is not null);
            var spoolFile = Directory.GetFiles(directory, "*.json").Single(); var spoolBytes = File.ReadAllBytes(spoolFile);
            check("database lock retains durable late result and single send ownership: " + restart, f.Executor.HasOutstandingSend && f.Transport.Calls == 1 && f.Host.Writes == 0);
            if (restart) { f.Executor.Dispose(); await Until(() => !f.Executor.HasOutstandingSend); await Task.Delay(75); }
            locked.Rollback();
            if (restart) f.Restart();
            else await Until(() => !f.Executor.HasOutstandingSend);
            await f.Drain();
            check("late storage recovers without provider resend or automatic display: " + restart, f.Transport.Calls == 1 && f.Host.Writes == 0 && f.Count("SELECT COUNT(*) FROM translation_versions") == 1
                && f.Count("SELECT COUNT(*) FROM request_attempts WHERE late_completed_utc IS NOT NULL AND late_translation_id IS NOT NULL") == 1);
            check("late recovery preserves unknown evidence and clears degraded state: " + restart, before == f.Text("SELECT id||'|'||state||'|'||error_code FROM request_attempts")
                && f.Executor.BackgroundError is null && Directory.GetFiles(directory, "*.json").Length == 0);
            File.WriteAllBytes(spoolFile, spoolBytes); f.Restart(); await f.Drain();
            check("replayed durable copy after commit is idempotent: " + restart, f.Transport.Calls == 1 && f.Count("SELECT COUNT(*) FROM translation_versions") == 1 && Directory.GetFiles(directory, "*.json").Length == 0);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "response-invalid-spool")))
        {
            var directory = Path.Combine(f.Root, "late-responses"); Directory.CreateDirectory(directory); var file = Path.Combine(directory, Id() + ".json"); File.WriteAllText(file, "{broken"); f.Restart();
            var denied = false; try { await f.Executor.RunNextAsync(); } catch (JsonException) { denied = true; }
            check("invalid durable response is retained with degraded health and no sends", denied && File.Exists(file) && f.Transport.Calls == 0 && f.Executor.BackgroundError == "late_history_persistence_failed");
        }
    }
}
