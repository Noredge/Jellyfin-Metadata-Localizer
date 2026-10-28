using System.Diagnostics;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class ExecutorFailureChecks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        void Reject(string name, Action action)
        { try { action(); } catch (InvalidOperationException) { check(name, true); return; } check(name, false); }
        using (var f = new ExecutorFixture(Path.Combine(root, "lock-and-retry")))
        {
            var m = f.Add("locked"); f.Host.Change(f.Key(m), "Overview", x => x with { Writable = false, DisplayError = "field_locked" }); var batch = f.Submit([m], ["Overview"]); await f.Drain();
            var failed = f.Work.Find(batch.WorkIds[0])!;
            check("usable translation is saved even when the host field is locked", failed.State == "failed" && failed.IssueKind == "display" && failed.Error == "field_locked" && f.Transport.Calls == 1 && f.Host.Writes == 0 && failed.TranslationId is not null);
            f.Host.Change(f.Key(m), "Overview", x => x with { Writable = true, DisplayError = null }); f.Work.Retry(failed.Id); await f.Drain();
            check("unlock retry only displays the saved version", f.Transport.Calls == 1 && f.Work.Find(failed.Id)!.State == "completed" && f.Host.Writes == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "source-unreadable")))
        {
            var m = f.Add("unreadable"); var original = f.Host.Get(f.Key(m), "Overview").Original;
            f.Host.Change(f.Key(m), "Overview", x => x with { Original = null, SourceError = "nfo_busy" }); var batch = f.Submit([m], ["Overview"]); await f.Drain();
            check("temporarily unreadable source waits without requesting", f.Work.Find(batch.WorkIds[0])!.State == "waiting" && f.Transport.Calls == 0);
            f.Host.Change(f.Key(m), "Overview", x => x with { Original = original, SourceError = null }); await f.Observer.ObserveAllAsync(); f.Clock = f.Clock.AddSeconds(4); await f.Drain();
            check("source recovery resumes the existing demand safely", f.Transport.Calls == 1 && f.Work.Find(batch.WorkIds[0])!.State == "completed");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "dictionary-failure")))
        {
            var a = f.Add("one", "", "良い", "失敗"); var b = f.Add("two", "", "良い", "失敗");
            f.Transport.Handler = (input, send, _) => { send(); return Task.FromResult(input.Text == "失敗" ? new GenerationReply(null, "invalid_output", HttpStatus: 200) : FakeUnifiedTransport.Reply(input)); };
            var batch = f.Submit([a, b], ["Genres"]); await f.Drain();
            check("shared term failure keeps successful terms and original collection display", f.Transport.Calls == 2 && f.Host.Writes == 0 && f.Work.List(batch.Id).All(x => x.State == "failed" && x.Error == "dependency_failed"));
            f.Transport.Handler = null; f.Work.Retry(batch.WorkIds[0]); await f.Drain();
            check("retry requests only the failed term and wakes all dependent collections", f.Transport.Calls == 3 && f.Transport.Inputs.Count(x => x.Text == "良い") == 1 && f.Work.List(batch.Id).All(x => x.State == "completed") && f.Host.Writes == 2);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "unknown-outcome")))
        {
            var m = f.Add("unknown"); f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "network_uncertain", OutcomeUnknown: true)); };
            var batch = f.Submit([m], ["Overview"]); await f.Drain(); f.Restart(); await f.Drain();
            check("unknown network outcome remains paused through restart without repeat", f.Transport.Calls == 1 && f.Work.Find(batch.WorkIds[0])!.State == "paused" && f.Text("SELECT state FROM request_attempts") == "unknown");
            Reject("unknown outcome retry requires explicit risk acceptance", () => f.Work.Retry(batch.WorkIds[0]));
            var oldError = f.Text("SELECT error_code FROM request_attempts"); var oldElapsed = f.Count("SELECT elapsed_ms FROM request_attempts");
            f.Transport.Handler = null; f.Work.Retry(batch.WorkIds[0], acceptUnknown: true); await f.Drain();
            check("explicit unknown retry creates a distinct attempt", f.Transport.Calls == 2 && f.Count("SELECT COUNT(*) FROM request_attempts") == 2 && f.Count("SELECT MAX(sequence) FROM request_attempts") == 2);
            check("unknown history and duration survive explicit retry", f.Text("SELECT error_code FROM request_attempts WHERE sequence=1") == oldError && f.Count("SELECT elapsed_ms FROM request_attempts WHERE sequence=1") == oldElapsed && f.Count("SELECT uncertainty_acknowledged FROM request_attempts WHERE sequence=1") == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "rate-limit")))
        {
            var a = f.Add("rate-a"); var b = f.Add("rate-b");
            f.Transport.Handler = (input, send, _) => { send(); return Task.FromResult(f.Transport.Calls <= 2 ? new GenerationReply(null, "rate_limit", HttpStatus: 429, RetryAfterMs: 2000) : FakeUnifiedTransport.Reply(input)); };
            var batch = f.Submit([a, b], ["Name"]); await f.Drain();
            check("429 pauses related sends using one shared service deadline", f.Transport.Calls == 1 && f.Count("SELECT COUNT(*) FROM request_attempts") == 1 && f.Work.List(batch.Id).All(x => x.State == "waiting"));
            f.Clock = f.Clock.AddSeconds(2); await f.Drain();
            check("finite automatic retry preserves both 429 attempts", f.Transport.Calls == 2 && f.Count("SELECT COUNT(*) FROM request_attempts WHERE http_status=429") == 2 && f.Work.Find(batch.WorkIds[0])!.State == "failed");
            f.Clock = f.Clock.AddSeconds(2); await f.Drain(); f.Clock = f.Clock.AddMinutes(3); await f.Drain();
            check("other work proceeds after service delay without infinite retries", f.Transport.Calls == 3 && f.Work.Find(batch.WorkIds[1])!.State == "completed");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "authentication")))
        {
            var a = f.Add("auth-a"); var b = f.Add("auth-b");
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "authentication", HttpStatus: 401)); };
            var batch = f.Submit([a, b], ["Name"]); await f.Drain();
            check("authentication failure pauses the connection without per-film HTTP failures", f.Transport.Calls == 1 && f.Work.List(batch.Id).All(x => x.State == "paused") && f.Count("SELECT COUNT(*) FROM request_attempts") == 1);
            f.Transport.Handler = null; f.Work.Retry(batch.WorkIds[0]); f.Work.Retry(batch.WorkIds[1]); await f.Drain();
            check("connection recovery preserves the original authentication record", f.Transport.Calls == 3 && f.Count("SELECT COUNT(*) FROM request_attempts WHERE error_code='authentication'") == 1 && f.Work.List(batch.Id).All(x => x.State == "completed"));
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "local-rate-limit"), rpm: 1))
        {
            var a = f.Add("rpm-a"); var b = f.Add("rpm-b"); var batch = f.Submit([a, b], ["Name"]); await f.Drain();
            check("configured request limit waits before creating an HTTP attempt", f.Transport.Calls == 1 && f.Count("SELECT COUNT(*) FROM request_attempts") == 1 && f.Work.Find(batch.WorkIds[1])!.State == "waiting");
            f.Clock = f.Clock.AddMinutes(1); await f.Drain(); check("configured request limit resumes after the persisted window", f.Transport.Calls == 2 && f.Work.List(batch.Id).All(x => x.State == "completed"));
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "late-deadline"), timeout: 1000))
        {
            var m = f.Add("late"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var response = new TaskCompletionSource<GenerationReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Handler = async (_, send, _) => { send(); entered.SetResult(); return await response.Task; };
            var batch = f.Submit([m], ["Overview"]); var running = f.Executor.RunNextAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await running;
            check("deadline with an ignoring transport records unknown and retains its send slot", f.Work.Find(batch.WorkIds[0])!.State == "paused" && f.Executor.HasOutstandingSend && f.Text("SELECT state FROM request_attempts") == "unknown");
            var locked = false; try { using var other = new UnifiedExecutor(f.Work, f.Host, f.Transport); } catch (IOException) { locked = true; }
            check("a second executor cannot take ownership during an outstanding request", locked);
            var otherMovie = f.Add("other"); f.Submit([otherMovie], ["Name"]); await f.Drain();
            check("ignoring transport does not permit a second actual request", f.Transport.Calls == 1 && f.Transport.MaximumActive == 1);
            response.SetResult(new("晚到只存历史", HttpStatus: 200, Usage: new(10, 20, 30)));
            var until = Stopwatch.StartNew(); while (f.Executor.HasOutstandingSend && until.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
            check("late response is appended once without changing the original unknown diagnosis", !f.Executor.HasOutstandingSend && f.Count("SELECT COUNT(*) FROM translation_versions") == 1 && f.Text("SELECT state FROM request_attempts") == "unknown" && f.Text("SELECT late_translation_id FROM request_attempts") is not null && f.Content.Selected(f.Content.ReadField(m, "Overview")!.SourceId, "zh-Hans") is null && f.Host.Writes == 0);
            f.Transport.Handler = null; f.Clock = f.Clock.AddSeconds(1); await f.Drain(); check("send slot releases after the real transport finishes", f.Transport.Calls == 2 && f.Transport.MaximumActive == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "before-send-deadline"), timeout: 1000))
        {
            var m = f.Add("before-send"); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Handler = async (input, send, _) => { await release.Task; send(); return FakeUnifiedTransport.Reply(input); };
            f.Submit([m], ["Name"]); await f.Executor.RunNextAsync();
            check("deadline before send is explicitly not-sent", f.Transport.Calls == 0 && f.Text("SELECT state FROM request_attempts") == "not_sent");
            release.SetResult(); var until = Stopwatch.StartNew(); while (f.Executor.HasOutstandingSend && until.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
            check("a delayed before-send callback cannot send after its work failed", f.Transport.Calls == 0 && !f.Executor.HasOutstandingSend);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "diagnostic-privacy")))
        {
            var m = f.Add("privacy"); f.Transport.Handler = (_, send, _) => { send(); throw new InvalidOperationException("raw-secret-fixture-response"); }; f.Submit([m], ["Name"]); await f.Drain();
            check("transport exceptions retain only structured unknown diagnosis", f.Text("SELECT error_code FROM request_attempts") == "network_uncertain" && !f.Text("SELECT diagnostics_json FROM request_attempts")!.Contains("raw-secret-fixture", StringComparison.Ordinal));
        }
    }
}
