using System.Net;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using Localizer.Core;
using Localizer.Translation;
using Microsoft.Data.Sqlite;

if (args[0] == "recover-child")
{
    var store = new CandidateStore(args[1]); var provider = new FakeProvider();
    var job = await new TranslationQueue(store, provider).RunAsync("job");
    return job.State == TranslationJobState.Completed && provider.Calls == 0 ? 0 : 1;
}
var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
var results = new List<object>(); var failed = 0;
var scope = new LibraryKey("synthetic-server", "selected-library");
var rules = new TitleRules("test-rules-v1", "Translate naturally; preserve placeholders.", []);
(CandidateStore Store, TitleTranslationInput[] Inputs, string Database) Fresh(int count = 2, TranslationPolicy? policy = null)
{
    var db = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db"); var store = new CandidateStore(db);
    var inputs = Enumerable.Range(0, count).Select(n =>
    {
        var key = new MediaKey(scope.ServerId, scope.LibraryId, "item-" + n);
        var source = store.ObserveSource(key, $"山田太郎の旅 {n}", "LOCAL-PREFIX ");
        return new TitleTranslationInput(key, TitlePipeline.Prepare(source, TargetLanguage.SimplifiedChinese, ["山田太郎"], rules) with { Model = "synthetic-model" });
    }).ToArray();
    store.CreateTranslationJob("job", scope, TargetLanguage.SimplifiedChinese, inputs, policy ?? new(2, 0, 3000, 0));
    return (store, inputs, db);
}
void Expect(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
async Task Throws<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
async Task Check(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.GetType().Name }); Console.WriteLine("FAIL " + name + ": " + e); }
}
Action<TranslationCheckpoint, TranslationItem> Crash(TranslationCheckpoint expected) => (point, _) => { if (point == expected) throw new IOException("Injected interruption"); };

