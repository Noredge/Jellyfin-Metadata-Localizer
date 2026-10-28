using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Localizer.Plugin;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer)
    : BasePlugin<BasePluginConfiguration>(paths, serializer), IHasWebPages
{
    public override string Name => "Metadata Localizer";
    public override string Description => "Translate movie metadata with shared language selection, reusable translations and durable background work.";
    // Public identity remains distinct from personal forks.
    public override Guid Id => new("12bb696e-b52a-4b98-a4fa-39d32bfca5aa");
    public IEnumerable<PluginPageInfo> GetPages() =>
    [new PluginPageInfo { Name = "metadata-localizer", EmbeddedResourcePath = "Localizer.Plugin.Web.unified.html",
        EnableInMainMenu = true, DisplayName = "Metadata Localizer", MenuIcon = "translate" }];
}
