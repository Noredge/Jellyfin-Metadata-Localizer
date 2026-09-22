using System.Text.Json;
using Localizer.Core;
using Localizer.Translation;

namespace Localizer.Plugin;

public sealed record TranslationServiceSettings(long Revision, string DefaultServiceId, TranslationServiceProfile[] Profiles,
    Dictionary<string, string>? EndpointBindings = null);

// Settings contain no credentials. A task copies its selected profile before it becomes runnable.
public sealed class ServiceSettingsStore(string root)
{
    private static readonly object Gate = new();
    private string FilePath => Path.Combine(root, "services.json");
    public TranslationServiceSettings Read()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return new(0, "openai", [TranslationServiceProfile.OpenAiDefault(), TranslationServiceProfile.GroqDefault(),
                new("local", "lmstudio", "http://127.0.0.1:1234/api/v1", TranslationServiceProfile.UnconfiguredModel)]);
            var value = JsonSerializer.Deserialize<TranslationServiceSettings>(File.ReadAllText(FilePath))
                ?? throw new IOException("InvalidServiceSettings");
            Validate(value); return value;
        }
    }
    public TranslationServiceProfile Select(string? id)
    {
        var settings = Read();
        var selected = settings.Profiles.SingleOrDefault(x => x.Id == (id ?? settings.DefaultServiceId))
            ?? throw new ArgumentException("UnknownService");
        if (!selected.IsConfigured) throw new OperationValidationException("model_not_configured");
        selected.RequireConfigured();
        return selected;
    }
    public TranslationServiceSettings Save(TranslationServiceSettings value)
    {
        // Endpoint history is server-owned. A client cannot erase it by omitting or replacing it.
        Validate(value with { EndpointBindings = null });
        lock (Gate)
        {
            Directory.CreateDirectory(root);
            using var lease = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = Read();
            if (current.Revision != value.Revision) throw new RevisionConflictException();
            var bindings = new Dictionary<string, string>(current.EndpointBindings ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var previous in current.Profiles.Where(x => x.Kind == "openai-compatible"))
                bindings.TryAdd(previous.Id, previous.CanonicalEndpoint());
            foreach (var profile in value.Profiles)
            {
                var previous = current.Profiles.SingleOrDefault(x => x.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
                if (previous is not null && previous.Kind != profile.Kind
                    || bindings.ContainsKey(profile.Id) && profile.Kind != "openai-compatible")
                    throw new OperationValidationException("service_kind_immutable");
                if (profile.Kind != "openai-compatible") continue;
                var endpoint = profile.CanonicalEndpoint();
                if (bindings.TryGetValue(profile.Id, out var bound) && bound != endpoint
                    && File.Exists(ProviderCredential.FilePath(root, profile.Id)))
                    throw new OperationValidationException("service_endpoint_bound_to_credential");
                bindings[profile.Id] = endpoint;
            }
            var next = value with { Revision = checked(current.Revision + 1), Profiles = value.Profiles.ToArray(), EndpointBindings = bindings };
            Validate(next);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, next); stream.Flush(true); }
                File.Move(temporary, FilePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return next;
        }
    }
    internal void RequireCredentialEndpoint(string serviceId, string endpoint)
    {
        if (!TranslationServiceProfile.ValidId(serviceId)) throw new ArgumentException("InvalidServiceIdentity");
        endpoint = PublicCloudEndpoint.Canonicalize(endpoint);
        var current = Read();
        var bound = current.EndpointBindings?.SingleOrDefault(x => x.Key.Equals(serviceId, StringComparison.OrdinalIgnoreCase)).Value
            ?? current.Profiles.SingleOrDefault(x => x.Kind == "openai-compatible" && x.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase))?.CanonicalEndpoint();
        if (bound != endpoint) throw new InvalidOperationException("ServiceEndpointNotSavedForCredential");
    }
    private static void Validate(TranslationServiceSettings value)
    {
        if (value.Revision < 0 || string.IsNullOrWhiteSpace(value.DefaultServiceId)
            || value.Profiles is null || value.Profiles.Length is < 1 or > 20 || value.Profiles.Any(x => x is null)
            || value.Profiles.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Profiles.Length
            || !value.Profiles.Any(x => x.Id == value.DefaultServiceId)) throw new ArgumentException("InvalidServiceSettings");
        foreach (var profile in value.Profiles) profile.Validate();
        if (value.EndpointBindings is not null)
        {
            if (value.EndpointBindings.Count > 1000
                || value.EndpointBindings.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.EndpointBindings.Count)
                throw new ArgumentException("InvalidServiceEndpointBindings");
            foreach (var pair in value.EndpointBindings)
                if (!TranslationServiceProfile.ValidId(pair.Key) || PublicCloudEndpoint.Canonicalize(pair.Value) != pair.Value)
                    throw new ArgumentException("InvalidServiceEndpointBindings");
        }
    }
}