await Check("create_and_reopen_never_call_provider", () =>
{
    var f = Fresh(); var reopened = new CandidateStore(f.Database);
    Expect(reopened.GetTranslationJob("job").State == TranslationJobState.Ready && reopened.GetTranslationItems("job").All(x => x.Attempts == 0)); return Task.CompletedTask;
});
await Check("serial_queue_saves_unapproved_candidates", async () =>
{
    var f = Fresh(3); var provider = new FakeProvider { Handler = async (_, token) => { await Task.Delay(5, token); return new("中文候选", null, ["NumberMismatch"]); } };
    Expect((await new TranslationQueue(f.Store, provider).RunAsync("job")).State == TranslationJobState.Completed && provider.Calls == 3 && provider.MaximumActive == 1);
    Expect(f.Store.GetTranslationItems("job").All(x => !f.Store.GetCandidate(x.CandidateId!).Approved && x.Result!.Warnings.Contains("NumberMismatch")));
});
await Check("snapshot_and_job_id_are_immutable", async () =>
{
    var f = Fresh(1); var old = f.Store.GetTranslationItems("job")[0].Spec;
    var changed = f.Inputs.Select(x => x with { Spec = x.Spec with { Model = "different" } }).ToArray();
    await Throws<IdempotencyConflictException>(() => { f.Store.CreateTranslationJob("job", scope, TargetLanguage.SimplifiedChinese, changed, new(2, 0, 3000, 0)); return Task.CompletedTask; });
    Expect(f.Store.GetTranslationItems("job")[0].Spec == old);
});
await Check("wrong_scope_and_duplicate_items_rejected", async () =>
{
    var f = Fresh(1);
    await Throws<OperationValidationException>(() => { f.Store.CreateTranslationJob("wrong", new("server", "other"), TargetLanguage.SimplifiedChinese, f.Inputs, new()); return Task.CompletedTask; });
    await Throws<ArgumentException>(() => { f.Store.CreateTranslationJob("duplicate", scope, TargetLanguage.SimplifiedChinese, [f.Inputs[0], f.Inputs[0]], new()); return Task.CompletedTask; });
});
await Check("same_fingerprint_cache_skips_provider_and_preserves_human_edit", async () =>
{
    var f = Fresh(1); var provider = new FakeProvider(); var runner = new TranslationQueue(f.Store, provider); await runner.RunAsync("job");
    var candidate = f.Store.GetCandidate(f.Store.GetTranslationItems("job")[0].CandidateId!);
    var edited = f.Store.Edit(candidate.Id, candidate.Revision, "人工修订"); f.Store.Approve(edited.Id, edited.Revision);
    f.Store.CreateTranslationJob("replay", scope, TargetLanguage.SimplifiedChinese, f.Inputs, new(2, 0, 3000, 0));
    await runner.RunAsync("replay"); Expect(provider.Calls == 1 && f.Store.GetTranslationItems("replay")[0].State == TranslationItemState.Cached && f.Store.GetCandidate(candidate.Id).Text == "人工修订");
});
await Check("different_language_uses_distinct_candidate", async () =>
{
    var f = Fresh(1); var provider = new FakeProvider(); var runner = new TranslationQueue(f.Store, provider); await runner.RunAsync("job");
    var source = f.Store.GetCurrentSource(f.Inputs[0].Key)!;
    f.Store.CreateTranslationJob("english", scope, TargetLanguage.English, [new(source.Key, TitlePipeline.Prepare(source, TargetLanguage.English, ["山田太郎"], rules))], new(2, 0, 3000, 0));
    await runner.RunAsync("english"); Expect(provider.Calls == 2 && f.Store.GetSelected(source.Id, TargetLanguage.SimplifiedChinese) is not null && f.Store.GetSelected(source.Id, TargetLanguage.English) is not null);
});
await Check("changed_source_skips_before_request", async () =>
{
    var f = Fresh(1); f.Store.ObserveSource(f.Inputs[0].Key, "new source", ""); var provider = new FakeProvider();
    await new TranslationQueue(f.Store, provider).RunAsync("job"); Expect(provider.Calls == 0 && f.Store.GetTranslationItems("job")[0].State == TranslationItemState.SourceChanged);
});
await Check("late_response_is_saved_for_historical_source_only", async () =>
{
    var f = Fresh(1); var provider = new FakeProvider { Handler = (_, _) => { f.Store.ObserveSource(f.Inputs[0].Key, "new source", ""); return Task.FromResult(new TitleProviderResult("历史候选", null, [])); } };
    await new TranslationQueue(f.Store, provider).RunAsync("job"); var item = f.Store.GetTranslationItems("job")[0];
    Expect(item.State == TranslationItemState.SourceChanged && item.CandidateId is not null && f.Store.GetSelected(f.Store.GetCurrentSource(item.Source.Key)!.Id, TargetLanguage.SimplifiedChinese) is null);
});
await Check("invalid_output_is_recorded_and_next_item_continues", async () =>
{
    var f = Fresh(); var provider = new FakeProvider { Handler = (r, _) => Task.FromResult(r.Source.Key.ItemId == "item-0" ? new TitleProviderResult(null, "Refused", []) : new("中文候选", null, [])) };
    Expect((await new TranslationQueue(f.Store, provider).RunAsync("job")).State == TranslationJobState.CompletedWithErrors && provider.Calls == 2);
    Expect(f.Store.GetTranslationItems("job")[0].CandidateId is null);
});
await Check("authentication_error_pauses_batch_and_needs_explicit_retry", async () =>
{
    var f = Fresh(); var provider = new FakeProvider { Handler = (_, _) => throw new TitleProviderException(ProviderError.Authentication) };
    var runner = new TranslationQueue(f.Store, provider); Expect((await runner.RunAsync("job")).State == TranslationJobState.Paused);
    await runner.RunAsync("job"); Expect(provider.Calls == 1 && f.Store.GetTranslationItems("job")[1].Attempts == 0);
    provider.Handler = null; Expect((await runner.RunAsync("job", new(RetryFailed: true))).State == TranslationJobState.Completed && provider.Calls == 3);
});
await Check("rate_limit_retries_are_bounded", async () =>
{
    var f = Fresh(); var provider = new FakeProvider { Handler = (_, _) => throw new TitleProviderException(ProviderError.RateLimited, TimeSpan.Zero) };
    var runner = new TranslationQueue(f.Store, provider); Expect((await runner.RunAsync("job")).State == TranslationJobState.Paused && provider.Calls == 2);
    await runner.RunAsync("job", new(RetryFailed: true)); Expect(provider.Calls == 2 && f.Store.GetTranslationItems("job")[1].Attempts == 0);
});
await Check("rate_limit_can_succeed_on_next_bounded_attempt", async () =>
{
    var f = Fresh(1); var n = 0; var provider = new FakeProvider { Handler = (_, _) => ++n == 1 ? throw new TitleProviderException(ProviderError.RateLimited, TimeSpan.Zero) : Task.FromResult(new TitleProviderResult("中文候选", null, [])) };
    Expect((await new TranslationQueue(f.Store, provider).RunAsync("job")).State == TranslationJobState.Completed && provider.Calls == 2);
});
await Check("long_retry_after_pauses_without_shortening_server_delay", async () =>
{
    var f = Fresh(); var provider = new FakeProvider { Handler = (_, _) => throw new TitleProviderException(ProviderError.RateLimited, TimeSpan.FromMinutes(2)) };
    var job = await new TranslationQueue(f.Store, provider).RunAsync("job"); Expect(job.State == TranslationJobState.Paused && provider.Calls == 1 && DateTimeOffset.Parse(job.NextRequestUtc!) > DateTimeOffset.UtcNow.AddSeconds(90));
});
await Check("timeout_is_uncertain_not_automatically_retried", async () =>
{
    var f = Fresh(2, new(2, 0, 20, 0)); var provider = new FakeProvider { Handler = async (_, token) => { await Task.Delay(10000, token); return new("unused", null, []); } };
    var runner = new TranslationQueue(f.Store, provider); await runner.RunAsync("job"); await runner.RunAsync("job");
    Expect(provider.Calls == 1 && f.Store.GetTranslationItems("job")[0].State == TranslationItemState.Uncertain);
    provider.Handler = null; Expect((await runner.RunAsync("job", new(AcceptUncertainRequest: true))).State == TranslationJobState.Completed);
});
await Check("cancel_stops_next_item_and_preserves_completed_result", async () =>
{
    var f = Fresh(); var provider = new FakeProvider { Handler = (_, _) => { f.Store.RequestTranslationCancel("job"); return Task.FromResult(new TitleProviderResult("中文候选", null, [])); } };
    var runner = new TranslationQueue(f.Store, provider); Expect((await runner.RunAsync("job")).State == TranslationJobState.Cancelled && provider.Calls == 1);
    Expect(f.Store.GetTranslationItems("job")[0].State == TranslationItemState.Succeeded && f.Store.GetTranslationItems("job")[1].State == TranslationItemState.Pending);
    provider.Handler = null; Expect((await runner.RunAsync("job")).State == TranslationJobState.Completed && provider.Calls == 2);
});
await Check("persistent_cancel_interrupts_inflight_request", async () =>
{
    var f = Fresh(); var entered = new TaskCompletionSource(); var provider = new FakeProvider { Handler = async (_, token) => { entered.SetResult(); await Task.Delay(10000, token); return new("unused", null, []); } };
    var running = new TranslationQueue(f.Store, provider).RunAsync("job"); await entered.Task;
    new CandidateStore(f.Database).RequestTranslationCancel("job"); var job = await running;
    Expect(job.State == TranslationJobState.Cancelled && provider.Calls == 1 && f.Store.GetTranslationItems("job")[0].State == TranslationItemState.Uncertain);
});
await Check("second_worker_cannot_start_while_request_is_active", async () =>
{
    var f = Fresh(1); var entered = new TaskCompletionSource(); var finish = new TaskCompletionSource();
    var provider = new FakeProvider { Handler = async (_, _) => { entered.SetResult(); await finish.Task; return new("中文候选", null, []); } };
    var running = new TranslationQueue(f.Store, provider).RunAsync("job"); await entered.Task;
    await Throws<OperationBusyException>(() => new TranslationQueue(new CandidateStore(f.Database), new FakeProvider()).RunAsync("job"));
    finish.SetResult(); await running; Expect(provider.Calls == 1);
});
await Check("interrupted_request_requires_attribution_acknowledgment", async () =>
{
    var f = Fresh(1); var provider = new FakeProvider();
    await Throws<IOException>(() => new TranslationQueue(f.Store, provider, Crash(TranslationCheckpoint.RequestSaved)).RunAsync("job"));
    var runner = new TranslationQueue(new CandidateStore(f.Database), provider); runner.Reconcile("job"); await runner.RunAsync("job");
    Expect(provider.Calls == 0 && f.Store.GetTranslationItems("job")[0].State == TranslationItemState.Uncertain);
    Expect((await runner.RunAsync("job", new(AcceptUncertainRequest: true))).State == TranslationJobState.Completed && provider.Calls == 1);
});
await Check("response_and_candidate_checkpoints_recover_without_new_request", async () =>
{
    foreach (var point in new[] { TranslationCheckpoint.ResponseSaved, TranslationCheckpoint.CandidateSaved })
    {
        var f = Fresh(1); var provider = new FakeProvider(); await Throws<IOException>(() => new TranslationQueue(f.Store, provider, Crash(point)).RunAsync("job"));
        Expect((await new TranslationQueue(new CandidateStore(f.Database), provider).RunAsync("job")).State == TranslationJobState.Completed && provider.Calls == 1);
    }
});
await Check("separate_process_finalizes_saved_response_offline", async () =>
{
    var f = Fresh(1); await Throws<IOException>(() => new TranslationQueue(f.Store, new FakeProvider(), Crash(TranslationCheckpoint.ResponseSaved)).RunAsync("job"));
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("recover-child"); start.ArgumentList.Add(f.Database);
    using var process = Process.Start(start)!; await process.WaitForExitAsync(); Expect(process.ExitCode == 0);
});
await Check("schema_three_migration_keeps_existing_candidates", () =>
{
    var f = Fresh(1); var source = f.Store.GetCurrentSource(f.Inputs[0].Key)!; var c = f.Store.SaveGenerated(source.Id, TargetLanguage.SimplifiedChinese, "existing", f.Inputs[0].Spec);
    using (var db = new SqliteConnection("Data Source=" + f.Database)) { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE genre_save_receipts; DROP TABLE translation_items; DROP TABLE translation_jobs; PRAGMA user_version=3;"; cmd.ExecuteNonQuery(); }
    var reopened = new CandidateStore(f.Database); Expect(reopened.GetCandidate(c.Id).Text == "existing"); return Task.CompletedTask;
});

// Exercise the actual HTTP provider through a recording in-memory handler, never through live cloud traffic.
var sample = Fresh(1); var sourceSample = sample.Store.GetCurrentSource(sample.Inputs[0].Key)!;
var requestSample = new TitleProviderRequest(sourceSample, TargetLanguage.SimplifiedChinese, sample.Inputs[0].Spec);
await Check("http_payload_contains_only_masked_title_prompt_and_allowed_parameters", async () =>
{
    var handler = new StubHttp { Handler = async (request, token) =>
    {
        var body = await request.Content!.ReadAsStringAsync(token);
        Expect(request.RequestUri!.AbsoluteUri == TitlePipeline.GroqEndpoint + "/chat/completions" && request.Method == HttpMethod.Post);
        Expect(request.Headers.Authorization?.Parameter == "synthetic-key" && !body.Contains("山田太郎") && !body.Contains("selected-library") && !body.Contains("LOCAL-PREFIX"));
        using var parsed = JsonDocument.Parse(body); Expect(parsed.RootElement.GetProperty("reasoning_effort").GetString() == "none");
        return StubHttp.Response("__JML_PERSON_A__的旅行 0");
    } };
    using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("synthetic-key"), handler);
    var result = await provider.TranslateAsync(requestSample, default); Expect(result.Title == "山田太郎的旅行 0" && result.TotalTokens == 24);
});
await Check("campaign_local_provenance_is_not_sent_to_provider", async () =>
{
    var context = System.Text.Json.Nodes.JsonNode.Parse(requestSample.Spec.ContextJson)!;
    context["CampaignId"] = "private-campaign-marker";
    context["CampaignItemId"] = "private-item-marker";
    var handler = new StubHttp { Handler = async (request, token) =>
    {
        var body = await request.Content!.ReadAsStringAsync(token);
        Expect(!body.Contains("private-campaign-marker") && !body.Contains("private-item-marker")
            && !body.Contains("CampaignId") && !body.Contains("山田太郎"));
        return StubHttp.Response("__JML_PERSON_A__中文 0");
    } };
    using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("synthetic-key"), handler);
    var result = await provider.TranslateAsync(requestSample with { Spec = requestSample.Spec with { ContextJson = context.ToJsonString() } }, default);
    Expect(result.Title is not null && result.ErrorCategory is null);
});
await Check("http_statuses_are_classified_without_error_body_leak", async () =>
{
    foreach (var (status, expected) in new[] { (401, ProviderError.Authentication), (403, ProviderError.Permission), (404, ProviderError.ModelUnavailable),
        (429, ProviderError.RateLimited), (500, ProviderError.TransportUncertain), (302, ProviderError.Configuration), (400, ProviderError.RequestRejected), (422, ProviderError.RequestRejected) })
    {
        var handler = new StubHttp { Handler = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("SECRET SHOULD NOT APPEAR") }) };
        using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("synthetic-key"), handler);
        try { await provider.TranslateAsync(requestSample, default); throw new InvalidOperationException("missing error"); }
        catch (TitleProviderException e) { Expect(e.Category == expected && !e.Message.Contains("SECRET") && handler.Calls == 1
            && e.Diagnostics?.HttpStatus == status && !JsonSerializer.Serialize(e.Diagnostics).Contains("SECRET")); }
    }
});
await Check("arbitrary_endpoint_or_extra_payload_fields_rejected_before_key_load", async () =>
{
    var keys = 0; var handler = new StubHttp(); using var provider = new GroqTitleProvider(_ => { keys++; return ValueTask.FromResult("key"); }, handler);
    foreach (var spec in new[] { requestSample.Spec with { Endpoint = "https://other.invalid" }, requestSample.Spec with { ParametersJson = "{\"messages\":[]}" }, requestSample.Spec with { ContextJson = "{}" } })
        await Throws<TitleProviderException>(() => provider.TranslateAsync(requestSample with { Spec = spec }, default));
    Expect(keys == 0 && handler.Calls == 0);
});
await Check("placeholders_refusal_truncation_and_model_mismatch_rejected", async () =>
{
    foreach (var mode in new[] { "missing", "duplicate", "altered", "refusal", "truncated", "model" })
    {
        var handler = new StubHttp { Handler = (_, _) => Task.FromResult(mode switch
        { "missing" => StubHttp.Response("中文 0"), "duplicate" => StubHttp.Response("__JML_PERSON_A____JML_PERSON_A__中文 0"),
          "altered" => StubHttp.Response("__jml_person_a__中文 0"), "refusal" => StubHttp.Response("我无法翻译"),
          "truncated" => StubHttp.Response("__JML_PERSON_A__中文 0", "length"), _ => StubHttp.Response("__JML_PERSON_A__中文 0", model: "different") }) };
        using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("key"), handler);
        var result = await provider.TranslateAsync(requestSample, default); Expect(result.Title is null && result.ErrorCategory is not null);
    }
});
await Check("numbers_and_mask_changes_are_review_warnings", async () =>
{
    using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("key"), new StubHttp { Handler = (_, _) => Task.FromResult(StubHttp.Response("__JML_PERSON_A__中文 2 ●")) });
    var result = await provider.TranslateAsync(requestSample, default); Expect(result.Title is not null && result.Warnings.Contains("NumberMismatch") && result.Warnings.Contains("MaskOrSymbolChanged"));
});
await Check("response_size_is_bounded", async () =>
{
    using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("key"), new StubHttp { Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 262145)) }) });
    Expect((await provider.TranslateAsync(requestSample, default)).ErrorCategory == "ResponseTooLarge");
});
await Check("repeated_and_overlapping_names_are_protected_per_occurrence", () =>
{
    var protectedText = TitlePipeline.Protect("山田太郎と山田、山田太郎", ["山田", "山田太郎"]);
    Expect(protectedText.Mapping.Count == 3 && protectedText.Mapping["__JML_PERSON_A__"] == "山田太郎" && protectedText.Mapping["__JML_PERSON_B__"] == "山田"); return Task.CompletedTask;
});
await Check("existing_chinese_and_english_rules_feed_frozen_prompts", () =>
{
    foreach (var (file, language) in new[] { ("title-rules.zh-Hans.json", TargetLanguage.SimplifiedChinese), ("title-rules.en.json", TargetLanguage.English) })
    {
        var savedRules = JsonSerializer.Deserialize<TitleRules>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var source = sourceSample with { OriginalTitle = "山田太郎の社会復帰 0" };
        var spec = TitlePipeline.Prepare(source, language, ["山田太郎"], savedRules);
        Expect(spec.RulesVersion == savedRules.Version && spec.PromptText.Contains(savedRules.Instructions));
        foreach (var rule in savedRules.Rules.Where(x => System.Text.RegularExpressions.Regex.IsMatch(source.OriginalTitle, x.Pattern))) Expect(spec.PromptText.Contains(rule.Instruction));
        Expect(TitlePipeline.ValidateContext(new(source, language, spec)).MaskedSource == "__JML_PERSON_A__の社会復帰 0");
    }
    return Task.CompletedTask;
});

