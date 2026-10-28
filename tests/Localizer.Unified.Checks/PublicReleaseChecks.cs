using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class PublicReleaseChecks
{
    internal static void Run(string root, Action<string, bool> check)
    {
        var server=Path.Combine(root,"public-layout");var legacy=Path.Combine(server,"metadata-localizer");Directory.CreateDirectory(legacy);
        var sentinel=Path.Combine(legacy,"candidates.db");File.WriteAllText(sentinel,"older database sentinel");
        using var locked=new FileStream(sentinel,FileMode.Open,FileAccess.Read,FileShare.None);
        check("older data is detected without opening its database",DataLayout.HasOlderData(server));
        var newer=DataLayout.Root(server);check("public data uses a distinct versioned directory",newer==Path.Combine(server,"metadata-localizer-v2") && newer!=legacy);
        var db=new UnifiedDatabase(newer,create:true);
        using(var read=db.OpenRead())
        {
            check("fresh public installation defaults to automatic processing off",Scalar<long>(read,null,"SELECT auto_enabled FROM settings")==0);
            check("fresh installation has no old queue requests or media",Scalar<long>(read,null,"SELECT (SELECT COUNT(*) FROM work_items)+(SELECT COUNT(*) FROM request_attempts)+(SELECT COUNT(*) FROM media_items)")==0);
        }
        check("new initialization does not add files to older directory",Directory.GetFiles(legacy).Length==1);
        db.ReleaseReadPool();
        var rejected=false;try{DataLayout.Root("relative-data");}catch(ArgumentException){rejected=true;}
        check("public data layout requires an explicit absolute server path",rejected);
    }
}
