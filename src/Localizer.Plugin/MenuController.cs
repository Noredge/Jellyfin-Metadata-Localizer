using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed partial class AdminController
{
    [HttpGet("menu/zh-Hans")]
    public ActionResult MenuChinese()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Localizer.Plugin.Web.menu.zh-Hans.json")!;
        return Ok(JsonSerializer.Deserialize<Dictionary<string, string>>(stream));
    }
}
