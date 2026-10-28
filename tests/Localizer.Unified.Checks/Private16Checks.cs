using System.Text.Json;
using Localizer.Unified;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

internal static class Private16Checks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-readmission")))
        {
            var media = f.Add("returning", "山を歩く。"); var key = f.Key(media);
            var title = f.Content.ReadField(media, "Name")!;
            var manual = f.Content.SaveManual(title, "zh-Hans", "人工标题", true, f.Host.Get(key, "Name").Display, "returning-manual");
            using (var db = f.Database.Open()) Execute(db, null, "UPDATE media_items SET path_evidence=$e WHERE id=$m", ("$e", "{\"confirmed\":\"retained\"}"), ("$m", media));
            var old = f.Submit([media], ["Overview"]);
            foreach (var field in new[] { "Name", "Overview", "Genres" }) f.Host.Change(key, field, x => x with { Present = false });
            await f.Observer.ObserveAllAsync();
            foreach (var field in new[] { "Name", "Overview", "Genres" }) f.Host.Change(key, field, x => x with { Present = true });
            await f.Observer.ObserveAllAsync();
            check("returning same identity and path becomes visible and fillable", new ReadModel(f.Database).Movies("", null, "all", 0, 30).Total == 1 && new FillPlanner(f.Database).Read([media], ["Overview"]).Candidates.Length == 0 && f.Count("SELECT COUNT(*) FROM work_items WHERE state='queued'") > 0);
            check("readmission preserves evidence manual selection and revoked old work", f.Text("SELECT path_evidence FROM media_items") == "{\"confirmed\":\"retained\"}" && f.Content.Selected(title.SourceId, "zh-Hans")!.Id == manual.Id && f.Work.Find(old.WorkIds.Single())!.State == "expired");
            await f.Drain();
            check("returning film uses cached manual body without duplicate title generation", f.Host.Get(key, "Name").Display == "LAB · 人工标题" && f.Transport.Inputs.All(x => x.Field != "Name"));
            f.Content.MarkRemoved(media);
            foreach (var field in new[] { "Name", "Overview", "Genres" }) f.Host.Change(key, field, x => x with { MediaPath = "C:\\other\\returning.mp4" });
            await f.Observer.ObserveAllAsync();
            check("different path cannot reactivate an old identity", f.Count("SELECT present FROM media_items") == 0 && f.Content.ReadField(media, "Name")!.Availability == "unreadable");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-local-recovery")))
        {
            var media = f.Add("local-recovery"); var batch = f.Submit([media], ["Overview"]);
            f.Host.BeforeReadReturn = _ => throw new SqliteException("Synthetic SQLITE_BUSY", 5);
            try { await f.Executor.RunNextAsync(); } catch (SqliteException) { }
            check("local executor exception releases the running lease with diagnostic", f.Work.Find(batch.WorkIds.Single()) is { State: "waiting", Error: "executor_database_error" } && f.Transport.Calls == 0);
            f.Host.BeforeReadReturn = null; f.Clock = f.Clock.AddSeconds(2); await f.Drain();
            check("transient local failure resumes without a process restart", f.Work.Find(batch.WorkIds.Single())!.State == "completed" && f.Transport.Calls == 1);
        }
        foreach (var stage in new[] { "request.prepared", "request.received", "request.committed" })
        {
            using var f = new ExecutorFixture(Path.Combine(root, "p16-recovery-" + stage));
            var media = f.Add(stage); var batch = f.Submit([media], ["Overview"]); f.Executor.Dispose(); var fail = true;
            using var executor = new UnifiedExecutor(f.Work, f.Host, f.Transport, (point, _) => { if (fail && point == stage) { fail = false; throw new IOException("Synthetic local persistence interruption"); } });
            try { await executor.RunNextAsync(); } catch (IOException) { }
            f.Clock = f.Clock.AddSeconds(2);
            for (var i = 0; i < 10 && await executor.RunNextAsync(); i++) { }
            if (stage == "request.received")
            {
                var blocked = false; try { f.Work.Retry(batch.WorkIds.Single()); } catch (InvalidOperationException error) when (error.Message == "UnknownOutcomeConsentRequired") { blocked = true; }
                check("after-send interruption stays unknown and never silently resends", f.Transport.Calls == 1 && f.Work.Find(batch.WorkIds.Single())!.State == "paused" && f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='unknown'") == 1 && blocked);
            }
            else check("local recovery at " + stage + " uses persisted send boundary", f.Work.Find(batch.WorkIds.Single())!.State == "completed" && f.Transport.Calls == 1 && (stage != "request.prepared" || f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='not_sent' AND error_code='interrupted_before_send'") == 1));
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-fill-retry")))
        {
            var media = f.Add("retry-terms", "山を歩く。", "good-term", "bad-term");
            f.Transport.Handler = (input, send, _) => { send(); return Task.FromResult(input.Text == "bad-term" ? new GenerationReply(null, "invalid_output", HttpStatus: 200) : FakeUnifiedTransport.Reply(input)); };
            f.Work.Submit(Id(), "fill", f.Settings.Read().Revision, [f.Seed(media, "Genres")]); await f.Drain(); var before = f.Transport.Calls;
            f.Transport.Handler = null;
            var retry = f.Work.Submit(Id(), "fill", f.Settings.Read().Revision, [f.Seed(media, "Genres")]); await f.Drain();
            check("confirmed fill retries the failed dependency exactly once", f.Transport.Calls == before + 1 && f.Work.Find(retry.WorkIds.Single())!.State == "completed");
            check("refill retains failed attempt and reuses every successful term", f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='failed' AND error_code='invalid_output'") == 1 && f.Transport.Inputs.Count(x => x.Text == "good-term") == 1 && f.Transport.Inputs.Count(x => x.Text == "bad-term") == 2);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-fill-unknown")))
        {
            var media = f.Add("unknown-term", "山を歩く。", "uncertain");
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "network_uncertain", OutcomeUnknown: true)); };
            f.Work.Submit(Id(), "fill", f.Settings.Read().Revision, [f.Seed(media, "Genres")]); await f.Drain();
            var next = f.Work.Submit(Id(), "fill", f.Settings.Read().Revision, [f.Seed(media, "Genres")]); await f.Drain();
            check("fill never bypasses unknown dependency consent", f.Transport.Calls == 1 && next.WorkIds.All(id => f.Work.Find(id)!.State is "failed" or "paused") && f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='unknown' AND uncertainty_acknowledged=0") == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-fill-bounded")))
        {
            var media = f.Add("bounded", "山を歩く。", "always-fails");
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "invalid_output")); };
            f.Work.Submit(Id(), "fill", f.Settings.Read().Revision, [f.Seed(media, "Genres")]); await f.Drain();
            var retry = f.Work.Submit(Id(), "fill", f.Settings.Read().Revision, [f.Seed(media, "Genres")]); await f.Drain();
            check("one explicit fill retries a failed word only once even if it fails again", f.Transport.Calls == 2 && f.Work.Find(retry.WorkIds.Single())!.State == "failed");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-shared-fill-bounded")))
        {
            var first = f.Add("shared-first", "山を歩く。", "always-fails"); var second = f.Add("shared-second", "海を歩く。", "always-fails");
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "invalid_output")); };
            f.Work.Submit(Id(), "fill", f.Settings.Read().Revision, [f.Seed(first, "Genres"), f.Seed(second, "Genres")]); await f.Drain();
            check("shared failure is not retried once per parent in the original fill", f.Transport.Calls == 1);
            f.Work.Submit(Id(), "fill", f.Settings.Read().Revision, [f.Seed(first, "Genres"), f.Seed(second, "Genres")]); await f.Drain();
            check("one new fill retries a shared failed word once across all films", f.Transport.Calls == 2);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-incremental")))
        {
            f.Host.HintsEnabled = true; var media = f.Add("incremental", "山を歩く。", "common");
            await f.Observer.ObserveAllAsync(); var reads = f.Host.FieldReads;
            var stamp = f.Text("SELECT observed_utc FROM media_fields WHERE field='Overview'");
            await f.Observer.ObserveAllAsync();
            check("unchanged inventory skips all trusted field reads and row timestamp writes", f.Host.FieldReads == reads && f.Text("SELECT observed_utc FROM media_fields WHERE field='Overview'") == stamp);
            f.Host.Change(f.Key(media), "Overview", x => x with { Original = "海を歩く。", Display = "海を歩く。" }); await f.Observer.ObserveAllAsync();
            check("source fingerprint changes admit only the changed field", f.Host.FieldReads == reads + 3 && f.Work.List().Length == 1 && f.Work.List()[0].Field == "Overview");
            reads = f.Host.FieldReads; await f.Observer.ObserveMediaAsync(new(f.Key(media), f.Host.Get(f.Key(media), "Name").MediaPath));
            check("explicit events bypass observation hints", f.Host.FieldReads == reads + 3);
            reads = f.Host.FieldReads; f.Clock = f.Clock.AddMinutes(16); await f.Observer.ObserveAllAsync();
            check("periodic complete reconciliation still checks unchanged sources", f.Host.FieldReads == reads + 3);
            var settings = f.Settings.Read(); f.Settings.Save(settings.Revision, settings.Language, settings.Fields, settings.ManagedLibraries, true);
            reads = f.Host.FieldReads; await f.Observer.ObserveAllAsync();
            check("settings changes invalidate cached observation context", f.Host.FieldReads == reads + 3);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-legacy-json")))
        {
            var item = f.AddHostOnly("legacy-json", "山を歩く。", "共通");
            var media = f.Content.RegisterMedia(item.Key, item.MediaPath, "{}");
            using (var db = f.Database.Open()) Execute(db, null,
                "INSERT INTO source_versions(id,kind,owner_id,content,content_hash,created_utc) VALUES($id,'Genres',$m,$c,$h,$n)",
                ("$id", Id()), ("$m", media), ("$c", "[\"共通\"]"), ("$h", ContentStore.GenreFingerprint(["共通"])), ("$n", Now()));
            f.Observe(media, "Genres");
            using (var db = f.Database.Open()) Execute(db, null, "UPDATE media_fields SET observed_utc='2000-01-01T00:00:00Z' WHERE media_id=$m", ("$m", media));
            f.Observe(media, "Genres");
            check("equivalent legacy Unicode JSON does not rewrite observations or source bytes", f.Text("SELECT observed_utc FROM media_fields") == "2000-01-01T00:00:00Z" && f.Content.ReadField(media, "Genres")!.Content == "[\"共通\"]");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-auto-batches")))
        {
            f.AddHostOnly("first", "山を歩く。", "shared"); f.AddHostOnly("second", "海を歩く。", "shared");
            await f.Observer.ObserveAllAsync(); await f.Drain();
            using var summary = JsonDocument.Parse(JsonSerializer.Serialize(new ReadModel(f.Database).Overview()));
            var batches = summary.RootElement.GetProperty("Batches");
            check("one discovery pass groups automatic films into one recent timed task", batches.GetArrayLength() == 1 && batches[0].GetProperty("kind").GetString() == "added" && batches[0].GetProperty("total").GetInt64() == 6 && batches[0].GetProperty("created_utc").GetString() is not null);
            check("automatic batch work uses the same executor and shared terms", f.Transport.Calls == 5 && f.Count("SELECT COUNT(*) FROM batch_items") == 6 && f.Count("SELECT COUNT(*) FROM work_items WHERE state='completed'") == 7);
            await f.Observer.ObserveAllAsync(); check("unchanged automatic observation creates no duplicate batch", f.Count("SELECT COUNT(*) FROM batches") == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-auto-off")))
        {
            f.AddHostOnly("auto-off"); await f.Observer.ObserveAllAsync(); var settings = f.Settings.Read();
            f.Settings.Save(settings.Revision, settings.Language, settings.Fields, settings.ManagedLibraries, false); await f.Drain();
            check("automatic summary batches do not become manual execution demand", f.Transport.Calls == 0 && f.Work.List().All(x => x.State == "expired"));
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-auto-cancel")))
        {
            var item = f.AddHostOnly("auto-cancel"); await f.Observer.ObserveAllAsync();
            var media = Hash(JsonSerializer.Serialize(new[] { item.Key.ServerId, item.Key.LibraryId, item.Key.ItemId }));
            var manual = f.Submit([media], ["Name"]); var automatic = f.Text("SELECT id FROM batches WHERE kind='added'")!;
            f.Work.CancelBatch(automatic); await f.Drain();
            check("cancelling automatic batch preserves shared manual demand", f.Transport.Calls == 1 && f.Work.Find(manual.WorkIds.Single())!.State == "completed" && f.Work.List().Where(x => x.Field != "Name").All(x => x.State == "cancelled"));
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p16-parameters")))
        {
            var original = f.Settings.Connection()!; var count = f.Count("SELECT COUNT(*) FROM connection_versions");
            foreach (var invalid in new[] { "{\"MaxOutputTokens\":0}", "{\"MaxOutputTokens\":32769}", "{\"MaxOutputTokens\":64.5}", "{\"MaxOutputTokens\":\"512\"}", "{\"MaxOutputTokens\":null}", "{\"OverviewMaxOutputTokens\":63}", "{\"UseJsonResponseFormat\":\"true\"}", "{\"UseJsonResponseFormat\":null}", "{\"TokenParameter\":\"other\"}", "{\"ReasoningEffort\":\"unsupported\"}", "{\"Store\":1}", "{\"MaxOutputTokens\":64,\"MaxOutputTokens\":512}", "[]" })
            {
                var rejected = false;
                try { f.Settings.SaveConnection(original.Revision, original.Endpoint, original.Model, original.TimeoutMs, original.RequestsPerMinute, original.CredentialReference, invalid); }
                catch (ConnectionParameterException error) { rejected = !string.IsNullOrWhiteSpace(error.Parameter); }
                check("invalid parameter is rejected before save: " + invalid, rejected && f.Count("SELECT COUNT(*) FROM connection_versions") == count);
            }
            var valid = f.Settings.SaveConnection(original.Revision, original.Endpoint, original.Model, original.TimeoutMs, original.RequestsPerMinute, original.CredentialReference,
                "{\"MaxOutputTokens\":64,\"OverviewMaxOutputTokens\":32768,\"TokenParameter\":\"max_completion_tokens\",\"UseJsonResponseFormat\":false,\"ReasoningEffort\":null,\"Store\":true}");
            check("supported boundary values and optional nulls save without HTTP", valid.Revision == original.Revision + 1 && f.Transport.Calls == 0 && f.Count("SELECT COUNT(*) FROM request_attempts") == 0);
            check("private16 retains schema compatibility and database constraints", f.Count("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0 && f.Text("PRAGMA integrity_check") == "ok" && f.Count("PRAGMA user_version") == UnifiedDatabase.SchemaVersion);
        }
    }
}
