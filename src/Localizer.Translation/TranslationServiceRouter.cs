using Localizer.Core;

namespace Localizer.Translation;

/// <summary>Routes only the frozen generation endpoint. Local failure can never select the cloud.</summary>
public sealed class TranslationServiceRouter : ITitleProvider, IDisposable
{
    private readonly GroqTitleProvider groq;
    private readonly LmStudioTitleProvider local;
    private readonly OpenAiTitleProvider openai;
    private readonly CompatibleTitleProvider compatible;
    public TranslationServiceRouter(Func<CancellationToken, ValueTask<string>> groqCredential,
        HttpMessageHandler? groqTestHandler = null, HttpMessageHandler? localTestHandler = null,
        Func<CancellationToken, ValueTask<string>>? openAiCredential = null, HttpMessageHandler? openAiTestHandler = null,
        Func<string, string, CancellationToken, ValueTask<string>>? compatibleCredential = null, HttpMessageHandler? compatibleTestHandler = null)
    {
        groq = new(groqCredential, groqTestHandler);
        local = new(localTestHandler);
        openai = new(openAiCredential ?? (_ => throw new InvalidOperationException("OpenAiCredentialUnavailable")), openAiTestHandler);
        compatible = new(compatibleCredential ?? ((_, _, _) => throw new InvalidOperationException("ServiceCredentialUnavailable")), compatibleTestHandler);
    }
    public async Task<TitleProviderResult> TranslateAsync(TitleProviderRequest request, CancellationToken cancellationToken)
    {
        using var lease = await TranslationDispatch.EnterAsync(cancellationToken);
        if (request.Spec.ServiceKind == "openai-compatible") return await compatible.TranslateAsync(request, cancellationToken);
        if (request.Spec.ServiceKind is not null || request.Spec.ServiceId is not null)
            throw new TitleProviderException(ProviderError.Configuration);
        if (request.Spec.Endpoint == TranslationServiceProfile.OpenAiEndpoint) return await openai.TranslateAsync(request, cancellationToken);
        if (request.Spec.Endpoint == TitlePipeline.GroqEndpoint) return await groq.TranslateAsync(request, cancellationToken);
        try { _ = TranslationServiceProfile.LocalEndpoint(request.Spec.Endpoint); }
        catch (ArgumentException) { throw new TitleProviderException(ProviderError.Configuration); }
        return await local.TranslateAsync(request, cancellationToken);
    }
    public Task<TranslationModelInfo[]> ListModelsAsync(TranslationServiceProfile profile, CancellationToken cancellationToken)
    {
        profile.Validate();
        if (profile.Kind == "openai-compatible") return compatible.ListModelsAsync(profile, cancellationToken);
        return profile.Kind == "openai" ? openai.ListModelsAsync(cancellationToken) : profile.Kind == "groq" ? groq.ListModelsAsync(cancellationToken)
            : local.ListModelsAsync(profile.CanonicalEndpoint(), cancellationToken);
    }
    public void Dispose() { groq.Dispose(); local.Dispose(); openai.Dispose(); compatible.Dispose(); }
}
