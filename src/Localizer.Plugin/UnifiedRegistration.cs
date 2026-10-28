using Localizer.Translation;
using Localizer.Unified;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Localizer.Plugin;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.TryAddSingleton<IUnifiedTransport>(provider =>
        {
            var root = Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "metadata-localizer-v2");
            var credentials = new ServerCredentialStore(root);
            return new UnifiedCompatibleTransport((reference, endpoint, token) =>
            { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(credentials.Load(endpoint, reference)); }, UnifiedRuntime.Rules);
        });
        services.AddSingleton<UnifiedRuntime>();
        services.AddHostedService<UnifiedWorker>();
    }
}
