namespace Localizer.Unified;

public sealed record HostFieldObservation(MediaIdentity Key, string MediaPath, string Field, string? Original, string Display,
    string Prefix = "", string EvidenceJson = "{}", bool Present = true, bool Writable = true, string? SourceError = null,
    string? DisplayError = null, string[]? PersonNames = null);
public sealed record HostWriteCommand(MediaIdentity Key, string MediaPath, string Field, string Original, string Prefix,
    string ExpectedDisplay, string TargetDisplay, Func<bool> AuthorityStillValid);
public enum HostWriteOutcome { Written, AlreadyMatches, Blocked, Conflict }
public interface IUnifiedHost
{
    Task<HostFieldObservation> ReadAsync(MediaIdentity key, string field, CancellationToken token);
    // The adapter rechecks identity, path, trusted source, field locks, saver settings, expected display and authority at its write boundary.
    Task<HostWriteOutcome> WriteAsync(HostWriteCommand command, CancellationToken token);
}
public interface IUnifiedReadScope
{
    // Bounded to one media observation or detail read. Send/write boundaries never reuse it.
    IDisposable BeginReadScope(MediaIdentity key, UnifiedSettings? settings = null);
}
public sealed record GenerationInput(string Id, string SourceId, string Field, string Language, string Text, string PromptVersion,
    string NamesJson, string ContextJson);
public sealed record GenerationDiagnostics(string? Stage = null, string? Code = null, int? ContentLength = null,
    string? ContentSha256 = null, string? FinishReason = null, string[]? Warnings = null,
    System.Text.Json.JsonElement? Validation = null, int? OutputTokenLimit = null);
public sealed record TokenUsage(long? PromptTokens = null, long? CompletionTokens = null, long? TotalTokens = null);
public sealed record GenerationReply(string? Text, string? Error = null, int? HttpStatus = null, GenerationDiagnostics? Diagnostics = null,
    TokenUsage? Usage = null, long? RetryAfterMs = null, bool NotSent = false, bool OutcomeUnknown = false);
public interface IUnifiedTransport
{
    string PromptVersion(string field, string language);
    // Must call markSending immediately before the real HTTP send, after local validation/credential loading.
    Task<GenerationReply> GenerateAsync(GenerationInput input, ConnectionVersion connection, Action markSending, CancellationToken token);
}
public interface IUnifiedModelCatalog
{
    Task<string[]> ListModelsAsync(ModelCatalogConnection connection, CancellationToken token);
}
// Transient catalogue credentials are never persisted in the business database or an attempt.
public sealed record ModelCatalogConnection(string Endpoint, string? ApiKey = null, string? CredentialReference = null)
{
    public override string ToString() => "ModelCatalogConnection { credentials redacted }";
}
public sealed record RequestAttempt(string Id, string WorkId, int Sequence, GenerationInput Input, ConnectionVersion Connection, string State);
public sealed class ExecutionSupersededException() : InvalidOperationException("ExecutionSuperseded");
public sealed class SourceTemporarilyUnavailableException() : InvalidOperationException("SourceTemporarilyUnavailable");
