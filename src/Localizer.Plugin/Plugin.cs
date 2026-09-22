using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Localizer.Plugin;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer)
    : BasePlugin<BasePluginConfiguration>(paths, serializer), IHasWebPages
{
    public override string Name => "Metadata Localizer";
    public override string Description => "Translate and manage movie titles, overviews and genres, with source protection and restore support.";
    public override Guid Id => new("12bb696e-b52a-4b98-a4fa-39d32bfca5aa");
    public IEnumerable<PluginPageInfo> GetPages() =>
    [new PluginPageInfo { Name = "metadata-localizer", EmbeddedResourcePath = "Localizer.Plugin.Web.admin.html" }];
}
