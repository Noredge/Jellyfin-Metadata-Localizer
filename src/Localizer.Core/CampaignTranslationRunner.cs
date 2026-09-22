namespace Localizer.Core;

// Production delegates to the existing globally leased serial worker. An isolated test host may
// inject a deterministic implementation without depending on the plugin's assembly load context.
public interface ICampaignTranslationRunner
{
    bool IsRunning(string id);
    void Run(string id, TranslationResume resume);
}
