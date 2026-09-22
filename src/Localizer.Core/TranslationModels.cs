namespace Localizer.Core;

public enum TranslationJobState { Ready, Running, Paused, CancelRequested, Cancelled, Completed, CompletedWithErrors, Interrupted }
public enum TranslationItemState { Pending, Requesting, Received, Succeeded, Cached, Invalid, Failed, SourceChanged, Uncertain }
public enum TranslationCheckpoint { RequestSaved, ResponseSaved, CandidateSaved }
// Append only: numeric values may exist in persisted task records.
public enum ProviderError { Authentication, Permission, ModelUnavailable, Configuration, RateLimited, TransportUncertain, CredentialUnavailable, RequestRejected, QuotaExceeded }
public sealed class TitleProviderException(ProviderError category, TimeSpan? retryAfter = null, ProviderDiagnostics? diagnostics = null) : Exception(category.ToString())
{
    public ProviderError Category { get; } = category;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public ProviderDiagnostics? Diagnostics { get; } = diagnostics;
}
// No raw headers, response body, provider messages, request IDs or credentials are persisted.
public sealed record ProviderDiagnostics(int? HttpStatus = null, string? ServiceCode = null, long? ElapsedMs = null,
    long? PromptTokens = null, long? CompletionTokens = null, long? TotalTokens = null,
    long? RequestLimit = null, long? RequestsRemaining = null, long? TokenLimit = null, long? TokensRemaining = null,
    long? RequestResetMs = null, long? TokenResetMs = null, long? RetryAfterMs = null);
public sealed record TranslationPolicy(int MaxAttempts = 2, int IntervalMs = 12000, int TimeoutMs = 60000, int RetryDelayMs = 12000,
    bool Adaptive = false, int RequestsPerMinute = 30, int TokensPerMinute = 8000, int MaxAutoWaitMs = 60000)
{
    public static TranslationPolicy AdaptiveGroq() => new(IntervalMs: 0, RetryDelayMs: 3000, Adaptive: true);
    internal void Validate()
    {
        if (MaxAttempts is < 1 or > 3 || IntervalMs is < 0 or > 60000 || TimeoutMs is < 1 or > 120000 || RetryDelayMs is < 0 or > 60000
            || RequestsPerMinute is < 1 or > 10000 || TokensPerMinute is < 1 or > 10000000 || MaxAutoWaitMs is < 0 or > 60000)
            throw new ArgumentException("Invalid bounded translation policy.");
    }
    /// <summary>Conservative fallback before response headers; successful usage can release unused completion reservation.</summary>
    public TimeSpan RequestSpacing(TitleProviderRequest request, ProviderDiagnostics? diagnostics = null)
    {
        if (!Adaptive) return TimeSpan.FromMilliseconds(IntervalMs);
        var completion = 1024L;
        try
        {
            using var parameters = System.Text.Json.JsonDocument.Parse(request.Spec.ParametersJson);
            if (parameters.RootElement.TryGetProperty("max_completion_tokens", out var value) && value.TryGetInt64(out var n) && n > 0)
                completion = Math.Min(n, 10000000);
        }
        catch (System.Text.Json.JsonException) { }
        // UTF-8 byte count is a conservative token estimate until actual usage is returned.
        var prompt = diagnostics?.PromptTokens ?? System.Text.Encoding.UTF8.GetByteCount(request.Spec.PromptText + request.Source.OriginalTitle) + 64L;
        var reservation = Math.Min(100000000L, prompt + completion);
        var tokenRate = Math.Min(TokensPerMinute, diagnostics?.TokenLimit is > 0 ? diagnostics.TokenLimit.Value : TokensPerMinute);
        // Groq's request limit header may describe the daily bucket, so it must not increase our RPM ceiling.
        var requestRate = Math.Min(RequestsPerMinute, diagnostics?.RequestLimit is > 0 ? diagnostics.RequestLimit.Value : RequestsPerMinute);
        var charged = diagnostics?.TotalTokens is > 0 && diagnostics.TokensRemaining >= reservation && diagnostics.TokenResetMs is not null
            ? diagnostics.TotalTokens.Value : reservation;
        var delay = Math.Max(IntervalMs, Math.Max(60000d / requestRate, 60000d * charged / tokenRate));
        if (diagnostics?.RequestsRemaining is 0 && diagnostics.RequestResetMs is { } requestReset) delay = Math.Max(delay, requestReset);
        if (diagnostics?.TokensRemaining is { } tokens && tokens < reservation && diagnostics.TokenResetMs is { } tokenReset) delay = Math.Max(delay, tokenReset);
        if (diagnostics?.RetryAfterMs is { } retry) delay = Math.Max(delay, retry);
        return TimeSpan.FromMilliseconds(Math.Ceiling(Math.Min(delay, 604800000d)));
    }
}
public sealed record TitleTranslationInput(MediaKey Key, GenerationSpec Spec);
public sealed record TitleProviderRequest(SourceSnapshot Source, TargetLanguage Language, GenerationSpec Spec);
public sealed record TitleProviderResult(string? Title, string? ErrorCategory, string[] Warnings, long? TotalTokens = null, ProviderDiagnostics? Diagnostics = null,
    string? NameTemplate = null);
public interface ITitleProvider
{
    // Respect cancellation. Implementations must not perform their own retries or fallback.
    Task<TitleProviderResult> TranslateAsync(TitleProviderRequest request, CancellationToken cancellationToken);
}
public sealed record TranslationJob(string Id, string Fingerprint, LibraryKey Scope, TargetLanguage Language,
    TranslationPolicy Policy, TranslationJobState State, string? ErrorCategory, string? NextRequestUtc, string CreatedUtc, string UpdatedUtc);
public sealed record TranslationItem(string JobId, int Ordinal, SourceSnapshot Source, GenerationSpec Spec,
    TranslationItemState State, int Attempts, string? CandidateId, TitleProviderResult? Result, string? ErrorCategory);
public sealed record TranslationResume(bool RetryFailed = false, bool AcceptUncertainRequest = false);
