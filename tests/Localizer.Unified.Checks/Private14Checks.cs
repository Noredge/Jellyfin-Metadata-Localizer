using System.Text.Json;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class Private14Checks
{
    internal static async Task Run(string root, Action<string,bool> check)
    {
        using var f = new ExecutorFixture(Path.Combine(root,"private14"));
        void Fail(string id,string code){using var db=f.Database.Open();Execute(db,null,"UPDATE work_items SET state='failed',step='request',issue_kind='translation',error_code=$e WHERE id=$id",("$e",code),("$id",id));}
        var a=f.Add("filled","", "山"); var b=f.Add("missing","海を歩く。");
        f.Submit([a], ["Name","Genres"]); await f.Drain();
        var reads=0; f.Host.BeforeReadReturn=_=>{reads++;return Task.CompletedTask;};
        var planner=new FillPlanner(f.Database);var plan=planner.Read();
        check("fill planner reads SQL only and excludes selected and empty fields", reads==0&&plan.Candidates.Length==2&&plan.Skipped==4);
        check("fill candidates honor requested movies and fields", planner.Read([a],["Name"]).Candidates.Length==0&&planner.Read([b],["Overview"]).Candidates.Single().Field.Field=="Overview");
        var before=f.Count("SELECT COUNT(*) FROM work_items");
        var repeat=f.Work.Submit("repeat-fill","fill",f.Settings.Read().Revision,[f.Seed(a,"Name"),f.Seed(a,"Overview"),f.Seed(a,"Genres")]);
        check("repeat fill skips translated and empty fields even through direct submission",repeat.WorkIds.Length==0&&f.Count("SELECT COUNT(*) FROM work_items")==before);
        check("zero-work fill replays the same receipt",f.Work.Submit("repeat-fill","fill",f.Settings.Read().Revision,[f.Seed(a,"Name"),f.Seed(a,"Overview"),f.Seed(a,"Genres")]).Id==repeat.Id);
        var missing=f.Seed(b,"Name"); var batch=f.Work.Submit("active-fill","fill",f.Settings.Read().Revision,[missing]);
        check("active work is skipped by SQL preview and second fill",planner.Read([b],["Name"]).Candidates.Length==0&&f.Work.Submit("active-fill-again","fill",f.Settings.Read().Revision,[missing]).WorkIds.Length==0);
        Fail(batch.WorkIds.Single(),"invalid_output");
        check("definitive failure remains eligible for explicit fill",planner.Read([b],["Name"]).Candidates.Length==1);
        var issues=new ReadModel(f.Database).Issues("translation","invalid_output",0,30);
        check("issue entry points to exact film and failing field",issues.Total==1&&(string)issues.Items[0]["media_id"]! ==b&&(string)issues.Items[0]["field"]! =="Name");
        var retry=f.Work.Submit("fresh-fill","fill",f.Settings.Read().Revision,[missing]); await f.Drain();
        check("successful retry removes current issue while retaining first failure",new ReadModel(f.Database).Issues("translation","invalid_output",0,30).Total==0&&f.Work.Find(batch.WorkIds.Single())!.State=="failed"&&f.Work.Find(retry.WorkIds.Single())!.State=="completed");
        var ov=f.Content.ReadField(b,"Overview")!; f.Content.KeepOriginal(ov,"海を歩く。");
        check("kept originals cannot be admitted by fill",planner.Read([b],["Overview"]).Candidates.Length==0&&f.Work.Submit("kept-fill","fill",f.Settings.Read().Revision,[f.Seed(b,"Overview")]).WorkIds.Length==0);
        var c=f.Add("race");var stalePreview=f.Seed(c,"Name"); f.Content.SaveManual(stalePreview.Field,"zh-Hans","人工译文",true,stalePreview.ExpectedDisplay,"race-manual");
        // A selection may arrive without a source/control change (e.g. a completed parallel cache save).
        var fresh=f.Seed(c,"Name");
        check("submission rechecks new selected translation atomically",f.Work.Submit("selected-race-fill","fill",f.Settings.Read().Revision,[fresh]).WorkIds.Length==0);

        var config=new ConfigurationStore(f.Database);
        for(var i=0;i<65;i++)config.SavePerson(Guid.NewGuid().ToString("N"),0,$"Name {i:D3}",$"中文{i:D3}",$"English {i:D3}",["别名"+i.ToString("D3")]);
        var first=config.People("",0,30);var second=config.People("",30,30);var last=config.People("",60,30);
        check("person rules are bounded ordered pages",first.Total==65&&first.Items.Length==30&&second.Items.Length==30&&last.Items.Length==5&&!first.Items.Select(x=>x.Id).Intersect(second.Items.Select(x=>x.Id)).Any());
        check("person search matches escaped Unicode aliases and case-insensitive English",config.People("别名064",0,30).Items.Single().Original=="Name 064"&&config.People("english 064",0,30).Total==1&&config.People("中文063",0,30).Total==1);
        config.DeletePerson(last.Items.Last().Id,last.Items.Last().Revision);
        check("paged rule deletion retains revision semantics",config.People("",60,30).Items.Length==4);

        var genre=f.Content.ReadField(a,"Genres")!;var selected=f.Content.Selected(genre.SourceId,"zh-Hans")!;
        // Emulate private.13's same-value ID mismatch using JSON escape/ordering variants.
        using(var db=f.Database.Open())
        {
            var text=JsonSerializer.Serialize(JsonSerializer.Deserialize<string[]>(selected.Text)!.Reverse().ToArray(),new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
            Execute(db,null,"INSERT INTO translation_versions(id,source_id,language,text,text_hash,origin,created_utc,provenance_json) VALUES('same-genre',$s,'zh-Hans',$t,$h,'dictionary',$n,'{}')",("$s",genre.SourceId),("$t",text),("$h",Hash(text)),("$n",Now()));
            Execute(db,null,"UPDATE translation_selections SET translation_id='same-genre',revision=revision+1 WHERE source_id=$s AND language='zh-Hans'",("$s",genre.SourceId));
        }
        long Pending(){using var j=JsonDocument.Parse(JsonSerializer.Serialize(new ReadModel(f.Database).Overview()));return j.RootElement.GetProperty("Fields").EnumerateArray().Single(x=>x.GetProperty("field").GetString()=="Genres").GetProperty("pending_display").GetInt64();}
        check("same genre set with different ID or JSON encoding is already displayed",Pending()==0);
        await f.Drain();var calls=f.Transport.Calls;var writes=f.Host.Writes; f.Work.Submit("genre-sync","manual",f.Settings.Read().Revision,[f.Seed(a,"Genres")]);await f.Drain();
        check("same genre set keeps selection and needs no request or native write",f.Content.Selected(genre.SourceId,"zh-Hans")!.Id=="same-genre"&&f.Transport.Calls==calls&&f.Host.Writes==writes);
        using(var db=f.Database.Open())Execute(db,null,"UPDATE display_intents SET render_revision=render_revision+1 WHERE media_id=$m AND field='Genres'",("$m",a));
        check("same-value comparison never hides stale render revisions",Pending()==1);

        for(var i=0;i<35;i++){var m=f.Add("issue"+i);var w=f.Work.Submit("issue"+i,"manual",f.Settings.Read().Revision,[f.Seed(m,"Name")]);Fail(w.WorkIds.Single(),"output_budget");}
        var read=new ReadModel(f.Database);var page=read.Issues("translation","output_budget",0,30);var tail=read.Issues("translation","output_budget",30,30);
        check("issue movie pages have stable bounded navigation",page.Total==35&&page.Items.Length==30&&tail.Items.Length==5&&!page.Items.Select(x=>x["media_id"]).Intersect(tail.Items.Select(x=>x["media_id"])).Any());
        check("new features preserve database constraints",f.Count("SELECT COUNT(*) FROM pragma_foreign_key_check")==0&&f.Text("PRAGMA integrity_check")=="ok");
    }
}
