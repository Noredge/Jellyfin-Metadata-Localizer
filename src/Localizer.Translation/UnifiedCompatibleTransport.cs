using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Localizer.Core;
using Localizer.Unified;

namespace Localizer.Translation;

/// <summary>One HTTP attempt, no scheduling, retries, fallback or detached transport tasks.</summary>
public sealed class UnifiedCompatibleTransport : IUnifiedTransport, IUnifiedModelCatalog, IDisposable
{
    private readonly HttpClient client;
    private readonly Func<string, string, CancellationToken, ValueTask<string>> credential;
    private readonly Func<string, TitleRules> rules;
    public UnifiedCompatibleTransport(Func<string, string, CancellationToken, ValueTask<string>> credential,
        Func<string, TitleRules> rules, HttpMessageHandler? testHandler = null)
    {
        this.credential = credential; this.rules = rules;
        client = PublicCloudEndpoint.CreateClient(testHandler, Timeout.InfiniteTimeSpan);
    }
    public string PromptVersion(string field, string language) => field switch
    {
        "Name" => "unified-title-v1/" + rules(language).Version,
        "Overview" => "unified/" + OverviewPipeline.Version,
        "Term" => "unified/" + GenreTranslationPipeline.Version,
        _ => throw new ArgumentException("InvalidGenerationField")
    };
    public async Task<string[]> ListModelsAsync(ModelCatalogConnection connection, CancellationToken token)
    {
        var endpoint = PublicCloudEndpoint.Canonicalize(connection.Endpoint);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var key = connection.ApiKey ?? await credential(connection.CredentialReference ?? "", endpoint, deadline.Token);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new InvalidOperationException("credential_unavailable");
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint + "/models");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("model_catalog_unavailable");
        var raw = await GroqDiagnostics.Body(response, deadline.Token) ?? throw new InvalidOperationException("model_catalog_unavailable");
        using var document = JsonDocument.Parse(raw); var data = document.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 2000) throw new InvalidOperationException("model_catalog_unavailable");
        var models = data.EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
        if (models.Any(x => !TranslationServiceProfile.ValidModel(x))) throw new InvalidOperationException("model_catalog_unavailable");
        return models.Select(x => x!).ToArray();
    }
    public async Task<GenerationReply> GenerateAsync(GenerationInput input, ConnectionVersion connection, Action markSending, CancellationToken token)
    {
        var sent = false; string? raw = null; ProviderDiagnostics? provider = null; string? finish = null;
        var stage = "prepare"; TranslationValidation? validation = null; int? outputLimit = null;
        GenerationReply Fail(string code, string? error = null, bool unknown = false) => new(null, error ?? "invalid_output", provider?.HttpStatus,
            new(stage, validation?.Code ?? code, raw?.Length, raw is null ? null : UnifiedDatabase.Hash(raw), finish,
                Validation: validation is null ? null : JsonSerializer.SerializeToElement(validation), OutputTokenLimit: outputLimit),
            provider is null ? null : new(provider.PromptTokens, provider.CompletionTokens, provider.TotalTokens), provider?.RetryAfterMs, !sent, unknown);
        try
        {
            var endpoint = PublicCloudEndpoint.Canonicalize(connection.Endpoint);
            if (!TranslationServiceProfile.ValidModel(connection.Model)) return Fail("invalid_model", "configuration");
            var language = Languages.Parse(input.Language);
            if (input.PromptVersion != PromptVersion(input.Field, input.Language)) return Fail("prompt_version_changed", "configuration");
            try { ConnectionParameters.Validate(connection.ParametersJson); }
            catch (ConnectionParameterException) { return Fail("invalid_parameters", "configuration"); }
            using var options = JsonDocument.Parse(connection.ParametersJson);
            var p = options.RootElement;
            int Maximum(int fallback) => p.TryGetProperty("MaxOutputTokens", out var v) ? v.GetInt32() : fallback;
            int OverviewMaximum() => p.TryGetProperty("OverviewMaxOutputTokens", out var v) ? v.GetInt32() : Math.Max(4096, Maximum(3200));
            bool JsonMode() => !p.TryGetProperty("UseJsonResponseFormat", out var v) || v.GetBoolean();
            var tokenParameter = p.TryGetProperty("TokenParameter", out var parameter) ? parameter.GetString()! : "max_tokens";
            var reasoning = p.TryGetProperty("ReasoningEffort", out var effort) && effort.ValueKind != JsonValueKind.Null ? effort.GetString() : null;
            bool? store = p.TryGetProperty("Store", out var saved) && saved.ValueKind != JsonValueKind.Null ? saved.GetBoolean() : null;
            if (tokenParameter is not ("max_tokens" or "max_completion_tokens") || Maximum(1024) is < 64 or > 32768 || OverviewMaximum() is < 64 or > 32768
                || reasoning is not (null or "none" or "minimal" or "low" or "medium" or "high")) return Fail("invalid_parameters", "configuration");
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var names = JsonDocument.Parse(input.NamesJson)) foreach (var name in names.RootElement.EnumerateArray())
            {
                var original = name.GetProperty("original").GetString()!;
                var translated = name.GetProperty("replacement").ValueKind == JsonValueKind.String ? name.GetProperty("replacement").GetString()! : original;
                replacements.TryAdd(original, translated);
                foreach (var alias in name.GetProperty("aliases").EnumerateArray()) replacements.TryAdd(alias.GetString()!, translated);
            }
            using (var context = JsonDocument.Parse(input.ContextJson)) if (context.RootElement.TryGetProperty("protected_names", out var names))
                foreach (var name in names.EnumerateArray()) replacements.TryAdd(name.GetString()!, name.GetString()!);
            var matching = replacements.Keys.Where(x => input.Text.Contains(x, StringComparison.Ordinal)).ToArray();
            ProtectedTitle Decorate(ProtectedTitle protection) => protection with
            {
                DisplayNames = protection.Mapping.ToDictionary(x => x.Key, x => replacements.GetValueOrDefault(x.Value, x.Value)),
                PersonIds = protection.Mapping.ToDictionary(x => x.Key, _ => ""), NameRevision = 0
            };
            TitleProviderRequest? titleRequest = null; OverviewTranslationInput? overview = null; GenreTranslationTerm[]? terms = null;
            string prompt, payload; int budget;
            if (input.Field == "Name")
            {
                var source = new SourceSnapshot(1, new("unified", "unified", input.SourceId), 1, input.Text, "", UnifiedDatabase.Hash(input.Text));
                var spec = TitlePipeline.Prepare(source, language, matching, rules(input.Language));
                var protection = Decorate(JsonSerializer.Deserialize<ProtectedTitle>(spec.ContextJson)!);
                spec = spec with { Endpoint = endpoint, Model = connection.Model, ContextJson = JsonSerializer.Serialize(protection), PromptVersion = input.PromptVersion };
                titleRequest = new(source, language, spec); prompt = spec.PromptText; payload = protection.MaskedSource; budget = Maximum(1024);
            }
            else if (input.Field == "Overview")
            {
                overview = OverviewPipeline.Prepare(input.Text, language, matching);
                overview = overview with { Protection = Decorate(overview.Protection), PromptVersion = input.PromptVersion };
                prompt = overview.Prompt; payload = overview.Protection.MaskedSource; budget = OverviewMaximum();
            }
            else if (input.Field == "Term")
            {
                terms = [new("G0001", input.Text, [language])]; prompt = GenreTranslationPipeline.Prompt;
                payload = GenreTranslationPipeline.Payload(terms); budget = Maximum(1024);
            }
            else return Fail("invalid_field", "configuration");
            outputLimit = budget;
            string key;
            try { key = await credential(connection.CredentialReference ?? "", endpoint, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return Fail("credential_unavailable", "credential_unavailable"); }
            if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return Fail("credential_unavailable", "credential_unavailable");
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            message.Content = new StringContent(CompatibleChat.Body(connection.Model, prompt, payload, budget, JsonMode(), tokenParameter, reasoning, store).ToJsonString(), Encoding.UTF8, "application/json");
            token.ThrowIfCancellationRequested(); markSending(); sent = true; stage = "transport";
            var watch = Stopwatch.StartNew();
            // Await the actual network/body operation. The unique executor owns deadline and late-result policy.
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            provider = GroqDiagnostics.Read(response, watch.ElapsedMilliseconds);
            raw = await GroqDiagnostics.Body(response, token);
            provider = GroqDiagnostics.Read(response, watch.ElapsedMilliseconds, raw);
            if (!response.IsSuccessStatusCode)
            {
                var category = GroqDiagnostics.Category(response.StatusCode, provider.ServiceCode);
                var error = category switch { ProviderError.Authentication => "authentication", ProviderError.Permission => "permission", ProviderError.QuotaExceeded => "quota",
                    ProviderError.RateLimited => "rate_limit", ProviderError.ModelUnavailable => "service_unavailable", ProviderError.TransportUncertain => "network_uncertain", _ => "configuration" };
                return Fail("http_error", error, category == ProviderError.TransportUncertain);
            }
            if (raw is null) return Fail("response_too_large");
            stage = "envelope";
            using var document = JsonDocument.Parse(raw);
            var choice = document.RootElement.GetProperty("choices")[0];
            finish = choice.GetProperty("finish_reason").GetString();
            if (finish is { Length: > 80 }) finish = "invalid";
            var msg = choice.GetProperty("message");
            if (finish == "content_filter" || msg.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refusal.GetString())) return Fail("provider_refusal", "refusal");
            if (finish != "stop") return Fail("incomplete_output", "output_budget");
            stage = "content"; string? text, errorCode; string[]? warnings;
            if (titleRequest is not null)
            {
                var result = TitlePipeline.Parse(raw, titleRequest, allowModelAlias: true);
                text = result.Title; errorCode = result.ErrorCategory; warnings = result.Warnings;
            }
            else if (overview is not null)
            {
                var result = OverviewPipeline.Parse(msg.GetProperty("content").GetString()!, overview with
                    { Protection = overview.Protection with { Mapping = overview.Protection.DisplayNames! } });
                text = result.Text; errorCode = result.Error; warnings = result.Warnings; validation = result.Validation;
            }
            else
            {
                var result = GenreTranslationPipeline.Parse(msg.GetProperty("content").GetString()!, terms!);
                var candidate = result.Candidates.SingleOrDefault(); text = candidate?.Text; errorCode = result.Error ?? candidate?.Error; warnings = candidate?.Warnings;
            }
            if (errorCode is not null || string.IsNullOrWhiteSpace(text)) return Fail("invalid_translated_content", errorCode is "refusal" or "Refused" ? "refusal" : errorCode == "output_budget" ? "output_budget" : "invalid_output");
            return new(text, HttpStatus: provider.HttpStatus, Diagnostics: new(stage, "usable", raw.Length, UnifiedDatabase.Hash(raw), finish, warnings,
                validation is null ? null : JsonSerializer.SerializeToElement(validation), outputLimit),
                Usage: new(provider.PromptTokens, provider.CompletionTokens, provider.TotalTokens));
        }
        catch (ExecutionSupersededException) { throw; }
        catch (SourceTemporarilyUnavailableException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { return Fail("transport_interrupted", sent ? "network_uncertain" : "network", sent && provider?.HttpStatus is not >= 400); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException or FormatException or OverflowException)
        { return Fail("invalid_" + stage, sent ? "invalid_output" : "configuration"); }
    }
    public void Dispose() => client.Dispose();
}
