using System.Text.Json;
using Localizer.Unified;

static class ReadModelChecks
{
    internal static void Run(string root)
    {
        if(Directory.Exists(root)) throw new ArgumentException("New output root required");
        var checks=new List<object>();
        void Check(string name,bool passed){checks.Add(new{name,passed});Console.WriteLine((passed?"PASS ":"FAIL ")+name);if(!passed)throw new InvalidOperationException(name);}
        var database=new UnifiedDatabase(root,true);var read=new ReadModel(database);var settings=new SettingsStore(database);var content=new ContentStore(database);
        var library=Guid.NewGuid().ToString("N");var s=settings.Read();settings.Save(s.Revision,s.Language,s.Fields,[library],false);
        var media=content.RegisterMedia(new("server",library,Guid.NewGuid().ToString("N")),Path.Combine(root,"movie.mp4"),"{}");
        var title=content.Observe(media,"Name","read fixture","{}");content.Observe(media,"Overview","","{}");content.Observe(media,"Genres","[]","{}");
        using var overview=JsonDocument.Parse(JsonSerializer.Serialize(read.Overview()));
        var fields=overview.RootElement.GetProperty("Fields").EnumerateArray().ToArray();
        Check("empty overview excluded from SQL denominator",fields.Single(x=>x.GetProperty("field").GetString()=="Overview").GetProperty("denominator").GetInt64()==0);
        using var movies=JsonDocument.Parse(JsonSerializer.Serialize(read.Movies("fixture",null,"missing",0,30)));
        Check("SQL search and filter return one bounded item",movies.RootElement.GetProperty("Total").GetInt64()==1&&movies.RootElement.GetProperty("Items").GetArrayLength()==1);
        var draft=content.SaveManual(title,"zh-Hans","译文",false,"read fixture","read-save");var f=content.ReadField(media,"Name")!;
        content.Choose(f,"zh-Hans",draft.Id,"read fixture");
        using var pending=JsonDocument.Parse(JsonSerializer.Serialize(read.Overview()));
        Check("selected version awaiting display counted independently",pending.RootElement.GetProperty("Fields").EnumerateArray().Single(x=>x.GetProperty("field").GetString()=="Name").GetProperty("pending_display").GetInt64()==1);
        using(var db=database.Open())
        {
            var intent=UnifiedDatabase.Scalar<string>(db,null,"SELECT id FROM display_intents WHERE media_id=$m AND field='Name'",("$m",media));
            UnifiedDatabase.Execute(db,null,"""
                INSERT INTO display_writes(id,intent_id,intent_revision,action,state,source_revision,control_revision,target_epoch,before_value,before_hash,target_value,target_hash,created_utc,verified_utc)
                VALUES('verified-display',$i,1,'apply','verified',1,1,0,'read fixture',$before,'译文',$target,$n,$n)
                """,("$i",intent),("$before",UnifiedDatabase.Hash("read fixture")),("$target",UnifiedDatabase.Hash("译文")),("$n",UnifiedDatabase.Now()));
            UnifiedDatabase.Execute(db,null,"UPDATE display_intents SET state='applied' WHERE id=$i",("$i",intent));
        }
        f=content.Observe(media,"Name","read fixture","{}",observedDisplay:"译文");
        content.SaveManual(f,"zh-Hans","未选用的新人工稿",false,"译文","second-draft");
        using var savedDraft=JsonDocument.Parse(JsonSerializer.Serialize(read.Overview()));
        Check("save-only draft does not falsely mark the verified selected display as pending",savedDraft.RootElement.GetProperty("Fields").EnumerateArray().Single(x=>x.GetProperty("field").GetString()=="Name").GetProperty("pending_display").GetInt64()==0);
        content.KeepOriginal(content.ReadField(media,"Name")!,"read fixture");
        using var kept=JsonDocument.Parse(JsonSerializer.Serialize(read.Overview()));
        Check("kept original excluded from SQL denominator",kept.RootElement.GetProperty("Fields").EnumerateArray().Single(x=>x.GetProperty("field").GetString()=="Name").GetProperty("denominator").GetInt64()==0);
        Check("work summary reads ordinary columns",JsonSerializer.Serialize(read.Work(null,0,30))=="[]");
        Check("request detail uses real attempt columns",JsonSerializer.Serialize(read.Attempts("none",0,20))=="[]");
        using(var db=database.Open())
            foreach(var i in new[]{0,5,2,3,1,4}) UnifiedDatabase.Execute(db,null,"INSERT INTO batches(id,submission_key,kind,language,target_epoch,created_utc) VALUES($id,$id,'fill','zh-Hans',0,$utc)",("$id","recent-"+i),("$utc",$"2026-10-27T18:{i:D2}:00+00:00"));
        using var recent=JsonDocument.Parse(JsonSerializer.Serialize(read.Overview()));
        var batches=recent.RootElement.GetProperty("Batches").EnumerateArray().ToArray();
        Check("overview shows only three newest batches with their submission timestamps",batches.Length==3&&batches.Select(x=>x.GetProperty("id").GetString()).SequenceEqual(new[]{"recent-5","recent-4","recent-3"})&&batches.All(x=>x.GetProperty("created_utc").GetString()!.StartsWith("2026-10-27T18:",StringComparison.Ordinal)));
        using(var db=database.Open()) Check("recent task limit preserves all older batch history",UnifiedDatabase.Scalar<long>(db,null,"SELECT COUNT(*) FROM batches")==6);
        File.WriteAllText(Path.Combine(root,"read-checks.json"),JsonSerializer.Serialize(new{status="passed",checks},new JsonSerializerOptions{WriteIndented=true}));
    }
}
