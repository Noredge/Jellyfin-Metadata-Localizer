using Localizer.Translation;
using Localizer.Core;
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
        services.TryAddSingleton(provider => new LocalRulePackProvider(DataLayout.Root(provider.GetRequiredService<IApplicationPaths>().DataPath)));
        services.TryAddSingleton<IUnifiedTransport>(provider =>
        {
            var root = DataLayout.Root(provider.GetRequiredService<IApplicationPaths>().DataPath);
            var credentials = new ServerCredentialStore(root);
            var localRules = provider.GetRequiredService<LocalRulePackProvider>();
            return new UnifiedCompatibleTransport((reference, endpoint, token) =>
            { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(credentials.Load(endpoint, reference)); }, language => UnifiedRuntime.Rules(language, localRules.Read()));
        });
        services.AddSingleton<UnifiedRuntime>();
        services.AddHostedService<UnifiedWorker>();
    }
}
