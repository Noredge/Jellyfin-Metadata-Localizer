using System.Security.Cryptography;
using System.Text;

namespace Localizer.Translation;

// Structural diagnostics only: never persist response text, headers or credentials.
public sealed record TranslationValidation(string Stage, string Code, int ContentLength = 0, string? ContentSha256 = null,
    string? FinishReason = null, string? ObservedType = null, string[]? ActualFields = null,
    string[]? MissingNameTokens = null, string[]? DuplicateNameTokens = null, string[]? UnexpectedNameTokens = null,
    long? JsonLine = null, long? JsonBytePosition = null, string? PromptVersion = null)
{
    public static TranslationValidation Content(string stage, string code, string? text) => new(stage, code, text?.Length ?? 0,
        text is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
}
