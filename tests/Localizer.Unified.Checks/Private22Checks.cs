using System.Text.Json;
using Localizer.Core;
using Localizer.Unified;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

internal static class Private22Checks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        using (var f = new ExecutorFixture(Path.Combine(root, "p22-read-pool")))
        {
            var media = f.Add("pool"); var wake = f.Database.WorkAvailable.Version;
            using (var db = f.Database.OpenRead())
            {
                check("pooled reader enforces query-only and native readonly mode", Scalar<long>(db,null,"PRAGMA query_only")==1 && db.DataSource==f.Database.Path);
                var denied = false; try { Execute(db,null,"UPDATE settings SET revision=revision+1"); } catch(SqliteException) { denied=true; }
                check("reader rejects writes without waking work",denied && f.Database.WorkAvailable.Version==wake);
                Execute(db,null,"PRAGMA query_only=OFF");
                denied=false;try{Execute(db,null,"UPDATE settings SET revision=revision+1");}catch(SqliteException){denied=true;}
                check("readonly connection still rejects writes with query-only disabled",denied);
            }
            using (var db = f.Database.OpenRead()) check("next pool borrower restores query-only",Scalar<long>(db,null,"PRAGMA query_only")==1);
            using (var db = f.Database.Open()) check("writer keeps FULL synchronization and foreign keys",Scalar<long>(db,null,"PRAGMA synchronous")==2 && Scalar<long>(db,null,"PRAGMA foreign_keys")==1 && Scalar<long>(db,null,"PRAGMA query_only")==0);
            using (var db = f.Database.OpenRead())
            using (var tx=db.BeginTransaction(deferred:true))
            {
                var old=Scalar<long>(db,tx,"SELECT COUNT(*) FROM runtime_state");
                await Task.Run(()=>f.Database.Write((w,t)=>Execute(w,t,"INSERT INTO runtime_state VALUES('pool-concurrent','one')"),wakeExecutor:false));
                check("WAL reader retains its snapshot while writer commits",Scalar<long>(db,tx,"SELECT COUNT(*) FROM runtime_state")==old);
            }
            using (var db=f.Database.OpenRead())check("new reader observes committed state",Scalar<string>(db,null,"SELECT value FROM runtime_state WHERE key='pool-concurrent'")=="one");
            await Task.WhenAll(Enumerable.Range(0,12).Select(async i=>{await Task.Yield();for(var n=0;n<10;n++){var d=f.Content.ReadDetails(media);if(d.Fields.Length!=3)throw new Exception("partial concurrent detail");}}));
            check("concurrent borrowers return complete media snapshots",f.Content.ReadDetails(media).Fields.Length==3);
            f.Database.ReleaseReadPool();
            using(var exclusive=new FileStream(f.Database.Path,FileMode.Open,FileAccess.Read,FileShare.None))check("read pool releases database handles for offline backup",exclusive.Length>0);
            check("pool release supports subsequent fresh reads",f.Content.ReadField(media,"Name") is not null);
            f.Database.ReleaseReadPool();
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "p22-detail-search-fill")))
        {
            var a=f.Add("Case-Search","source synopsis A","shared"); var b=f.Add("second","","shared");
            var state=f.Content.ReadField(a,"Name")!;var selected=f.Content.SaveManual(state,"zh-Hans","selected title",true,"before",Id());
            f.Content.SaveManual(f.Content.ReadField(a,"Name")!,"zh-Hans","unselected historical text",false,"before",Id());
            using(var timing=new OperationTimings())
            {
                var details=f.Content.ReadDetails(a);
                check("batched detail uses one reader and complete settings",timing.Counts().GetValueOrDefault("db_read_open")==1 && details.Settings.Language=="zh-Hans" && details.Fields.Length==3);
                check("batched detail retains selected version and display intent",details.Fields.Single(x=>x.State.Field=="Name") is { Selection.Id:var chosen,Intent:not null } && chosen==selected.Id);
                check("batched state matches independent field reads",details.Fields.All(x=>x.State==f.Content.ReadField(a,x.State.Field)));
            }
            var read=new ReadModel(f.Database);var variants=new[]{"","case-search","source synopsis","unselected historical","selected title","no match"};
            var filters=new[]{"all","missing","problem","manual","original"};
            var expected=variants.SelectMany(q=>filters.Select(filter=>JsonSerializer.Serialize(read.Movies(q,null,filter,0,30)))).ToArray();
            f.Database.Write((db,tx)=>Execute(db,tx,"DROP INDEX source_owner"),wakeExecutor:false);
            var unindexed=variants.SelectMany(q=>filters.Select(filter=>JsonSerializer.Serialize(read.Movies(q,null,filter,0,30)))).ToArray();
            check("search index preserves path source history display and filter results",expected.SequenceEqual(unindexed));
            _=new UnifiedDatabase(f.Root);_=new UnifiedDatabase(f.Root);
            check("owner index installs idempotently without schema changes",f.Count("SELECT COUNT(*) FROM sqlite_master WHERE name='source_owner'")==1 && f.Text("PRAGMA user_version")=="1");
            using(var db=f.Database.OpenRead())
            {using var cmd=Command(db,null,"EXPLAIN QUERY PLAN SELECT id FROM source_versions WHERE owner_id=$id",("$id",a));using var r=cmd.ExecuteReader();var plan=new List<string>();while(r.Read())plan.Add(r.GetString(3));check("owner searches use their index",plan.Any(x=>x.Contains("source_owner",StringComparison.Ordinal)));}
            var planner=new FillPlanner(f.Database);var all=planner.Read();var one=planner.Read([a,a],["Overview"]);
            check("fill returns only eligible fields and preserves skipped counts",all.Candidates.Length==4 && all.Skipped==2 && one.Candidates.Length==1 && one.Skipped==0);
            check("fill empty restrictions never expand to whole library",planner.Read([],null).Candidates.Length==0 && planner.Read(null,[]).Skipped==0);
            check("fill skips selected title and empty synopsis",planner.Read([a],["Name"]).Skipped==1 && planner.Read([b],["Overview"]).Skipped==1);
            var s=f.Settings.Read();f.Settings.Save(s.Revision,s.Language,new(true,false,true),s.ManagedLibraries,true);
            check("fill enabled-field filter applies before reading candidates",planner.Read([a],["Overview"]).Candidates.Length==0 && planner.Read([a],["Overview"]).Skipped==0);
            s=f.Settings.Read();f.Settings.Save(s.Revision,s.Language,s.Fields,[],true);
            check("fill excludes unmanaged identities before counting",planner.Read().Candidates.Length==0 && planner.Read().Skipped==0);
            check("reader optimizations never send model requests",f.Transport.Calls==0 && f.Count("SELECT COUNT(*) FROM request_attempts")==0);
            f.Database.ReleaseReadPool();
        }
        var folder=Path.Combine(root,"p22-nfo-session");Directory.CreateDirectory(folder);var mediaPath=Path.Combine(folder,"clip.mp4");File.WriteAllBytes(mediaPath,[]);var nfo=Path.ChangeExtension(mediaPath,".nfo");
        File.WriteAllText(nfo,"<movie><plot>one</plot><genre>genre</genre></movie>");
        using(var timing=new OperationTimings())
        {
            var session=new NfoReadSession();var hash=session.Digest(nfo);var overview=NfoOverview.Read(mediaPath,null,session);var genres=NfoGenres.Read(mediaPath,null,session);
            check("one operation shares NFO bytes and secure XML parse",overview.Text=="one" && genres.Values.Single()=="genre" && genres.LegacyFingerprint==hash && timing.Counts().GetValueOrDefault("nfo_file_read")==1 && timing.Counts().GetValueOrDefault("nfo_parse")==1);
            var stamp=File.GetLastWriteTimeUtc(nfo);File.WriteAllText(nfo,"<movie><plot>two</plot><genre>genre</genre></movie>");File.SetLastWriteTimeUtc(nfo,stamp);
            check("new read boundary detects same-size same-time NFO edits",NfoOverview.Read(mediaPath).Text=="two" && NfoGenres.Read(mediaPath).LegacyFingerprint!=hash);
        }
        File.WriteAllText(nfo,"<!DOCTYPE movie [<!ENTITY x SYSTEM 'file:///never-read'>]><movie><plot>&x;</plot><genre>x</genre></movie>");
        check("shared parser still prohibits DTD and external entities",NfoOverview.Read(mediaPath).Error=="overview_nfo_invalid" && NfoGenres.Read(mediaPath).Error=="genre_nfo_invalid");
        File.WriteAllText(nfo,new string('x',4*1024*1024+1));
        check("shared NFO session preserves bounded file reads",NfoOverview.Read(mediaPath).Error=="overview_nfo_too_large" && NfoGenres.Read(mediaPath).Error=="genre_nfo_too_large");
        File.WriteAllText(nfo,"<movie><plot></plot></movie>");check("empty synopsis stays a normal empty source",NfoOverview.Read(mediaPath).Error=="overview_empty");
    }
}
