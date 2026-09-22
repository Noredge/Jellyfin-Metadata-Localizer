using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Localizer.Translation;

/// <summary>Single request, no retries or fallback; synopsis budgets are independent of title budgets.</summary>
public sealed class OverviewProvider(Func<string> credential, HttpMessageHandler? testHandler = null)
{
    public async Task<OverviewTranslationOutput> TranslateAsync(TranslationServiceProfile service,
        OverviewTranslationInput input, CancellationToken token)
    {
        service.RequireConfigured();
        using var lease = await TranslationDispatch.EnterAsync(token);
        using var client = new HttpClient(testHandler ?? (service.Kind == "openai-compatible" ? PublicCloudEndpoint.CreateHandler()
            : new HttpClientHandler { AllowAutoRedirect = false, UseProxy = service.Kind != "lmstudio" })) { Timeout = TimeSpan.FromSeconds(180) };
        object body = service.Kind == "openai-compatible" ? CompatibleChat.Body(service.Model, input.Prompt,
            input.Protection.MaskedSource, 3200, service.UseJsonResponseFormat) : service.Kind == "openai" ? new { model = service.Model,
            messages = new[] { new { role = "system", content = input.Prompt }, new { role = "user", content = input.Protection.MaskedSource } },
            max_completion_tokens = 3200, reasoning_effort = "none", response_format = new { type = "json_object" }, store = false }
            : service.Kind == "groq" ? new { model = service.Model,
            messages = new[] { new { role = "system", content = input.Prompt }, new { role = "user", content = input.Protection.MaskedSource } },
            temperature = 0.2, max_completion_tokens = 3200, reasoning_effort = "none", response_format = new { type = "json_object" } }
            : new { model = service.Model, system_prompt = input.Prompt, input = input.Protection.MaskedSource,
                temperature = 0.2, max_output_tokens = 3200, reasoning = "off", store = false, integrations = Array.Empty<string>(), stream = false };
        using var message = new HttpRequestMessage(HttpMethod.Post, service.CanonicalEndpoint()
            + (service.Kind != "lmstudio" ? "/chat/completions" : "/chat"));
        if (service.Kind != "lmstudio")
        {
            string key;
            try { key = credential(); } catch { return new(null, "authentication"); }
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsWhiteSpace)) return new(null, "authentication");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var reply = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            if (!reply.IsSuccessStatusCode) return new(null, reply.StatusCode switch {
                HttpStatusCode.TooManyRequests => "rate_limit", HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication",
                HttpStatusCode.NotFound => "service_unavailable", _ => "invalid_output" });
            var raw = await GroqDiagnostics.Body(reply, token);
            if (raw is null) return new(null, "invalid_output");
            using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
            if (service.Kind != "lmstudio")
            {
                var choice = root.GetProperty("choices")[0]; var msg = choice.GetProperty("message");
                if (choice.GetProperty("finish_reason").GetString() == "content_filter"
                    || msg.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(refusal.GetString())) return new(null, "refusal");
                if (service.Kind == "openai-compatible" && choice.GetProperty("finish_reason").GetString() is not ("stop" or "length"))
                    return new(null, "invalid_output");
                return OverviewPipeline.Parse(msg.GetProperty("content").GetString() ?? "", input,
                    choice.GetProperty("finish_reason").GetString() == "length");
            }
            var text = string.Concat(root.GetProperty("output").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "message")
                .Select(x => x.GetProperty("content").GetString()));
            var clipped = root.TryGetProperty("stats", out var stats) && stats.TryGetProperty("total_output_tokens", out var count) && count.GetInt64() >= 3200;
            return OverviewPipeline.Parse(text, input, clipped);
        }
        catch (OperationCanceledException) { return new(null, token.IsCancellationRequested ? "interrupted" : "network"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return new(null, "network"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        { return new(null, "invalid_output"); }
    }
}
