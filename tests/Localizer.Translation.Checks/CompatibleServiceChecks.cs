using System.Net;
using System.Text;
using System.Text.Json;
using Localizer.Core;
using Localizer.Translation;

public static class CompatibleServiceChecks
{
    private static void Expect(bool value) { if (!value) throw new InvalidOperationException("Compatible service assertion failed."); }
    private static HttpResponseMessage Response(object content, string finish = "stop") => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { model = "resolved-model-revision", choices = new[] {
        new { finish_reason = finish, message = new { content = JsonSerializer.Serialize(content) } } } }), Encoding.UTF8, "application/json") };
    private static async Task Error(Func<Task> action, ProviderError category)
    { try { await action(); } catch (TitleProviderException error) { Expect(error.Category == category); return; } throw new Exception("Expected provider error."); }
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        var profile = new TranslationServiceProfile("cloud-a", "openai-compatible", "https://api.example.com/custom/v1", "alias-model", 1024, "Example cloud");
        var source = new SourceSnapshot(1, new("synthetic", "movies", "movie"), 1, "Up", "", "synthetic");
        var original = TitlePipeline.Prepare(source, TargetLanguage.SimplifiedChinese, [], new("test", "Translate the supplied title.", []));
        TitleProviderRequest Request(TranslationServiceProfile? service = null) => new(source, TargetLanguage.SimplifiedChinese, (service ?? profile).Apply(original));
        await check("compatible_title_freezes_identity_and_uses_only_generic_chat_fields", async () =>
        {
            var loaded = new List<string>();
            var handler = new StubHttp { Handler = async (request, token) =>
            {
                Expect(request.RequestUri!.AbsoluteUri == profile.Endpoint + "/chat/completions");
                Expect(request.Headers.Authorization!.Parameter == "synthetic-cloud-a");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Expect(body.RootElement.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[] { "max_tokens", "messages", "model" }));
                Expect(body.RootElement.GetProperty("model").GetString() == "alias-model" && body.RootElement.GetProperty("max_tokens").GetInt32() == 1024);
                return Response(new { title = "飞屋环游记" });
            } };
            using var router = new TranslationServiceRouter(_ => throw new Exception("Must not load Groq key"),
                openAiCredential: _ => throw new Exception("Must not load OpenAI key"), compatibleCredential: (id, endpoint, _) =>
                { Expect(endpoint == profile.Endpoint); loaded.Add(id); return ValueTask.FromResult("synthetic-" + id); }, compatibleTestHandler: handler);
            var frozen = Request();
            Expect(frozen.Spec.ServiceId == profile.Id && frozen.Spec.ServiceKind == profile.Kind);
            Expect((await router.TranslateAsync(frozen, default)).Title == "飞屋环游记" && loaded.SequenceEqual(new[] { "cloud-a" }));
        });
        await check("compatible_profiles_on_builtin_endpoint_still_use_own_key_and_payload", async () =>
        {
            var service = profile with { Endpoint = TranslationServiceProfile.OpenAiEndpoint, Id = "cloud-b", UseJsonResponseFormat = true };
            var handler = new StubHttp { Handler = async (request, token) =>
            {
                Expect(request.Headers.Authorization!.Parameter == "synthetic-cloud-b");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Expect(body.RootElement.GetProperty("response_format").GetProperty("type").GetString() == "json_object");
                Expect(!body.RootElement.TryGetProperty("reasoning_effort", out _) && !body.RootElement.TryGetProperty("store", out _));
                return Response(new { title = "飞屋环游记" });
            } };
            using var router = new TranslationServiceRouter(_ => throw new Exception("Wrong built-in key"),
                openAiCredential: _ => throw new Exception("Wrong built-in key"), compatibleCredential: (id, endpoint, _) =>
                { Expect(id == "cloud-b" && endpoint == service.Endpoint); return ValueTask.FromResult("synthetic-cloud-b"); }, compatibleTestHandler: handler);
            Expect((await router.TranslateAsync(Request(service), default)).Title is not null);
        });
        await check("compatible_missing_credentials_never_fall_back_or_send", async () =>
        {
            var handler = new StubHttp(); var keys = 0;
            using var router = new TranslationServiceRouter(_ => { keys++; return ValueTask.FromResult("wrong-key"); },
                openAiCredential: _ => { keys++; return ValueTask.FromResult("wrong-key"); }, compatibleTestHandler: handler);
            await Error(() => router.TranslateAsync(Request(), default), ProviderError.CredentialUnavailable);
            await Error(() => router.TranslateAsync(Request() with { Spec = Request().Spec with { ServiceId = "../escape" } }, default), ProviderError.Configuration);
            await Error(() => router.TranslateAsync(Request() with { Spec = Request().Spec with { Endpoint = "https://127.0.0.1/v1" } }, default), ProviderError.Configuration);
            Expect(keys == 0 && handler.Calls == 0);
        });
        await check("compatible_model_listing_uses_profile_key_and_base_path", async () =>
        {
            var handler = new StubHttp { Handler = (request, _) =>
            {
                Expect(request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == profile.Endpoint + "/models"
                    && request.Headers.Authorization!.Parameter == "synthetic-list-key");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"alias-model\"},{\"id\":\"other-model\"},{\"id\":\"alias-model\"}]}") });
            } };
            using var router = new TranslationServiceRouter(_ => throw new Exception("Wrong key"), compatibleCredential: (id, endpoint, _) =>
            { Expect(id == profile.Id && endpoint == profile.Endpoint); return ValueTask.FromResult("synthetic-list-key"); }, compatibleTestHandler: handler);
            var models = await router.ListModelsAsync(profile with { Model = TranslationServiceProfile.UnconfiguredModel }, default);
            Expect(models.Select(x => x.Id).SequenceEqual(new[] { "alias-model", "other-model" }));
        });
        await check("compatible_refusal_clipping_and_invalid_json_stay_invalid", async () =>
        {
            foreach (var finish in new[] { "length", "content_filter" })
            {
                var handler = new StubHttp { Handler = (_, _) => Task.FromResult(Response(new { title = "飞屋环游记" }, finish)) };
                using var router = new TranslationServiceRouter(_ => throw new Exception("Wrong key"),
                    compatibleCredential: (_, _, _) => ValueTask.FromResult("synthetic-key"), compatibleTestHandler: handler);
                Expect((await router.TranslateAsync(Request(), default)).Title is null);
            }
            var invalid = new StubHttp { Handler = (_, _) => Task.FromResult(Response(new { unrelated = "not a title" })) };
            using var bad = new CompatibleTitleProvider((_, _, _) => ValueTask.FromResult("synthetic-key"), invalid);
            Expect((await bad.TranslateAsync(Request(), default)).Title is null);
        });
        await check("compatible_overview_and_genres_use_generic_payload_without_vendor_options", async () =>
        {
            var overviewHandler = new StubHttp { Handler = async (request, token) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Expect(request.RequestUri!.AbsoluteUri == profile.Endpoint + "/chat/completions"
                    && request.Headers.Authorization!.Parameter == "synthetic-overview-key");
                Expect(body.RootElement.EnumerateObject().Count() == 3 && body.RootElement.GetProperty("max_tokens").GetInt32() == 3200);
                return Response(new { overview = "A floating house begins a journey." });
            } };
            var input = OverviewPipeline.Prepare("A house begins a journey.", TargetLanguage.English, []);
            var overview = await new OverviewProvider(() => "synthetic-overview-key", overviewHandler).TranslateAsync(profile, input, default);
            Expect(overview.Error is null && overview.Text == "A floating house begins a journey.");
            var genreHandler = new StubHttp { Handler = async (request, token) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Expect(request.Headers.Authorization!.Parameter == "synthetic-genre-key" && body.RootElement.EnumerateObject().Count() == 4
                    && body.RootElement.GetProperty("max_tokens").GetInt32() == 1024
                    && body.RootElement.GetProperty("response_format").GetProperty("type").GetString() == "json_object");
                return Response(new { entries = new[] { new { id = "G0001", translations = new Dictionary<string, string> { ["zh-Hans"] = "冒险" } } } });
            } };
            var genres = await new GenreTranslationProvider(() => "synthetic-genre-key", genreHandler)
                .TranslateAsync(profile with { UseJsonResponseFormat = true }, [new("G0001", "Adventure", [TargetLanguage.SimplifiedChinese])], default);
            Expect(genres.Error is null && genres.Candidates.Single().Text == "冒险");
        });
        await check("legacy_generation_json_omits_optional_transport_identity", () =>
        {
            var serialized = JsonSerializer.Serialize(original);
            Expect(!serialized.Contains("ServiceKind") && !serialized.Contains("ServiceId"));
            Expect(JsonSerializer.Deserialize<GenerationSpec>(serialized) == original);
            var custom = profile.Apply(original);
            Expect(JsonSerializer.Deserialize<GenerationSpec>(JsonSerializer.Serialize(custom)) == custom);
            return Task.CompletedTask;
        });
    }
}
