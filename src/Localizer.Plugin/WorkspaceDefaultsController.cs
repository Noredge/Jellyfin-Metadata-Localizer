using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed partial class AdminController
{
    [HttpGet("libraries/{folder:guid}/workspace-defaults")]
    public ActionResult ReadWorkspaceDefaults(Guid folder) => CampaignGuard(() =>
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        return new WorkspaceDefaultsStore(Root, folder).Read();
    });

    [HttpPost("libraries/{folder:guid}/workspace-defaults"), RequestSizeLimit(4096)]
    public ActionResult SaveWorkspaceDefaults(Guid folder, [FromBody] WorkspaceDefaults request) => CampaignGuard(() =>
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        return new WorkspaceDefaultsStore(Root, folder).Save(request);
    });
}
