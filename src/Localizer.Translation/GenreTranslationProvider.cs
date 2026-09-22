using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Localizer.Translation;

/// <summary>Dictionary-only transport. One bounded request; no retries, fallback or writes.</summary>
public sealed class GenreTranslationProvider(Func<string> credential, HttpMessageHandler? testHandler = null)
{
    public async Task<GenreTranslationResult> TranslateAsync(TranslationServiceProfile service,
        IReadOnlyList<GenreTranslationTerm> batch, CancellationToken token)
    {
        service.RequireConfigured();
        var payload = GenreTranslationPipeline.Payload(batch);
        if (service.MaxOutputTokens < 512) throw new ArgumentException("Dictionary output budget must be at least 512.");
        using var lease = await TranslationDispatch.EnterAsync(token);
        using var client = new HttpClient(testHandler ?? (service.Kind == "openai-compatible" ? PublicCloudEndpoint.CreateHandler()
            : new HttpClientHandler { AllowAutoRedirect = false, UseProxy = service.Kind != "lmstudio" })) { Timeout = TimeSpan.FromSeconds(180) };
        object body = service.Kind == "openai-compatible" ? CompatibleChat.Body(service.Model, GenreTranslationPipeline.Prompt,
            payload, service.MaxOutputTokens, service.UseJsonResponseFormat) : service.Kind == "openai" ? new { model = service.Model,
            messages = new[] { new { role = "system", content = GenreTranslationPipeline.Prompt }, new { role = "user", content = payload } },
            max_completion_tokens = service.MaxOutputTokens, reasoning_effort = "none", response_format = new { type = "json_object" }, store = false }
            : service.Kind == "groq" ? new { model = service.Model,
            messages = new[] { new { role = "system", content = GenreTranslationPipeline.Prompt }, new { role = "user", content = payload } },
            temperature = 0.2, max_completion_tokens = service.MaxOutputTokens, reasoning_effort = "none", response_format = new { type = "json_object" } }
            : new { model = service.Model, system_prompt = GenreTranslationPipeline.Prompt, input = payload,
                temperature = 0.2, max_output_tokens = service.MaxOutputTokens, reasoning = "off", store = false, integrations = Array.Empty<string>(), stream = false };
        using var message = new HttpRequestMessage(HttpMethod.Post, service.CanonicalEndpoint()
            + (service.Kind != "lmstudio" ? "/chat/completions" : "/chat"));
        if (service.Kind != "lmstudio")
        {
            string key;
            try { key = credential(); } catch (OperationCanceledException) { throw; } catch { return new([], "credential_unavailable"); }
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsWhiteSpace)) return new([], "credential_unavailable");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var reply = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            if (!reply.IsSuccessStatusCode) return new([], reply.StatusCode switch {
                HttpStatusCode.TooManyRequests => "rate_limit", HttpStatusCode.Unauthorized => "authentication", HttpStatusCode.Forbidden => "permission",
                HttpStatusCode.NotFound => "service_unavailable", _ => "invalid_output" });
            var raw = await GroqDiagnostics.Body(reply, token);
            if (raw is null) return new([], "invalid_output");
            using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
            if (service.Kind != "lmstudio")
            {
                var choice = root.GetProperty("choices")[0]; var msg = choice.GetProperty("message");
                if (choice.GetProperty("finish_reason").GetString() == "content_filter"
                    || msg.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(refusal.GetString())) return new([], "refusal");
                if (service.Kind == "openai-compatible" && choice.GetProperty("finish_reason").GetString() is not ("stop" or "length"))
                    return new([], "invalid_output");
                return GenreTranslationPipeline.Parse(msg.GetProperty("content").GetString() ?? "", batch,
                    choice.GetProperty("finish_reason").GetString() == "length");
            }
            var text = string.Concat(root.GetProperty("output").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "message")
                .Select(x => x.GetProperty("content").GetString()));
            var clipped = root.TryGetProperty("stats", out var stats) && stats.TryGetProperty("total_output_tokens", out var count) && count.GetInt64() >= service.MaxOutputTokens;
            return GenreTranslationPipeline.Parse(text, batch, clipped);
        }
        catch (OperationCanceledException) { return new([], "network_uncertain"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return new([], "network_uncertain"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        { return new([], "invalid_output"); }
    }
}
