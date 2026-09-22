using Localizer.Core;
using Localizer.Plugin;
using Localizer.Translation;

public static class ServiceSettingsChecks
{
    private static void Expect(bool value) { if (!value) throw new InvalidOperationException("Service settings assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    public static async Task Run(Func<string, Func<Task>, Task> check, string scratch)
    {
        var profile = new TranslationServiceProfile("Cloud-A", "openai-compatible", "https://api.example.com/v1", "example-model");
        (ServiceSettingsStore Store, string Path) Fresh()
        {
            var path = Path.Combine(scratch, "services-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return (new(path), path);
        }
        await check("custom_service_key_paths_are_isolated_and_case_canonical", () =>
        {
            var f = Fresh();
            Expect(ProviderCredential.FilePath(f.Path, "Cloud-A") == ProviderCredential.FilePath(f.Path, "cloud-a"));
            Expect(ProviderCredential.FilePath(f.Path, "openai") != OpenAiCredential.FilePath(f.Path)
                && ProviderCredential.FilePath(f.Path, "groq") != GroqCredential.FilePath(f.Path));
            Reject<ArgumentException>(() => ProviderCredential.FilePath(f.Path, "../openai"));
            Reject<InvalidOperationException>(() => f.Store.RequireCredentialEndpoint(profile.Id, profile.Endpoint));
            return Task.CompletedTask;
        });
        await check("custom_service_endpoint_changes_without_key_invalidate_frozen_old_endpoint", () =>
        {
            var f = Fresh(); var initial = f.Store.Read();
            var saved = f.Store.Save(initial with { Profiles = [profile], DefaultServiceId = profile.Id });
            f.Store.RequireCredentialEndpoint(profile.Id, profile.Endpoint);
            var changed = profile with { Endpoint = "https://new.example.com/v2" };
            var updated = f.Store.Save(saved with { Profiles = [changed] });
            f.Store.RequireCredentialEndpoint(profile.Id, changed.Endpoint);
            Reject<InvalidOperationException>(() => f.Store.RequireCredentialEndpoint(profile.Id, profile.Endpoint));
            Expect(updated.Revision == saved.Revision + 1 && updated.Profiles.Single() == changed);
            return Task.CompletedTask;
        });
        await check("custom_service_key_locks_endpoint_even_after_delete_and_readd", () =>
        {
            var f = Fresh(); var initial = f.Store.Read();
            var saved = f.Store.Save(initial with { Profiles = [profile, TranslationServiceProfile.OpenAiDefault()], DefaultServiceId = profile.Id });
            var keyPath = ProviderCredential.FilePath(f.Path, profile.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllText(keyPath, "synthetic-file-presence-only");
            var changed = profile with { Endpoint = "https://other.example.com/v1" };
            Reject<OperationValidationException>(() => f.Store.Save(saved with { Profiles = [changed] }));
            var removed = f.Store.Save(saved with { Profiles = [TranslationServiceProfile.OpenAiDefault()], DefaultServiceId = "openai" });
            Expect(File.Exists(keyPath));
            f.Store.RequireCredentialEndpoint(profile.Id, profile.Endpoint);
            Reject<OperationValidationException>(() => f.Store.Save(removed with { Profiles = [changed], DefaultServiceId = profile.Id,
                EndpointBindings = new() { [profile.Id] = changed.Endpoint } }));
            Reject<OperationValidationException>(() => f.Store.Save(removed with { Profiles = [TranslationServiceProfile.OpenAiDefault() with { Id = profile.Id }], DefaultServiceId = profile.Id }));
            var returned = f.Store.Save(removed with { Profiles = [profile], DefaultServiceId = profile.Id, EndpointBindings = [] });
            Expect(returned.EndpointBindings![profile.Id] == profile.Endpoint && File.ReadAllText(keyPath) == "synthetic-file-presence-only");
            return Task.CompletedTask;
        });
        await check("service_profile_kind_and_case_collisions_are_rejected", () =>
        {
            var f = Fresh(); var initial = f.Store.Read();
            Reject<ArgumentException>(() => f.Store.Save(initial with { Profiles = [profile, profile with { Id = "cloud-a" }], DefaultServiceId = profile.Id }));
            Reject<OperationValidationException>(() => f.Store.Save(initial with { Profiles = [profile with { Id = "openai" }], DefaultServiceId = "openai" }));
            Expect(f.Store.Read().Revision == 0);
            return Task.CompletedTask;
        });
    }
}
