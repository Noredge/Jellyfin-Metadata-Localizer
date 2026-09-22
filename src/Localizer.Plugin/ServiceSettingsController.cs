using Localizer.Core;
using Localizer.Translation;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record ServiceTestRequest(TranslationServiceProfile Profile);

public sealed partial class AdminController
{
    private ServiceSettingsStore Services() => new(Root);
    private object ServicesView(TranslationServiceSettings value) => new
    {
        value.Revision, value.DefaultServiceId, value.Profiles,
        CredentialFiles = value.Profiles.ToDictionary(x => x.Id, x => x.Kind switch
        {
            "openai" => System.IO.File.Exists(OpenAiCredential.FilePath(Root)),
            "groq" => System.IO.File.Exists(GroqCredential.FilePath(Root)),
            "openai-compatible" => System.IO.File.Exists(ProviderCredential.FilePath(Root, x.Id)),
            _ => false
        }),
        OpenAiCredentialFilePresent = System.IO.File.Exists(OpenAiCredential.FilePath(Root)),
        CredentialFilePresent = System.IO.File.Exists(GroqCredential.FilePath(Root))
    };

    [HttpGet("services")]
    public ActionResult ReadServices() => CampaignGuard(() => ServicesView(Services().Read()));

    [HttpPost("services"), RequestSizeLimit(65536)]
    public ActionResult SaveServices([FromBody] TranslationServiceSettings request) =>
        CampaignGuard(() => ServicesView(Services().Save(request)));

    [HttpPost("services/test"), RequestSizeLimit(8192)]
    public async Task<ActionResult> TestService([FromBody] ServiceTestRequest request)
    {
        try
        {
            if (request.Profile is null) throw new ArgumentException();
            request.Profile.Validate();
            using var provider = new TranslationServiceRouter(_ => ValueTask.FromResult(GroqCredential.Load(Root)), openAiCredential: _ => ValueTask.FromResult(OpenAiCredential.Load(Root)),
                compatibleCredential: (id, endpoint, _) => ValueTask.FromResult(ProviderCredential.Load(Root, id, endpoint)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var models = await provider.ListModelsAsync(request.Profile, timeout.Token);
            return Ok(new { Success = true, Models = models.Select(x => x.Id).ToArray() });
        }
        catch (ArgumentException) { return BadRequest(new { Error = "invalid_service_profile" }); }
        catch (TitleProviderException error) { return StatusCode(502, new { Error = error.Category.ToString() }); }
        catch (OperationCanceledException) { return StatusCode(504, new { Error = "service_test_timeout" }); }
        catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
        { return StatusCode(502, new { Error = "service_test_unavailable" }); }
    }
}
