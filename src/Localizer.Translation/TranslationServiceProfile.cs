using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Localizer.Core;

namespace Localizer.Translation;

public sealed record TranslationServiceProfile(string Id, string Kind, string Endpoint, string Model, int MaxOutputTokens = 512,
    string? DisplayName = null, bool UseJsonResponseFormat = false)
{
    public const string OpenAiEndpoint = "https://api.openai.com/v1";
    public const string UnconfiguredModel = "configure-model";
    public bool IsConfigured => ValidModel(Model) && Model != UnconfiguredModel;
    public static TranslationServiceProfile OpenAiDefault() => new("openai", "openai", OpenAiEndpoint, UnconfiguredModel);
    public static TranslationServiceProfile GroqDefault() => new("groq", "groq", TitlePipeline.GroqEndpoint, TitlePipeline.QwenModel);
    public void Validate()
    {
        if (!ValidId(Id) || !ValidModel(Model) || MaxOutputTokens is < 64 or > 4096
            || DisplayName is not null && (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 100 || DisplayName.Any(char.IsControl)))
            throw new ArgumentException("InvalidTranslationServiceProfile");
        _ = CanonicalEndpoint();
    }
    public void RequireConfigured()
    {
        Validate();
        if (!IsConfigured) throw new ArgumentException("model_not_configured");
    }
    internal static void RequireConfiguredModel(string model)
    {
        if (!ValidModel(model) || model == UnconfiguredModel) throw new ArgumentException("model_not_configured");
    }
    public string CanonicalEndpoint()
    {
        if (Kind == "openai" && Endpoint == OpenAiEndpoint) return Endpoint;
        if (Kind == "groq" && Endpoint == TitlePipeline.GroqEndpoint) return Endpoint;
        if (Kind == "openai-compatible") return PublicCloudEndpoint.Canonicalize(Endpoint);
        if (Kind != "lmstudio") throw new ArgumentException("InvalidTranslationServiceEndpoint");
        return LocalEndpoint(Endpoint);
    }
    internal static bool ValidModel(string? model) => !string.IsNullOrWhiteSpace(model) && model.Length <= 256
        && !model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    public static bool ValidId(string? id) => id is not null
        && Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);
    internal static string LocalEndpoint(string endpoint)
    {
        if (endpoint is null || endpoint.Length > 512 || endpoint.Any(char.IsWhiteSpace) || endpoint.Contains('\\')
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.TrimEnd('/') != "/api/v1"
            || !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                 || IPAddress.TryParse(uri.DnsSafeHost, out var address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException("LocalTranslationEndpointMustBeLoopback");
        return uri.GetLeftPart(UriPartial.Authority) + "/api/v1";
    }
    public GenerationSpec Apply(GenerationSpec spec)
    {
        RequireConfigured();
        var parameters = Kind == "openai-compatible" ? CompatibleChat.Parameters(MaxOutputTokens, UseJsonResponseFormat).ToJsonString()
            : Kind == "openai"
            ? JsonSerializer.Serialize(new { max_completion_tokens = MaxOutputTokens, response_format = new { type = "json_object" }, reasoning_effort = "none", store = false })
            : Kind == "groq"
            ? JsonSerializer.Serialize(new { temperature = 0.2, max_completion_tokens = MaxOutputTokens,
                response_format = new { type = "json_object" }, reasoning_effort = "none" })
            : JsonSerializer.Serialize(new { temperature = 0.2, max_output_tokens = MaxOutputTokens,
                reasoning = "off", store = false, integrations = Array.Empty<string>(), stream = false });
        return spec with { Endpoint = CanonicalEndpoint(), Model = Model, ParametersJson = parameters,
            ServiceKind = Kind == "openai-compatible" ? Kind : null, ServiceId = Kind == "openai-compatible" ? Id : null };
    }
    public TranslationPolicy Policy()
    {
        Validate();
        return Kind == "groq" ? TranslationPolicy.AdaptiveGroq() : new(2, 0, 120000, 3000);
    }
}

public sealed record TranslationModelInfo(string Id, string? Name = null, bool? Loaded = null);
