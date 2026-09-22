using System.Text.Json;
using Localizer.Core;
using Localizer.Translation;
var count=0;
void Check(bool ok) { if(!ok) throw new Exception($"Check {count+1} failed"); count++; }
GenreDictionaryEntry Lookup(TargetLanguage l,string word) { var value=word=="known" || word=="partial"&&l==TargetLanguage.English ? "existing" : null;return new(word,value,null,value,0,value is null); }
var plan=GenreTranslationPipeline.Missing(["known","partial","new","new"],Lookup);
Check(plan.Length==2);
Check(plan.Single(x=>x.Original=="partial").Languages.SequenceEqual(new[]{TargetLanguage.SimplifiedChinese}));
Check(GenreTranslationPipeline.Missing(["known"],Lookup).Length==0);
var payload=GenreTranslationPipeline.Payload(plan);
Check(!payload.Contains("existing") && !payload.Contains("revision",StringComparison.OrdinalIgnoreCase));
var batch=new[]{new GenreTranslationTerm("G0001","35％OFFセール",[TargetLanguage.SimplifiedChinese,TargetLanguage.English])};
string Response(string zh,string en)=>JsonSerializer.Serialize(new{entries=new[]{new{id="G0001",translations=new Dictionary<string,string>{{"zh-Hans",zh},{"en",en}}}}});
var good=GenreTranslationPipeline.Parse(Response("35%优惠","35% Off Sale"),batch);
Check(good.Error is null && good.Candidates.All(x=>x.Error is null));
Check(GenreTranslationPipeline.Parse(Response("七折促销","35% Off Sale"),batch).Candidates[0].Error=="number_mismatch");
Check(GenreTranslationPipeline.Parse(Response("35%优惠","35% 中文"),batch).Candidates[1].Error=="target_language");
Check(GenreTranslationPipeline.Parse(Response("","35% Off Sale"),batch).Candidates[0].Error=="invalid_text");
Check(GenreTranslationPipeline.Parse("{}",batch,true).Error=="output_budget");
Check(GenreTranslationPipeline.Parse("{}",batch,refused:true).Error=="refusal");
Check(GenreTranslationPipeline.Parse("{\"entries\":[]}",batch).Error=="invalid_output");
Check(GenreTranslationPipeline.Parse(Response("35%优惠","35% Off Sale").Replace("G0001","G0002"),batch).Error=="invalid_output");
var refusal="{\"entries\":[{\"id\":\"G0001\",\"refusal\":\"Unable\"}]}";
Check(GenreTranslationPipeline.Parse(refusal,batch).Candidates.All(x=>x.Error=="refusal"));
Check(GenreTranslationPipeline.Parse("{\"entries\":[],\"entries\":[]}",batch).Error=="invalid_output");
try {GenreTranslationPipeline.Payload([]);throw new Exception("Empty batch accepted");}catch(ArgumentException){count++;}
var dir=Path.Combine(Path.GetTempPath(),"jml-genre-check-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var path=Path.Combine(dir,"dictionary-tasks.db");
var store=new GenreTranslationStore(path);
var connection=new GenreTranslationConnection("openai","https://api.openai.com/v1",4000);
var id=Guid.NewGuid();
var seed=new[]{new GenreTranslationSeed("原词",TargetLanguage.English,3,"Previous")};
var task=store.Create(id,"server","library","openai","synthetic-model","genre-test",seed,connection);
Check(task.State=="Ready" && task.Items[0].Source.MappingRevision==3);
Check(new GenreTranslationStore(path).Get(id).Connection==connection);
try{store.Create(id,"server","library","openai","synthetic-model","genre-test",seed,connection with {MaxOutputTokens=3200});throw new Exception();}catch(IdempotencyConflictException){count++;}
Check(store.Create(id,"server","library","openai","synthetic-model","genre-test",seed,connection).Revision==0);
try{store.Create(id,"server","library","openai","different","genre-test",seed,connection);throw new Exception();}catch(IdempotencyConflictException){count++;}
Check(store.List("server","other").Length==0);
var sending=store.Begin(id,0);
try{store.Begin(id,0);throw new Exception();}catch(RevisionConflictException){count++;}
try{store.Begin(id,sending.Revision);throw new Exception();}catch(OperationBusyException){count++;}
var reopened=new GenreTranslationStore(path);
Check(reopened.Get(id).State=="Sending");
Check(reopened.RecoverInterrupted()==1 && reopened.Get(id).State=="Uncertain");
Check(reopened.RecoverInterrupted()==0);
try{reopened.Begin(id,reopened.Get(id).Revision);throw new Exception();}catch(OperationBusyException){count++;}
var nextId=Guid.NewGuid();var ready=store.Create(nextId,"server","library","openai","synthetic-model","genre-test",seed,connection);
var running=store.Begin(nextId,ready.Revision);
try{store.Complete(nextId,running.Revision,[new(seed[0] with {Original="wrong"},"Generated","Text")]);throw new Exception();}catch(ArgumentException){count++;}
var done=store.Complete(nextId,running.Revision,[new(seed[0],"Generated","Translated")]);
Check(new GenreTranslationStore(path).Get(nextId).Items[0].Text=="Translated");
var edited=store.Edit(nextId,done.Revision,"原词",TargetLanguage.English,"Manual edit");
Check(edited.Items[0].HumanEdited && edited.Items[0].Source.ExpectedMapping=="Previous");
try{store.Edit(nextId,done.Revision,"原词",TargetLanguage.English,"Stale edit");throw new Exception();}catch(RevisionConflictException){count++;}
Check(new GenreTranslationStore(path).Get(nextId).Items[0].Text=="Manual edit");
var captured="";
var cloudJson=JsonSerializer.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=Response("35%优惠","35% Off Sale")}}}});
var cloud=new GenreTranslationProvider(()=>"synthetic-key",new Stub(async request=>{
 captured=await request.Content!.ReadAsStringAsync();
 Check(request.RequestUri!.AbsoluteUri=="https://api.openai.com/v1/chat/completions");
 Check(request.Headers.Authorization?.Parameter=="synthetic-key");
 return new(System.Net.HttpStatusCode.OK){Content=new StringContent(cloudJson)};
}));
var profile=new TranslationServiceProfile("openai","openai",TranslationServiceProfile.OpenAiEndpoint,"synthetic-model",4000);
Check((await cloud.TranslateAsync(profile,batch,CancellationToken.None)).Candidates.Length==2);
using(var body=JsonDocument.Parse(captured)) { Check(body.RootElement.GetProperty("max_completion_tokens").GetInt32()==4000); Check(body.RootElement.GetProperty("reasoning_effort").GetString()=="none"); }
var localJson=JsonSerializer.Serialize(new{output=new[]{new{type="message",content=Response("35%优惠","35% Off Sale")}},stats=new{total_output_tokens=80}});
var local=new GenreTranslationProvider(()=>throw new Exception("Local must not read cloud key"),new Stub(async request=>{
 captured=await request.Content!.ReadAsStringAsync();
 Check(request.RequestUri!.AbsoluteUri=="http://127.0.0.1:1234/api/v1/chat" && request.Headers.Authorization is null);
 return new(System.Net.HttpStatusCode.OK){Content=new StringContent(localJson)};
}));
Check((await local.TranslateAsync(new("local","lmstudio","http://127.0.0.1:1234/api/v1","synthetic-model",4000),batch,CancellationToken.None)).Error is null);
using(var body=JsonDocument.Parse(captured)) Check(body.RootElement.GetProperty("reasoning").GetString()=="off" && body.RootElement.GetProperty("store").GetBoolean()==false);
var groq=new GenreTranslationProvider(()=>"synthetic-key",new Stub(async request=>{
 var requestBody=await request.Content!.ReadAsStringAsync();
 Check(request.RequestUri!.AbsoluteUri=="https://api.groq.com/openai/v1/chat/completions" && requestBody.Contains("max_completion_tokens"));
 return new(System.Net.HttpStatusCode.OK){Content=new StringContent(cloudJson)};
}));
Check((await groq.TranslateAsync(new("groq","groq","https://api.groq.com/openai/v1","synthetic-model",4000),batch,CancellationToken.None)).Error is null);
var limited=new GenreTranslationProvider(()=>"synthetic-key",new Stub(_=>Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests))));
Check((await limited.TranslateAsync(profile,batch,CancellationToken.None)).Error=="rate_limit");
var lost=new GenreTranslationProvider(()=>"synthetic-key",new Stub(_=>throw new HttpRequestException()));
Check((await lost.TranslateAsync(profile,batch,CancellationToken.None)).Error=="network_uncertain");
var denied=new GenreTranslationProvider(()=>throw new InvalidOperationException(),new Stub(_=>throw new Exception("Must not send")));
Check((await denied.TranslateAsync(profile,batch,CancellationToken.None)).Error=="credential_unavailable");
var batchSeeds=Enumerable.Range(0,26).Select(i=>new GenreTranslationSeed("Word"+i,TargetLanguage.English,0,null)).ToArray();
var runId=Guid.NewGuid();store.Create(runId,"server","library","openai","synthetic-model",GenreTranslationPipeline.Version,batchSeeds,connection);
var calls=0;
var runner=new GenreTranslationRunner(store,(frozen,terms,token)=>{
 calls++;Check(frozen.Model=="synthetic-model" && frozen.MaxOutputTokens==4000);
 return Task.FromResult(new GenreTranslationResult(terms.SelectMany(t=>t.Languages.Select(l=>new GenreTranslationCandidate(t.Id,l,"Synthetic translation",null))).ToArray(),null));
});
var finished=await runner.RunAsync(runId,CancellationToken.None);
Check(calls==2 && finished.State=="Completed" && finished.Items.All(i=>i.State=="Generated"));
await runner.RunAsync(runId,CancellationToken.None);Check(calls==2);
var pausedId=Guid.NewGuid();store.Create(pausedId,"server","library","openai","synthetic-model",GenreTranslationPipeline.Version,batchSeeds,connection);
var limitedRunner=new GenreTranslationRunner(store,(_,_,_)=>Task.FromResult(new GenreTranslationResult([],"rate_limit")));
var paused=await limitedRunner.RunAsync(pausedId,CancellationToken.None);
Check(paused.State=="Paused" && paused.Items.Count(i=>i.State=="Failed")==25 && paused.Items.Count(i=>i.State=="Pending")==1);
store.ContinuePending(pausedId,paused.Revision);
var remaining=await runner.RunAsync(pausedId,CancellationToken.None);
Check(remaining.State=="Completed" && remaining.Items.Count(i=>i.State=="Generated")==1 && calls==3);
var uncertainId=Guid.NewGuid();store.Create(uncertainId,"server","library","openai","synthetic-model",GenreTranslationPipeline.Version,batchSeeds,connection);
var lostRunner=new GenreTranslationRunner(store,(_,_,_)=>throw new HttpRequestException());
var uncertainTask=await lostRunner.RunAsync(uncertainId,CancellationToken.None);
Check(uncertainTask.State=="Uncertain" && uncertainTask.Items.Count(i=>i.State=="Uncertain")==25);
await runner.RunAsync(uncertainId,CancellationToken.None);Check(calls==3);
var effectiveStore=new CandidateStore(Path.Combine(dir,"effective.db"));
var saveId=Guid.NewGuid();
var mappings=new[]{new GenreConfirmedMapping(new("test-word",TargetLanguage.English,0,null),"First"),new GenreConfirmedMapping(new("test-other",TargetLanguage.English,0,null),"Second")};
var savedMappings=effectiveStore.SaveConfirmedGenres(saveId,"save-fingerprint",mappings);
Check(savedMappings.Length==2 && effectiveStore.GetGenreDictionaryEntry(TargetLanguage.English,"test-word").Effective=="First");
Check(effectiveStore.SaveConfirmedGenres(saveId,"save-fingerprint",mappings)[0].Revision==1);
Check(effectiveStore.ReadGenreSaveReceipt(saveId,"save-fingerprint")!.Length==2);
try{effectiveStore.SaveConfirmedGenres(saveId,"changed",mappings);throw new Exception();}catch(IdempotencyConflictException){count++;}
var conflictId=Guid.NewGuid();
try{effectiveStore.SaveConfirmedGenres(conflictId,"conflict",[new(new("fresh-word",TargetLanguage.English,0,null),"Must roll back"),mappings[0]]);throw new Exception();}catch(RevisionConflictException){count++;}
Check(effectiveStore.GetGenreDictionaryEntry(TargetLanguage.English,"fresh-word").Unknown && effectiveStore.ReadGenreSaveReceipt(conflictId,"conflict")==null);
// Simulate a genuine v4 database with existing content and verify additive migration.
var migrationPath=Path.Combine(dir,"migration.db");var migrationStore=new CandidateStore(migrationPath);
migrationStore.SetGenreOverride(TargetLanguage.English,"keep",0,"Kept");
using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+migrationPath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP TABLE genre_save_receipts; PRAGMA user_version=4;";cmd.ExecuteNonQuery();}
Check(new CandidateStore(migrationPath).GetGenreDictionaryEntry(TargetLanguage.English,"keep").Effective=="Kept");
var ended=store.EndStopped(uncertainId,uncertainTask.Revision);
Check(ended.State=="Ended" && ended.Items.Count(i=>i.State=="Uncertain")==25 && ended.Items.Count(i=>i.State=="Skipped")==1);
var corrected=store.Edit(uncertainId,ended.Revision,ended.Items[0].Source.Original,TargetLanguage.English,"Reviewed manually");
Check(corrected.Items[0].HumanEdited && corrected.Items[0].Error==null);
await runner.RunAsync(uncertainId,CancellationToken.None);Check(calls==3);
var partialId=Guid.NewGuid();var partialTask=store.Create(partialId,"server","library","openai","synthetic-model",GenreTranslationPipeline.Version,batchSeeds,connection);
var phase=0;
var partialRunner=new GenreTranslationRunner(store,(_,terms,_)=>{
 phase++;
 if(phase==2) return Task.FromResult(new GenreTranslationResult([],"network_uncertain"));
 return Task.FromResult(new GenreTranslationResult(terms.Select(t=>new GenreTranslationCandidate(t.Id,TargetLanguage.English,"Good result",null)).ToArray(),null));
});
var partialResult=await partialRunner.RunAsync(partialId,CancellationToken.None);
var partialEnd=store.EndStopped(partialId,partialResult.Revision);
Check(partialEnd.Items.Count(i=>i.State=="Generated")==25 && partialEnd.Items.Count(i=>i.State=="Uncertain")==1);
var toSave=partialEnd.Items.Where(i=>i.State=="Generated").Select(i=>new GenreConfirmedMapping(i.Source,i.Text!)).ToArray();
Check(effectiveStore.SaveConfirmedGenres(Guid.NewGuid(),"partial-ended",toSave).Length==25);
try{store.BeginBatch(partialId,partialEnd.Revision,[0]);throw new Exception();}catch(OperationBusyException){count++;}
var orphanId=Guid.NewGuid();var orphan=store.Create(orphanId,"server","library","openai","synthetic-model",GenreTranslationPipeline.Version,batchSeeds,connection);
var orphanSending=store.BeginBatch(orphanId,orphan.Revision,[0]);
var orphanEnd=store.EndStopped(orphanId,orphanSending.Revision);
Check(orphanEnd.Items[0].State=="Uncertain" && orphanEnd.Items.Skip(1).All(i=>i.State=="Skipped"));
try{store.Edit(orphanId,orphanSending.Revision,batchSeeds[0].Original,TargetLanguage.English,"Stale");throw new Exception();}catch(RevisionConflictException){count++;}
var customId=Guid.NewGuid();
var customConnection=new GenreTranslationConnection("openai-compatible","https://api.example.com/v1",4000,true);
store.Create(customId,"server","library","cloud-a","alias-model",GenreTranslationPipeline.Version,
    [new("Adventure",TargetLanguage.SimplifiedChinese,0,null)],customConnection);
var customRunner=new GenreTranslationRunner(new GenreTranslationStore(path),(frozen,terms,_)=>
{
 Check(frozen.Id=="cloud-a" && frozen.Kind=="openai-compatible" && frozen.Endpoint==customConnection.Endpoint
     && frozen.Model=="alias-model" && frozen.MaxOutputTokens==4000 && frozen.UseJsonResponseFormat);
 return Task.FromResult(new GenreTranslationResult([new(terms.Single().Id,TargetLanguage.SimplifiedChinese,"冒险",null)],null));
});
Check((await customRunner.RunAsync(customId,CancellationToken.None)).Items.Single().Text=="冒险");
Check(!JsonSerializer.Serialize(connection).Contains("UseJsonResponseFormat"));
Console.WriteLine(JsonSerializer.Serialize(new {passed=count,failed=0}));
sealed class Stub(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
{
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request);
}
