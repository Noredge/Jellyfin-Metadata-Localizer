namespace Localizer.Core;

public enum ProviderError { Authentication, Permission, ModelUnavailable, Configuration, RateLimited, TransportUncertain, CredentialUnavailable, RequestRejected, QuotaExceeded, RequestNotSent }
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
public sealed record TitleProviderRequest(SourceSnapshot Source, TargetLanguage Language, GenerationSpec Spec);
public sealed record TitleProviderResult(string? Title, string? ErrorCategory, string[] Warnings, long? TotalTokens = null, ProviderDiagnostics? Diagnostics = null,
    string? NameTemplate = null);
