using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Localizer.Core;

namespace Localizer.Translation;

internal static class GroqDiagnostics
{
    // This is an exact allowlist, not a scrubber: arbitrary server text never reaches a journal.
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "invalid_api_key", "model_not_found", "model_decommissioned", "model_not_supported", "invalid_model",
        "invalid_request_error", "invalid_parameter", "unsupported_parameter", "unsupported_value",
        "rate_limit_exceeded", "tokens", "requests", "insufficient_quota", "quota_exceeded", "billing_hard_limit_reached",
        "context_length_exceeded", "json_validate_failed", "content_policy_violation", "invalid_json", "output_budget_exceeds_limit"
    };
    public static ProviderDiagnostics Read(HttpResponseMessage response, long elapsedMs, string? body = null)
    {
        string? code = null; long? prompt = null, completion = null, total = null;
        if (body is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String && Codes.Contains(value.GetString()!)) code = value.GetString();
                    // A request exceeding the entire output-per-minute bucket cannot succeed by waiting.
                    // Inspect only the bounded transient message; persist a fixed classification, never its text.
                    if (response.StatusCode == HttpStatusCode.TooManyRequests && code == "rate_limit_exceeded"
                        && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                        && message.GetString() is { Length: <= 8192 } explanation)
                    {
                        var match = Regex.Match(explanation,
                            @"Request too large\b[\s\S]*?(?:output tokens per minute|\(OTPM\))[\s\S]*?Limit\s*:?\s*(\d+)\s*,?\s*Requested\s*:?\s*(\d+)",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                        if (match.Success && long.TryParse(match.Groups[1].Value, out var limit)
                            && long.TryParse(match.Groups[2].Value, out var requested) && limit > 0 && requested > limit)
                            code = "output_budget_exceeds_limit";
                    }
                }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    prompt = Usage(usage, "prompt_tokens"); completion = Usage(usage, "completion_tokens"); total = Usage(usage, "total_tokens");
                }
            }
            catch (JsonException) { }
        }
        return new((int)response.StatusCode, code, elapsedMs, prompt, completion, total,
            Number(response, "x-ratelimit-limit-requests"), Number(response, "x-ratelimit-remaining-requests"),
            Number(response, "x-ratelimit-limit-tokens"), Number(response, "x-ratelimit-remaining-tokens"),
            Duration(Header(response, "x-ratelimit-reset-requests")), Duration(Header(response, "x-ratelimit-reset-tokens")), Retry(response));
    }
    public static ProviderError Category(HttpStatusCode status, string? code) => status switch
    {
        HttpStatusCode.Unauthorized => ProviderError.Authentication,
        HttpStatusCode.Forbidden => ProviderError.Permission,
        HttpStatusCode.NotFound => ProviderError.ModelUnavailable,
        HttpStatusCode.TooManyRequests when code == "output_budget_exceeds_limit" => ProviderError.Configuration,
        HttpStatusCode.TooManyRequests when code is "insufficient_quota" or "quota_exceeded" or "billing_hard_limit_reached" => ProviderError.QuotaExceeded,
        HttpStatusCode.TooManyRequests => ProviderError.RateLimited,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity when code is "invalid_api_key" => ProviderError.Authentication,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity when code is "insufficient_quota" or "quota_exceeded" or "billing_hard_limit_reached" => ProviderError.QuotaExceeded,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity when code is "model_not_found" or "model_decommissioned" or "model_not_supported" or "invalid_model" => ProviderError.ModelUnavailable,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity when code is "invalid_parameter" or "unsupported_parameter" or "unsupported_value" => ProviderError.Configuration,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => ProviderError.RequestRejected,
        _ => (int)status >= 500 ? ProviderError.TransportUncertain : ProviderError.Configuration
    };
    public static async Task<string?> Body(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream(); var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token); if (read == 0) break;
            if (buffer.Length + read > 262144) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static long? Usage(JsonElement usage, string name) => usage.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number
        && n.TryGetInt64(out var value) && value is >= 0 and <= 100000000 ? value : null;
    private static string? Header(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        var all = values.Take(2).ToArray(); return all.Length == 1 && all[0].Length <= 128 ? all[0] : null;
    }
    private static long? Number(HttpResponseMessage response, string name) => long.TryParse(Header(response, name), NumberStyles.None,
        CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100000000 ? value : null;
    private static long? Retry(HttpResponseMessage response)
    {
        var value = Header(response, "Retry-After");
        if (double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds <= 604800)
            return (long)Math.Ceiling(seconds * 1000);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return (long)Math.Clamp(Math.Ceiling((date - DateTimeOffset.UtcNow).TotalMilliseconds), 0, 604800000);
        return null;
    }
    private static long? Duration(string? value)
    {
        if (value is null) return null;
        var matches = Regex.Matches(value, @"(\d+(?:\.\d+)?)(ms|s|m|h|d)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (matches.Count == 0 || string.Concat(matches.Select(x => x.Value)) != value) return null;
        var milliseconds = 0d;
        foreach (Match match in matches)
        {
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return null;
            milliseconds += n * (match.Groups[2].Value switch { "ms" => 1, "s" => 1000, "m" => 60000, "h" => 3600000, _ => 86400000 });
        }
        return milliseconds is >= 0 and <= 604800000 ? (long)Math.Ceiling(milliseconds) : null;
    }
}
