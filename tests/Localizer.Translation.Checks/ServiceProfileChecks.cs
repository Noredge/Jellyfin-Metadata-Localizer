using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Localizer.Core;
using Localizer.Translation;

public static class ServiceProfileChecks
{
    private static void Expect(bool condition) { if (!condition) throw new InvalidOperationException("Service assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Expected profile rejection."); }
    private static async Task Error(Func<Task> action, ProviderError category)
    { try { await action(); } catch (TitleProviderException error) { Expect(error.Category == category); return; } throw new InvalidOperationException("Expected provider rejection."); }
    private static HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Native(string title = "__JML_PERSON_A__的休息 1", string model = "local-model", int generated = 20, int thinking = 0) => new
    {
        model_instance_id = model,
        output = new[] { new { type = "reasoning", content = "SECRET_REASONING_MUST_NOT_PERSIST" },
            new { type = "message", content = JsonSerializer.Serialize(new { title }) } },
        stats = new { input_tokens = 10, total_output_tokens = generated, reasoning_output_tokens = thinking }
    };
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        var source = new SourceSnapshot(1, new("synthetic-server", "private-library", "private-item"), 1, "山田の休日 1", "PRIVATE_PREFIX", "synthetic");
        var legacy = TitlePipeline.Prepare(source, TargetLanguage.SimplifiedChinese, ["山田"], new("test", "Preserve the supplied title.", [])) with { Model = "synthetic-model" };
        await check("m29_openai_routes_only_own_key_and_disables_reasoning_storage", async () =>
        {
            var ai = (TranslationServiceProfile.OpenAiDefault() with { Model = "synthetic-openai-model" });
            Reject(() => (ai with { Endpoint = "https://example.com/v1" }).Validate());
            var handler = new StubHttp { Handler = async (request, token) =>
            {
                Expect(request.RequestUri!.AbsoluteUri == TranslationServiceProfile.OpenAiEndpoint + "/chat/completions");
                Expect(request.Headers.Authorization!.Parameter == "test-openai");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Expect(body.RootElement.GetProperty("model").GetString() == "synthetic-openai-model"
                    && body.RootElement.GetProperty("reasoning_effort").GetString() == "none"
                    && !body.RootElement.GetProperty("store").GetBoolean() && !body.RootElement.TryGetProperty("temperature", out _));
                return Json(new { model = "synthetic-openai-model", choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { title = "__JML_PERSON_A__的休息 1" }) } } } });
            } };
            using var router = new TranslationServiceRouter(_ => throw new Exception("Wrong credential"), openAiCredential: _ => ValueTask.FromResult("test-openai"), openAiTestHandler: handler);
            var result = await router.TranslateAsync(new(source, TargetLanguage.SimplifiedChinese, ai.Apply(legacy)), default);
            Expect(result.Title is not null);
        });
        var profile = new TranslationServiceProfile("local", "lmstudio", "http://127.0.0.1:1234/api/v1", "local-model");
        TitleProviderRequest Request(GenerationSpec? spec = null) => new(source, TargetLanguage.SimplifiedChinese, spec ?? profile.Apply(legacy));
        await check("m10_service_profiles_validate_loopback_and_fixed_cloud_destinations", () =>
        {
            foreach (var endpoint in new[] { "http://localhost:1234/api/v1", "https://127.0.0.1:1234/api/v1/", "http://[::1]:1234/api/v1" })
                (profile with { Endpoint = endpoint }).Validate();
            foreach (var endpoint in new[] { "http://example.com/api/v1", "http://192.168.1.20:1234/api/v1", "http://127.0.0.1:1234/v1",
                "http://user:pass@localhost/api/v1", "http://localhost/api/v1?key=secret", "http://localhost/api/v1#fragment", "ftp://localhost/api/v1" })
                Reject(() => (profile with { Endpoint = endpoint }).Validate());
            Reject(() => ((TranslationServiceProfile.GroqDefault() with { Model = "synthetic-model" }) with { Endpoint = "https://other.invalid/v1" }).Validate());
            Reject(() => (profile with { Kind = "unknown" }).Validate()); Reject(() => (profile with { MaxOutputTokens = 0 }).Validate());
            Expect((profile with { Endpoint = "http://localhost:1234/api/v1/" }).Apply(legacy).Endpoint == "http://localhost:1234/api/v1");
            return Task.CompletedTask;
        });
        await check("m10_profile_application_freezes_transport_and_keeps_source_prompt_context", () =>
        {
            var frozen = profile.Apply(legacy); var changed = profile with { Model = "different-model", MaxOutputTokens = 1024 };
            Expect(frozen.PromptText == legacy.PromptText && frozen.ContextJson == legacy.ContextJson && frozen.Model == "local-model"
                && changed.Apply(legacy).Model != frozen.Model && profile.Policy().IntervalMs == 0 && !profile.Policy().Adaptive
                && (TranslationServiceProfile.GroqDefault() with { Model = "synthetic-model" }).Policy().Adaptive);
            using var parameters = JsonDocument.Parse(frozen.ParametersJson);
            Expect(parameters.RootElement.GetProperty("max_output_tokens").GetInt32() == 512);
            return Task.CompletedTask;
        });
        await check("m10_local_router_never_loads_cloud_key_and_sends_only_frozen_payload", async () =>
        {
            var keys = 0; var cloud = new StubHttp();
            var local = new StubHttp { Handler = async (request, token) =>
            {
                Expect(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == profile.Endpoint + "/chat" && request.Headers.Authorization is null);
                var text = await request.Content!.ReadAsStringAsync(token); using var body = JsonDocument.Parse(text);
                Expect(!text.Contains("山田") && !text.Contains("private-item") && !text.Contains("PRIVATE_PREFIX") && !text.Contains("private-library"));
                Expect(body.RootElement.GetProperty("reasoning").GetString() == "off" && !body.RootElement.GetProperty("store").GetBoolean()
                    && !body.RootElement.GetProperty("stream").GetBoolean() && body.RootElement.GetProperty("integrations").GetArrayLength() == 0
                    && body.RootElement.GetProperty("temperature").GetDouble() == 0.2 && body.RootElement.GetProperty("max_output_tokens").GetInt32() == 512);
                return Json(Native());
            } };
            using var router = new TranslationServiceRouter(_ => { keys++; throw new Exception("Never load cloud key"); }, cloud, local);
            var result = await router.TranslateAsync(Request(), default);
            Expect(result.Title == "山田的休息 1" && result.TotalTokens == 30 && result.Diagnostics?.HttpStatus == 200
                && !JsonSerializer.Serialize(result).Contains("SECRET_REASONING") && keys == 0 && cloud.Calls == 0 && local.Calls == 1);
        });
        await check("m10_legacy_groq_spec_still_routes_to_cloud_without_local_call", async () =>
        {
            var keys = 0; var local = new StubHttp(); var cloud = new StubHttp { Handler = (_, _) => Task.FromResult(StubHttp.Response("__JML_PERSON_A__的休息 1")) };
            using var router = new TranslationServiceRouter(_ => { keys++; return ValueTask.FromResult("synthetic-key"); }, cloud, local);
            var result = await router.TranslateAsync(Request(legacy), default);
            Expect(result.Title == "山田的休息 1" && keys == 1 && cloud.Calls == 1 && local.Calls == 0);
        });
        await check("m10_invalid_endpoint_is_rejected_before_any_provider_or_credential", async () =>
        {
            var keys = 0; var cloud = new StubHttp(); var local = new StubHttp();
            using var router = new TranslationServiceRouter(_ => { keys++; return ValueTask.FromResult("synthetic-key"); }, cloud, local);
            await Error(() => router.TranslateAsync(Request(legacy with { Endpoint = "http://other.invalid/api/v1" }), default), ProviderError.Configuration);
            Expect(keys == 0 && cloud.Calls == 0 && local.Calls == 0);
        });
        await check("m10_local_transport_failure_and_redirect_never_fall_back_to_cloud", async () =>
        {
            foreach (var mode in new[] { "connection", "redirect", "authentication" })
            {
                var cloud = new StubHttp(); var local = new StubHttp { Handler = (_, _) => mode == "connection" ? throw new HttpRequestException("PRIVATE_ENDPOINT_DETAIL")
                    : Task.FromResult(Json(new { error = "PRIVATE_SERVER_BODY" }, mode == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.Unauthorized)) };
                using var router = new TranslationServiceRouter(_ => throw new Exception("No cloud key"), cloud, local);
                await Error(() => router.TranslateAsync(Request(), default), mode == "connection" ? ProviderError.TransportUncertain : mode == "redirect" ? ProviderError.Configuration : ProviderError.Authentication);
                Expect(local.Calls == 1 && cloud.Calls == 0);
            }
        });
        await check("m10_local_request_rejects_tools_stateful_history_and_changed_reasoning", async () =>
        {
            foreach (var mutation in new[] { "reasoning", "store", "integrations", "previous_response_id" })
            {
                var frozen = profile.Apply(legacy); var parameters = JsonNode.Parse(frozen.ParametersJson)!;
                if (mutation == "reasoning") parameters[mutation] = "on";
                else if (mutation == "store") parameters[mutation] = true;
                else if (mutation == "integrations") parameters[mutation] = new JsonArray("remote-tool");
                else parameters[mutation] = "prior-chat";
                var handler = new StubHttp(); using var provider = new LmStudioTitleProvider(handler);
                await Error(() => provider.TranslateAsync(Request(frozen with { ParametersJson = parameters.ToJsonString() }), default), ProviderError.Configuration);
                Expect(handler.Calls == 0);
            }
        });
        await check("m10_local_response_model_budget_and_output_structure_are_checked", async () =>
        {
            foreach (var mode in new[] { "model", "budget", "tool", "multiple", "badstats" })
            {
                var response = JsonNode.Parse(JsonSerializer.Serialize(Native(model: mode == "model" ? "different" : "local-model", generated: mode == "budget" ? 512 : 20)))!;
                if (mode == "tool") response["output"]![1]!["type"] = "tool_call";
                if (mode == "multiple") response["output"]!.AsArray().Add(new JsonObject { ["type"] = "message", ["content"] = "extra" });
                if (mode == "badstats") response["stats"]!["input_tokens"] = -1;
                using var provider = new LmStudioTitleProvider(new StubHttp { Handler = (_, _) => Task.FromResult(Json(response)) });
                var result = await provider.TranslateAsync(Request(), default);
                Expect(result.Title is null && result.ErrorCategory == (mode == "model" ? "ModelMismatch" : mode == "budget" ? "TruncatedOrIncomplete" : "InvalidNativeResponse"));
            }
        });
        await check("m10_local_response_size_is_bounded_without_retaining_body", async () =>
        {
            using var provider = new LmStudioTitleProvider(new StubHttp { Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(new string('x', 262145)) }) });
            var result = await provider.TranslateAsync(Request(), default); Expect(result.ErrorCategory == "ResponseTooLarge" && result.Title is null);
        });
        await check("m10_model_listing_is_get_only_and_local_never_accesses_credentials", async () =>
        {
            var keys = 0; var cloud = new StubHttp(); var local = new StubHttp { Handler = (request, _) =>
            {
                Expect(request.Method == HttpMethod.Get && request.Content is null && request.Headers.Authorization is null && request.RequestUri!.AbsoluteUri == profile.Endpoint + "/models");
                return Task.FromResult(Json(new { models = new object[] {
                    new { key = "publisher/file", type = "llm", display_name = "Local model", loaded_instances = new[] { new { id = "local-model" } } },
                    new { key = "unloaded-model", type = "llm", loaded_instances = Array.Empty<object>() },
                    new { key = "embedding-model", type = "embedding", loaded_instances = Array.Empty<object>() } } }));
            } };
            using var router = new TranslationServiceRouter(_ => { keys++; return ValueTask.FromResult("synthetic-key"); }, cloud, local);
            var models = await router.ListModelsAsync(profile, default);
            Expect(models.Length == 2 && models[0].Id == "local-model" && models[0].Loaded == true && models[1].Loaded == false && keys == 0 && cloud.Calls == 0);
        });
        await check("m10_groq_model_listing_uses_fixed_cloud_endpoint_without_title", async () =>
        {
            var handler = new StubHttp { Handler = (request, _) =>
            {
                Expect(request.Method == HttpMethod.Get && request.Content is null && request.RequestUri!.AbsoluteUri == TitlePipeline.GroqEndpoint + "/models"
                    && request.Headers.Authorization?.Parameter == "synthetic-key");
                return Task.FromResult(Json(new { data = new[] { new { id = "synthetic-model" } } }));
            } };
            using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("synthetic-key"), handler);
            Expect((await provider.ListModelsAsync(default)).Single().Id == "synthetic-model");
        });
        await check("m10_groq_oversized_output_budget_is_configuration_not_waitable_rate_limit", async () =>
        {
            var message = "Request too large for model private-model in organization PRIVATE-ORG on output tokens per minute (OTPM): Limit 1000, Requested 1024. PRIVATE_SOURCE_DO_NOT_SAVE";
            using var provider = new GroqTitleProvider(_ => ValueTask.FromResult("synthetic-key"), new StubHttp { Handler = (_, _) =>
                Task.FromResult(Json(new { error = new { code = "rate_limit_exceeded", message } }, HttpStatusCode.TooManyRequests)) });
            try { await provider.TranslateAsync(Request(legacy), default); throw new InvalidOperationException("Missing limit error"); }
            catch (TitleProviderException error)
            {
                Expect(error.Category == ProviderError.Configuration && error.Diagnostics!.ServiceCode == "output_budget_exceeds_limit"
                    && error.Diagnostics.HttpStatus == 429 && !JsonSerializer.Serialize(error.Diagnostics).Contains("PRIVATE"));
            }
        });
    }
}