await Check("local_credential_failure_does_not_send_http", async () =>
{
    var handler = new StubHttp { Handler = (_, _) => throw new InvalidOperationException("HTTP must not run") };
    using var provider = new GroqTitleProvider(_ => throw new InvalidOperationException("private local detail"), handler);
    try { await provider.TranslateAsync(requestSample, default); throw new InvalidOperationException("Expected credential error"); }
    catch (TitleProviderException error) { Expect(error.Category == ProviderError.CredentialUnavailable && error.Message == "CredentialUnavailable"); }
});
await Check("task_list_filters_scope_and_bounds_results", () =>
{
    var f = Fresh();
    f.Store.CreateTranslationJob("second", scope, TargetLanguage.English, f.Inputs, new());
    Expect(f.Store.ListTranslationJobs(scope).Count == 2 && f.Store.ListTranslationJobs(scope, 1).Count == 1);
    Expect(f.Store.ListTranslationJobs(new(scope.ServerId, "another-library")).Count == 0);
    Expect(f.Store.ListTranslationJobs(new("another-server", scope.LibraryId)).Count == 0);
    return Task.CompletedTask;
});

await Check("m8_old_policy_json_keeps_fixed_pacing_and_enum_values", () =>
{
    var old = JsonSerializer.Deserialize<TranslationPolicy>("{\"MaxAttempts\":2,\"IntervalMs\":12000,\"TimeoutMs\":60000,\"RetryDelayMs\":12000}")!;
    Expect(!old.Adaptive && old.RequestSpacing(requestSample).TotalMilliseconds == 12000 && (int)ProviderError.CredentialUnavailable == 6 && (int)ProviderError.RequestRejected == 7);
    return Task.CompletedTask;
});
await Check("m8_existing_four_field_policy_fingerprint_replays_unchanged", () =>
{
    var f = Fresh(1); var items = f.Store.GetTranslationItems("job"); var language = TargetLanguage.SimplifiedChinese;
    var oldPolicyJson = "{\"MaxAttempts\":2,\"IntervalMs\":0,\"TimeoutMs\":3000,\"RetryDelayMs\":0}";
    using var oldPolicy = JsonDocument.Parse(oldPolicyJson);
    var legacyPayload = JsonSerializer.Serialize(new { scope, language, policy = oldPolicy.RootElement, items });
    var legacyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(legacyPayload))).ToLowerInvariant();
    var job = f.Store.GetTranslationJob("job");
    Expect(job.Fingerprint == legacyHash);
    // Emulate a persisted pre-M8 policy payload with none of the adaptive fields present.
    var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(job))!;
    node["Policy"] = System.Text.Json.Nodes.JsonNode.Parse(oldPolicyJson);
    using (var db = new SqliteConnection("Data Source=" + f.Database))
    {
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE translation_jobs SET payload_json=$payload WHERE id='job'";
        command.Parameters.AddWithValue("$payload", node.ToJsonString()); command.ExecuteNonQuery();
    }
    var reopened = new CandidateStore(f.Database);
    Expect(reopened.CreateTranslationJob("job", scope, language, f.Inputs, reopened.GetTranslationJob("job").Policy).Fingerprint == legacyHash);
    return Task.CompletedTask;
});
await Check("m8_adaptive_usage_speeds_up_without_spending_completion_reservation", () =>
{
    var policy = TranslationPolicy.AdaptiveGroq();
    var details = new ProviderDiagnostics(PromptTokens: 200, TotalTokens: 250, TokenLimit: 8000, TokensRemaining: 7000, TokenResetMs: 3000);
    Expect(policy.RequestSpacing(requestSample, details).TotalMilliseconds == 2000);
    // Without confirmed remaining capacity, the whole completion reservation determines the conservative pace.
    Expect(policy.RequestSpacing(requestSample, details with { TokensRemaining = null }).TotalMilliseconds >= 9180);
    Expect(policy.RequestSpacing(requestSample, details with { TokensRemaining = 100, TokenResetMs = 45000 }).TotalMilliseconds == 45000);
    Expect(policy.RequestSpacing(requestSample, details with { RequestsRemaining = 0, RequestResetMs = 3600000 }).TotalMilliseconds == 3600000);
    return Task.CompletedTask;
});
await Check("m8_response_diagnostics_preserve_only_numeric_limits_usage_and_allowlisted_code", async () =>
{
    var handler = new StubHttp { Handler = (_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"code\":\"json_validate_failed\",\"message\":\"private-title synthetic-key\",\"failed_generation\":\"private-output\"}}") };
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit-requests", "1000");
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", "999");
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit-tokens", "8000");
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-tokens", "7000");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset-requests", "1h2m3.5s");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset-tokens", "250ms");
        response.Headers.TryAddWithoutValidation("Retry-After", "1.5");
        response.Headers.TryAddWithoutValidation("x-request-id", "private-request-id");
        return Task.FromResult(response);
    } };
    using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("synthetic-key"), handler);
    try { await provider.TranslateAsync(requestSample, default); throw new Exception("missing error"); }
    catch (TitleProviderException e)
    {
        var d = e.Diagnostics!; var saved = JsonSerializer.Serialize(d);
        Expect(e.Category == ProviderError.RequestRejected && d.HttpStatus == 400 && d.ServiceCode == "json_validate_failed" && d.ElapsedMs >= 0
            && d.RequestLimit == 1000 && d.RequestsRemaining == 999 && d.TokenLimit == 8000 && d.TokensRemaining == 7000
            && d.RequestResetMs == 3723500 && d.TokenResetMs == 250 && d.RetryAfterMs == 1500 && e.RetryAfter == TimeSpan.FromMilliseconds(1500));
        Expect(!saved.Contains("private") && !saved.Contains("synthetic-key"));
    }
});
await Check("m8_unknown_service_code_and_malformed_limit_headers_are_discarded", async () =>
{
    var handler = new StubHttp { Handler = (_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"code\":\"PRIVATE arbitrary error\",\"type\":\"model_not_found\"}}") };
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-tokens", "-1");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset-tokens", "garbage 1s");
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit-tokens", "999999999999999999999");
        return Task.FromResult(response);
    } };
    using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("key"), handler);
    try { await provider.TranslateAsync(requestSample, default); throw new Exception("missing error"); }
    catch (TitleProviderException e)
    { Expect(e.Category == ProviderError.RequestRejected && e.Diagnostics!.ServiceCode is null && e.Diagnostics.TokensRemaining is null
        && e.Diagnostics.TokenResetMs is null && e.Diagnostics.TokenLimit is null && !JsonSerializer.Serialize(e.Diagnostics).Contains("PRIVATE")); }
});
await Check("m8_explicit_global_error_codes_still_pause_while_item_errors_continue", async () =>
{
    foreach (var (status, code, category) in new[] { (400, "model_not_found", ProviderError.ModelUnavailable), (422, "unsupported_parameter", ProviderError.Configuration),
        (400, "context_length_exceeded", ProviderError.RequestRejected), (429, "insufficient_quota", ProviderError.QuotaExceeded),
        (400, "invalid_api_key", ProviderError.Authentication), (400, "quota_exceeded", ProviderError.QuotaExceeded) })
    {
        using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("key"), new StubHttp { Handler = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code } })) }) });
        try { await provider.TranslateAsync(requestSample, default); throw new Exception("missing error"); }
        catch (TitleProviderException e) { Expect(e.Category == category && e.Diagnostics!.ServiceCode == code); }
    }
});
await Check("m8_invalid_title_still_keeps_usage_and_http_timing", async () =>
{
    using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("key"), new StubHttp { Handler = (_, _) => Task.FromResult(StubHttp.Response("中文无占位符")) });
    var result = await provider.TranslateAsync(requestSample, default);
    Expect(result.ErrorCategory == "PlaceholderMismatch" && result.TotalTokens == 24 && result.Diagnostics?.TotalTokens == 24
        && result.Diagnostics.HttpStatus == 200 && result.Diagnostics.ElapsedMs >= 0);
});
await Check("m8_item_rejection_is_durable_and_finishes_with_exceptions", async () =>
{
    var f = Fresh(); var provider = new FakeProvider { Handler = (r, _) => r.Source.Key.ItemId == "item-0"
        ? throw new TitleProviderException(ProviderError.RequestRejected, diagnostics: new(400, "json_validate_failed", 10))
        : Task.FromResult(new TitleProviderResult("中文候选", null, [])) };
    var job = await new TranslationQueue(f.Store, provider).RunAsync("job");
    var item = new CandidateStore(f.Database).GetTranslationItems("job")[0];
    Expect(job.State == TranslationJobState.CompletedWithErrors && provider.Calls == 2 && item.State == TranslationItemState.Failed
        && item.ErrorCategory == "RequestRejected" && item.Result!.Diagnostics?.HttpStatus == 400 && item.CandidateId is null);
});
await Check("m8_adaptive_short_rate_limit_retries_and_preserves_next_slot", async () =>
{
    var f = Fresh(1, new(2, 0, 3000, 0, true, 10000, 10000000)); var n = 0;
    var provider = new FakeProvider { Handler = (_, _) => ++n == 1
        ? throw new TitleProviderException(ProviderError.RateLimited, diagnostics: new(429, "rate_limit_exceeded", TokensRemaining: 0, TokenResetMs: 20))
        : Task.FromResult(new TitleProviderResult("中文候选", null, [], 24, new(PromptTokens: 20, TotalTokens: 24, TokensRemaining: 9999999, TokenResetMs: 1))) };
    var watch = Stopwatch.StartNew(); var job = await new TranslationQueue(f.Store, provider).RunAsync("job");
    Expect(job.State == TranslationJobState.Completed && provider.Calls == 2 && watch.ElapsedMilliseconds >= 20 && job.NextRequestUtc is not null);
});
await Check("m8_long_quota_reset_pauses_instead_of_waiting_or_resending", async () =>
{
    var f = Fresh(2, TranslationPolicy.AdaptiveGroq());
    var provider = new FakeProvider { Handler = (_, _) => throw new TitleProviderException(ProviderError.RateLimited,
        diagnostics: new(429, "rate_limit_exceeded", RequestsRemaining: 0, RequestResetMs: 3600000)) };
    var job = await new TranslationQueue(f.Store, provider).RunAsync("job");
    Expect(job.State == TranslationJobState.Paused && job.ErrorCategory == "QuotaExceeded" && provider.Calls == 1
        && DateTimeOffset.Parse(job.NextRequestUtc!) > DateTimeOffset.UtcNow.AddMinutes(59) && f.Store.GetTranslationItems("job")[1].Attempts == 0);
});
await Check("m8_successful_last_quota_request_saves_candidate_then_pauses_remaining", async () =>
{
    var f = Fresh(2, TranslationPolicy.AdaptiveGroq()); var provider = new FakeProvider { Handler = (_, _) => Task.FromResult(new TitleProviderResult("中文候选", null, [], 24,
        new(PromptTokens: 20, TotalTokens: 24, RequestsRemaining: 0, RequestResetMs: 3600000, TokensRemaining: 7000, TokenResetMs: 1000))) };
    var job = await new TranslationQueue(f.Store, provider).RunAsync("job");
    Expect(job.State == TranslationJobState.Paused && job.ErrorCategory == "QuotaExceeded" && provider.Calls == 1
        && f.Store.GetTranslationItems("job")[0].State == TranslationItemState.Succeeded && f.Store.GetTranslationItems("job")[1].Attempts == 0);
});

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
await ServiceProfileChecks.Run(Check);
await PersonNameChecks.Run(Check, root);
await GeneralMovieChecks.Run(Check);
await PublicCloudEndpointChecks.Run(Check);
await CompatibleServiceChecks.Run(Check);
await ServiceSettingsChecks.Run(Check, root);
File.WriteAllText(args[1], JsonSerializer.Serialize(new { stage = "M3-E translation queue and Groq transport", total = results.Count,
    passed = results.Count-failed, failed, liveModelRequests = 0, jellyfinWrites = 0, runtime = Environment.Version.ToString(), results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count-failed}/{results.Count} translation checks passed.");
return failed == 0 ? 0 : 1;
