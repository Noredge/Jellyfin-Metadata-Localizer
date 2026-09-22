using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Localizer.Core;

namespace Localizer.Translation;

/// <summary>Generic OpenAI Chat Completions transport. Credentials are resolved on demand and never stored in the task snapshot.</summary>
public sealed class CompatibleTitleProvider : ITitleProvider, IDisposable
{
    private readonly HttpClient client;
    private readonly Func<string, string, CancellationToken, ValueTask<string>> credential;
    // A custom handler is for offline HTTP contract tests. Production redirects are always disabled.
    public CompatibleTitleProvider(Func<string, string, CancellationToken, ValueTask<string>> credential, HttpMessageHandler? testHandler = null)
    {
        this.credential = credential;
        client = new HttpClient(testHandler ?? PublicCloudEndpoint.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<TitleProviderResult> TranslateAsync(TitleProviderRequest request, CancellationToken token)
    {
        JsonObject parameters; ProtectedTitle protection;
        try
        {
            if (request.Spec.ServiceKind != "openai-compatible" || !TranslationServiceProfile.ValidId(request.Spec.ServiceId)) throw new ArgumentException();
            _ = PublicCloudEndpoint.Canonicalize(request.Spec.Endpoint);
            TranslationServiceProfile.RequireConfiguredModel(request.Spec.Model);
            protection = TitlePipeline.ValidateContext(request);
            parameters = JsonNode.Parse(request.Spec.ParametersJson) as JsonObject ?? throw new ArgumentException();
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "max_tokens", "response_format" };
            if (parameters.Any(x => !allowed.Contains(x.Key)) || parameters["max_tokens"]?.GetValue<int>() is not (>= 64 and <= 4096)
                || parameters.TryGetPropertyValue("response_format", out var format) && (format is not JsonObject obj
                    || obj.Count != 1 || obj["type"]?.GetValue<string>() != "json_object")) throw new ArgumentException();
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
        { throw new TitleProviderException(ProviderError.Configuration); }
        var key = await LoadKeyAsync(request.Spec.ServiceId!, request.Spec.Endpoint, token);
        parameters["model"] = request.Spec.Model;
        parameters["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = request.Spec.PromptText },
            new JsonObject { ["role"] = "user", ["content"] = protection.MaskedSource });
        using var message = new HttpRequestMessage(HttpMethod.Post, PublicCloudEndpoint.Canonicalize(request.Spec.Endpoint) + "/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = new StringContent(parameters.ToJsonString(), Encoding.UTF8, "application/json");
        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try { response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException) { throw new TitleProviderException(ProviderError.TransportUncertain, diagnostics: new(ElapsedMs: watch.ElapsedMilliseconds)); }
        using (response)
        {
            string? body;
            try { body = await GroqDiagnostics.Body(response, token); }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                var details = GroqDiagnostics.Read(response, watch.ElapsedMilliseconds);
                // A known non-success HTTP status remains useful even if its optional diagnostic body is unreadable.
                var category = response.IsSuccessStatusCode ? ProviderError.TransportUncertain : GroqDiagnostics.Category(response.StatusCode, null);
                throw new TitleProviderException(category, details.RetryAfterMs is { } retry ? TimeSpan.FromMilliseconds(retry) : null, details);
            }
            var diagnostics = GroqDiagnostics.Read(response, watch.ElapsedMilliseconds, body);
            if (!response.IsSuccessStatusCode)
                throw new TitleProviderException(GroqDiagnostics.Category(response.StatusCode, diagnostics.ServiceCode),
                    diagnostics.RetryAfterMs is { } delay ? TimeSpan.FromMilliseconds(delay) : null, diagnostics);
            var result = body is null ? new TitleProviderResult(null, "ResponseTooLarge", []) : TitlePipeline.Parse(body, request, allowModelAlias: true);
            return result with { Diagnostics = diagnostics, TotalTokens = diagnostics.TotalTokens ?? result.TotalTokens };
        }
    }
    private async Task<string> LoadKeyAsync(string id, string endpoint, CancellationToken token)
    {
        string key;
        try { key = await credential(id, endpoint, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new TitleProviderException(ProviderError.CredentialUnavailable); }
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new TitleProviderException(ProviderError.CredentialUnavailable);
        return key;
    }
    public async Task<TranslationModelInfo[]> ListModelsAsync(TranslationServiceProfile profile, CancellationToken token)
    {
        profile.Validate();
        if (profile.Kind != "openai-compatible") throw new TitleProviderException(ProviderError.Configuration);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var message = new HttpRequestMessage(HttpMethod.Get, profile.CanonicalEndpoint() + "/models");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await LoadKeyAsync(profile.Id, profile.CanonicalEndpoint(), linked.Token));
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var text = await GroqDiagnostics.Body(response, linked.Token);
            var diagnostics = GroqDiagnostics.Read(response, watch.ElapsedMilliseconds, text);
            if (!response.IsSuccessStatusCode)
                throw new TitleProviderException(GroqDiagnostics.Category(response.StatusCode, diagnostics.ServiceCode), diagnostics: diagnostics);
            if (text is null) throw new JsonException();
            using var document = JsonDocument.Parse(text);
            var models = document.RootElement.GetProperty("data");
            if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 2000) throw new JsonException();
            return models.EnumerateArray().Select(x =>
            {
                var id = x.GetProperty("id").GetString();
                if (!TranslationServiceProfile.ValidModel(id)) throw new JsonException();
                return new TranslationModelInfo(id!);
            }).DistinctBy(x => x.Id).ToArray();
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { throw new TitleProviderException(ProviderError.TransportUncertain, diagnostics: new(ElapsedMs: watch.ElapsedMilliseconds)); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new TitleProviderException(ProviderError.Configuration); }
    }
    public void Dispose() => client.Dispose();
}
