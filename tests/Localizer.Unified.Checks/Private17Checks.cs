using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class Private17Checks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        var signal = new QueueWakeup(); var version = signal.Version;
        var beforeWait = signal.WaitAsync(version, TimeSpan.FromSeconds(8)); signal.Notify();
        check("queue notification interrupts the maximum idle wait", await beforeWait.WaitAsync(TimeSpan.FromSeconds(2)));
        version = signal.Version; signal.Notify();
        check("commit between empty read and wait registration is not lost", await signal.WaitAsync(version, TimeSpan.FromSeconds(8)).WaitAsync(TimeSpan.FromSeconds(2)));
        version = signal.Version;
        var first = signal.WaitAsync(version, TimeSpan.FromSeconds(8)); var second = signal.WaitAsync(version, TimeSpan.FromSeconds(8));
        for (var i = 0; i < 1000; i++) signal.Notify();
        check("burst notifications coalesce without leaving extra wake tokens", await first && await second && !await signal.WaitAsync(signal.Version, TimeSpan.Zero));
        check("quiet queue wait has a bounded timeout", !await signal.WaitAsync(signal.Version, TimeSpan.FromMilliseconds(20)));
        using (var cancellation = new CancellationTokenSource())
        {
            version = signal.Version; var cancelled = signal.WaitAsync(version, TimeSpan.FromSeconds(8), cancellation.Token);
            var other = signal.WaitAsync(version, TimeSpan.FromSeconds(8)); cancellation.Cancel(); var stopped = false;
            try { await cancelled; } catch (OperationCanceledException) { stopped = true; }
            signal.Notify(); check("cancellation releases one wait without poisoning other waiters", stopped && await other);
        }
        var backoff = new QueueIdleBackoff(); var sequence = new List<double>();
        for (var i = 0; i < 8; i++) { sequence.Add(backoff.Delay.TotalMilliseconds); backoff.Advance(); }
        check("empty queue exponentially backs off from 250ms to an 8s ceiling", sequence.SequenceEqual(new double[] { 250, 500, 1000, 2000, 4000, 8000, 8000, 8000 }));
        backoff.Reset(); check("new work resets idle delay immediately", backoff.Delay == TimeSpan.FromMilliseconds(250));

        using (var f = new ExecutorFixture(Path.Combine(root, "p17-commit-boundary")))
        {
            signal = f.Database.WorkAvailable; version = signal.Version;
            f.Database.Write((db, tx) => Scalar<long>(db, tx, "SELECT 1"));
            check("empty/read-only write transaction does not notify itself", signal.Version == version);
            var pending = signal.WaitAsync(version, TimeSpan.FromSeconds(8));
            f.Database.Write((db, tx) =>
            {
                Execute(db, tx, "INSERT INTO runtime_state VALUES('p17-commit','committed')");
                check("notification is withheld while producer transaction is open", !pending.IsCompleted); return true;
            });
            var notified = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            check("notified reader observes durable committed state", notified && f.Text("SELECT value FROM runtime_state WHERE key='p17-commit'") == "committed");
            version = signal.Version;
            try { f.Database.Write<bool>((db, tx) => { Execute(db, tx, "INSERT INTO runtime_state VALUES('p17-rollback','discard')"); throw new IOException("synthetic rollback"); }); }
            catch (IOException) { }
            check("rolled-back producer changes neither queue signal nor durable rows", signal.Version == version && f.Count("SELECT COUNT(*) FROM runtime_state WHERE key='p17-rollback'") == 0);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p17-producer-wake")))
        {
            var media = f.Add("producer", "山を歩く。"); signal = f.Database.WorkAvailable;
            async Task<bool> Wakes(Action write)
            { var wait = signal.WaitAsync(signal.Version, TimeSpan.FromSeconds(8)); write(); return await wait.WaitAsync(TimeSpan.FromSeconds(2)); }
            BatchReceipt? receipt = null; var command = Id(); var revision = f.Settings.Read().Revision;
            check("manual submission wakes idle worker after commit", await Wakes(() => receipt = f.Work.Submit(command, "manual", revision, [f.Seed(media, "Name")])));
            var replayVersion = signal.Version; var replay = f.Work.TryReplay(command, "manual", revision, [(media, "Name")]);
            check("pure submission lookup does not create a wake event", replay?.Id == receipt!.Id && signal.Version == replayVersion);
            f.Work.Submit(command, "manual", revision, [f.Seed(media, "Name")]);
            check("idempotent submission replay does not create a wake event", signal.Version == replayVersion);
            check("automatic source admission uses the same commit wake", await Wakes(() => f.Work.EnqueueAutomatic("added", f.Seed(media, "Overview"))));
            check("dictionary term generation uses the same commit wake", await Wakes(() => f.Work.SubmitTerms(Id(), "dictionary", f.Settings.Read().Revision, ["p17-term"])));
            var current = f.Content.ReadField(media, "Name")!;
            check("saved display intent wakes worker even without direct queue submission", await Wakes(() => f.Content.SaveManual(current, "zh-Hans", "人工标题", true, f.Host.Get(f.Key(media), "Name").Display, "p17-intent")));
            var settings = f.Settings.Read();
            check("global language change wakes the executor after settings commit", await Wakes(() => f.Settings.Save(settings.Revision, "en", settings.Fields, settings.ManagedLibraries, true)));
            check("cancelling a batch wakes worker to recheck remaining demand", await Wakes(() => f.Work.CancelBatch(receipt!.Id)));
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p17-retry-wake")))
        {
            var media = f.Add("retry"); var receipt = f.Submit([media], ["Overview"]);
            f.Transport.Handler = (_, _, _) => Task.FromResult(new GenerationReply(null, "configuration", NotSent: true)); await f.Drain();
            signal = f.Database.WorkAvailable; var pending = signal.WaitAsync(signal.Version, TimeSpan.FromSeconds(8)); f.Work.Retry(receipt.WorkIds.Single());
            check("explicit failed-work retry interrupts idle wait", await pending.WaitAsync(TimeSpan.FromSeconds(2)) && f.Work.Find(receipt.WorkIds.Single())!.State == "queued");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p17-deadlines")))
        {
            var media = f.Add("due"); var receipt = f.Submit([media], ["Overview"]); var id = receipt.WorkIds.Single();
            void Schedule(string state, double seconds, double? leaseSeconds = null) => f.Database.Write((db, tx) => Execute(db, tx,
                "UPDATE work_items SET state=$s,next_run_utc=$n,lease_until_utc=$lease WHERE id=$id", ("$s", state), ("$n", f.Clock.AddSeconds(seconds).ToString("O")),
                ("$lease", leaseSeconds is null ? null : f.Clock.AddSeconds(leaseSeconds.Value).ToString("O")), ("$id", id)));
            Schedule("waiting", .35);
            check("scheduled source/retry work bounds the 8s idle delay", Math.Abs(f.Work.NextWakeDelay(TimeSpan.FromSeconds(8)).TotalMilliseconds - 350) < 1);
            Schedule("waiting", .35, 2);
            check("future lease is respected when choosing next wake", f.Work.NextWakeDelay(TimeSpan.FromSeconds(8)) == TimeSpan.FromSeconds(2));
            check("scheduled deadline cannot exceed current backoff ceiling", f.Work.NextWakeDelay(TimeSpan.FromMilliseconds(500)) == TimeSpan.FromMilliseconds(500));
            Schedule("paused", -1);
            check("paused unknown or connection-blocked work does not cause a busy loop", f.Work.NextWakeDelay(TimeSpan.FromSeconds(8)) == TimeSpan.FromSeconds(8));
            var stateBefore = f.Work.Find(id)!; f.Work.NextWakeDelay(TimeSpan.FromSeconds(8));
            check("wake deadline inspection never changes execution state", f.Work.Find(id) == stateBefore);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p17-dependency-deadline")))
        {
            var media = f.Add("dependency", "山を歩く。", "pending-term"); var receipt = f.Submit([media], ["Genres"]); await f.Executor.RunNextAsync();
            var child = f.Work.Dependencies(receipt.WorkIds.Single()).Single();
            f.Database.Write((db, tx) => Execute(db, tx, "UPDATE work_items SET state='waiting',next_run_utc=$n WHERE id=$id", ("$n", f.Clock.AddSeconds(3).ToString("O")), ("$id", child.Id)));
            check("blocked parent does not mask the shared child's next deadline", f.Work.NextWakeDelay(TimeSpan.FromSeconds(8)) == TimeSpan.FromSeconds(3));
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p17-fallback")))
        {
            var media = f.Add("fallback"); var receipt = f.Submit([media], ["Overview"]); var id = receipt.WorkIds.Single(); signal = f.Database.WorkAvailable;
            f.Database.Write((db, tx) => Execute(db, tx, "UPDATE work_items SET state='waiting',next_run_utc=$n WHERE id=$id", ("$n", f.Clock.AddHours(1).ToString("O")), ("$id", id)));
            var pending = signal.WaitAsync(signal.Version, TimeSpan.FromMilliseconds(30));
            using (var db = f.Database.Open()) Execute(db, null, "UPDATE work_items SET state='queued',next_run_utc=NULL WHERE id=$id", ("$id", id));
            check("bounded fallback discovers a writer outside the in-process notifier", !await pending && f.Work.Claim("fallback")?.Id == id);
            check("queue hints preserve the unified schema and foreign keys", f.Text("PRAGMA integrity_check") == "ok" && f.Count("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p17-idle-to-task")))
        {
            var media = f.Add("idle-task"); signal = f.Database.WorkAvailable;
            for (var i = 0; i < 6; i++) { f.Work.Claim("empty"); }
            var versionBeforeCheck = signal.Version; check("repeated empty claims cannot self-wake the idle scheduler", !await f.Executor.RunNextAsync() && signal.Version == versionBeforeCheck);
            var wait = signal.WaitAsync(versionBeforeCheck, f.Work.NextWakeDelay(TimeSpan.FromSeconds(8)));
            var receipt = f.Submit([media], ["Overview"]); await wait.WaitAsync(TimeSpan.FromSeconds(2)); await f.Drain();
            check("new durable task runs promptly from maximum idle wait", f.Work.Find(receipt.WorkIds.Single())!.State == "completed" && f.Transport.Calls == 1);
            check("idle notification does not introduce extra model attempts", f.Count("SELECT COUNT(*) FROM request_attempts") == 1 && f.Transport.MaximumActive == 1);
        }
    }
}
