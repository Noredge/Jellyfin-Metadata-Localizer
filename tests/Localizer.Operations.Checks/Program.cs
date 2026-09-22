using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Localizer.Core;
using Microsoft.Data.Sqlite;

if (args.Length==4 && args[0]=="reconcile-child")
{
    var childStore=new CandidateStore(args[1]); var childTarget=new FakeNameTarget(args[2]);
    var before=childTarget.Writes;
    var childResult=await new NameOperationService(childStore,childTarget).ReconcileAsync(args[3]);
    return childResult.State==OperationState.ObservedExpected && childTarget.Writes==before ? 0 : 2;
}
if (args.Length!=2) throw new ArgumentException("Usage: operations-checks <scratch-dir> <report.json>");
var root=Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
var results=new List<object>(); var failed=0;
var key=new MediaKey("server-a","library-a","movie-a"); var scope=new LibraryKey(key.ServerId,key.LibraryId);
var cn=TargetLanguage.SimplifiedChinese; var en=TargetLanguage.English;
var spec=new GenerationSpec("https://provider.example/v1","qwen-baseline","p1","r1","{}","{}","Translate supplied title.");
void Expect(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
async Task Throws<T>(Func<Task> action) where T:Exception
{
    try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected "+typeof(T).Name);
}
async Task Check(string name, Func<Task> action)
{
    try { await action(); results.Add(new {name,passed=true}); Console.WriteLine("PASS "+name); }
    catch(Exception e) { failed++; results.Add(new {name,passed=false,error=e.GetType().Name}); Console.WriteLine("FAIL "+name+" "+e.GetType().Name+": "+e.Message); }
}
(CandidateStore Store,FakeNameTarget Target,LanguagePreviewEntry Preview,string Database,string TargetPath) Fresh()
{
    var directory=Path.Combine(root,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var database=Path.Combine(directory,"candidates.sqlite3"); var targetPath=Path.Combine(directory,"target.json");
    var store=new CandidateStore(database); var source=store.ObserveSource(key,"原文","DEMO · ");
    var candidate=store.SaveGenerated(source.Id,cn,"中文",spec); store.Approve(candidate.Id,candidate.Revision);
    var target=new FakeNameTarget(targetPath,new(new(key,true,false,"原文","DEMO · ","DEMO · 原文"),true));
    return (store,target,new LanguagePreview(store).Build(scope,cn,[target.Snapshot!.Movie]).Single(),database,targetPath);
}
Action<OperationCheckpoint,NameOperation> CrashAt(OperationCheckpoint point) => (at,_)=> { if (at==point) throw new SimulatedCrashException(); };

await Check("confirmed_localized_display_applies_and_restores_without_becoming_source", async()=>
{
    var f=Fresh(); var source=f.Store.ObserveSource(key,"Up","");
    const string confirmedDisplay="飞屋环游记";
    var candidate=f.Store.SaveGenerated(source.Id,cn,"飞屋历险记",spec);
    f.Store.Approve(candidate.Id,candidate.Revision);
    f.Target.Snapshot=f.Target.Snapshot! with { Movie=new(key,true,false,"Up","",confirmedDisplay) };
    Expect(TitleDisplayBaseline.Expected(source,confirmedDisplay,null)==f.Target.Snapshot.Movie.CurrentName);
    var preview=new LanguagePreview(f.Store).Build(scope,cn,[f.Target.Snapshot.Movie]).Single();
    Expect(preview.Status==PreviewStatus.ReadyForReview && preview.ExpectedCurrentName==confirmedDisplay);
    var service=new NameOperationService(f.Store,f.Target);
    var applied=await service.ApplyAsync("localized-apply",scope,preview);
    Expect(applied.State==OperationState.Applied && applied.BeforeValue==confirmedDisplay && applied.OriginalTitle=="Up");
    var point=f.Store.GetRestorePoint(key)!;
    Expect(TitleDisplayBaseline.Expected(source,confirmedDisplay,point)=="飞屋历险记");
    // Explicitly reconfirming the same source must not override the existing application journal.
    Expect(TitleDisplayBaseline.Expected(source,"外部改名",point)=="飞屋历险记");
    Expect((await service.ApplyAsync("localized-apply",scope,preview))==applied && f.Target.Writes==1);
    Expect((await service.RestoreAsync("localized-restore",scope,applied.Id)).State==OperationState.Applied);
    Expect(f.Target.Snapshot.Movie.CurrentName==confirmedDisplay && f.Store.GetCurrentSource(key)==source);
    Expect(TitleDisplayBaseline.Expected(source,"外部改名",f.Store.GetRestorePoint(key))==confirmedDisplay);
});
await Check("external_edit_after_confirmed_localized_display_is_preserved", async()=>
{
    var f=Fresh(); var source=f.Store.ObserveSource(key,"Up","");
    var candidate=f.Store.SaveGenerated(source.Id,cn,"飞屋历险记",spec);
    f.Store.Approve(candidate.Id,candidate.Revision);
    f.Target.Snapshot=f.Target.Snapshot! with { Movie=new(key,true,false,"Up","","飞屋环游记") };
    var preview=new LanguagePreview(f.Store).Build(scope,cn,[f.Target.Snapshot.Movie]).Single();
    f.Target.SetName("管理员在 Jellyfin 修改的标题");
    Expect(TitleDisplayBaseline.Expected(source,"飞屋环游记",null)!=f.Target.Snapshot.Movie.CurrentName);
    var operation=await new NameOperationService(f.Store,f.Target).ApplyAsync("changed-after-confirmation",scope,preview);
    Expect(operation.State==OperationState.Conflict && f.Target.Writes==0
        && f.Target.Snapshot.Movie.CurrentName=="管理员在 Jellyfin 修改的标题");
});
await Check("legacy_title_binding_keeps_strict_display_and_journal_expectations", async()=>
{
    var f=Fresh(); var source=f.Store.GetCurrentSource(key)!;
    Expect(TitleDisplayBaseline.Expected(source,null,null)=="DEMO · 原文");
    Expect(TitleDisplayBaseline.Expected(source,"已确认的显示",null)=="已确认的显示");
    var applied=await new NameOperationService(f.Store,f.Target).ApplyAsync("legacy-apply",scope,f.Preview);
    var point=f.Store.GetRestorePoint(key)!;
    Expect(TitleDisplayBaseline.Expected(source,null,point)==applied.PlannedValue);
    Expect(TitleDisplayBaseline.Expected(source,null,point with { Consumed=true })==applied.BeforeValue);
    Expect(TitleDisplayBaseline.Expected(source with { Id=source.Id+1 },"新来源的显示",point)=="新来源的显示");
    Expect(TitleDisplayBaseline.Expected(source with { Key=key with { ItemId="other-movie" } },"另一个条目的显示",point)=="另一个条目的显示");
});

await Check("original_title_preserves_candidate_and_restores_translation", async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target);
    await service.ApplyAsync("translation",scope,f.Preview);
    var selection=JsonSerializer.Serialize(f.Store.GetSelected(f.Preview.SourceId!.Value,cn));
    var original=await service.ApplyOriginalAsync("original",scope,key,f.Preview.SourceId.Value,"DEMO · 中文");
    Expect(original.State==OperationState.Applied && original.OriginalDisplay && original.CandidateId is null);
    Expect(f.Target.Snapshot!.Movie.CurrentName=="DEMO · 原文");
    Expect(JsonSerializer.Serialize(f.Store.GetSelected(f.Preview.SourceId.Value,cn))==selection);
    var writes=f.Target.Writes;
    await service.ApplyOriginalAsync("original",scope,key,f.Preview.SourceId.Value,"DEMO · 中文");
    Expect(f.Target.Writes==writes);
    await Throws<IdempotencyConflictException>(()=>service.ApplyOriginalAsync("original",scope,key,f.Preview.SourceId.Value,"different"));
    Expect((await service.RestoreAsync("restore-original",scope,"original")).State==OperationState.Applied);
    Expect(f.Target.Snapshot!.Movie.CurrentName=="DEMO · 中文");
});
await Check("original_title_conflict_and_interruption_recovery", async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target);
    await service.ApplyAsync("translation",scope,f.Preview);
    Expect((await service.ApplyOriginalAsync("wrong",scope,key,f.Preview.SourceId!.Value,"different")).State==OperationState.Conflict);
    var crashing=new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.TargetReturned));
    await Throws<SimulatedCrashException>(()=>crashing.ApplyOriginalAsync("crash-original",scope,key,f.Preview.SourceId.Value,"DEMO · 中文"));
    var writes=f.Target.Writes;
    Expect((await service.ReconcileAsync("crash-original")).State==OperationState.ObservedExpected && f.Target.Writes==writes);
});
await Check("apply_and_restore_only_name", async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target); var unrelated=f.Target.Unrelated;
    var applied=await service.ApplyAsync("apply",scope,f.Preview);
    Expect(applied.State==OperationState.Applied && applied.BeforeValue=="DEMO · 原文" && f.Target.Changes==1);
    var restored=await service.RestoreAsync("restore",scope,applied.Id);
    Expect(restored.State==OperationState.Applied && f.Target.Snapshot!.Movie.CurrentName=="DEMO · 原文" && f.Target.Unrelated==unrelated);
    Expect(f.Store.GetRestorePoint(key)!.Consumed);
});
await Check("same_operation_id_never_writes_twice",async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target);
    var a=await service.ApplyAsync("id",scope,f.Preview); var b=await service.ApplyAsync("id",scope,f.Preview);
    Expect(a==b && f.Target.Writes==1);
});
await Check("operation_id_cannot_be_reused_with_new_request",async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target); await service.ApplyAsync("id",scope,f.Preview);
    await Throws<IdempotencyConflictException>(()=>service.ApplyAsync("id",scope,f.Preview with { ProposedName="tampered" }));
    Expect(f.Target.Writes==1);
});
await Check("repeat_apply_nochange_keeps_restore_baseline",async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target); var first=await service.ApplyAsync("a",scope,f.Preview);
    var nochange=await service.ApplyAsync("b",scope,f.Preview);
    Expect(nochange.State==OperationState.NoChange && f.Target.Writes==1 && f.Store.GetRestorePoint(key)!.Application.Id==first.Id);
});
await Check("initial_same_value_does_not_invent_restore_point",async()=>
{
    var f=Fresh(); f.Target.SetName(f.Preview.ProposedName!);
    var operation=await new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview);
    Expect(operation.State==OperationState.NoChange && f.Target.Writes==0 && f.Store.GetRestorePoint(key)==null);
});
await Check("repeated_restore_does_not_create_undo_redo_chain",async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target); await service.ApplyAsync("a",scope,f.Preview);
    await service.RestoreAsync("r1",scope,"a"); var repeated=await service.RestoreAsync("r2",scope,"a");
    Expect(repeated.State==OperationState.NoChange && f.Target.Writes==2 && f.Store.GetRestorePoint(key)!.Application.Id=="a");
});
await Check("preview_then_external_edit_is_conflict",async()=>
{
    var f=Fresh(); f.Target.SetName("external edit");
    var operation=await new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview);
    Expect(operation.State==OperationState.Conflict && f.Target.Writes==0 && f.Target.Snapshot!.Movie.CurrentName=="external edit");
});
await Check("edited_candidate_invalidates_old_preview",async()=>
{
    var f=Fresh(); var candidate=f.Store.GetCandidate(f.Preview.CandidateId!); f.Store.Edit(candidate.Id,candidate.Revision,"新稿");
    await Throws<OperationValidationException>(()=>new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview));
    Expect(f.Target.Writes==0 && f.Store.FindOperation("a")==null);
});
await Check("changed_selection_invalidates_old_preview",async()=>
{
    var f=Fresh(); var c=f.Store.SaveGenerated(f.Preview.SourceId!.Value,cn,"另稿",spec with { Model="new" });
    f.Store.Approve(c.Id,c.Revision); f.Store.Select(c.Id,f.Preview.SelectionRevision!.Value);
    await Throws<OperationValidationException>(()=>new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview)); Expect(f.Target.Writes==0);
});
await Check("changed_local_source_invalidates_old_preview",async()=>
{
    var f=Fresh(); f.Store.ObserveSource(key,"新原文","DEMO · ");
    await Throws<OperationValidationException>(()=>new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview)); Expect(f.Target.Writes==0);
});
await Check("tampered_target_value_is_rejected",async()=>
{
    var f=Fresh(); await Throws<OperationValidationException>(()=>new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview with { ProposedName="forged" })); Expect(f.Target.Writes==0);
});
await Check("scope_lock_missing_type_source_and_saver_guards",async()=>
{
    foreach (var change in new Func<NameTargetSnapshot,NameTargetSnapshot?>[] {
        x=>x with { Movie=x.Movie with { NameLocked=true } },x=>x with { MetadataSaversExplicitlyDisabled=false },
        x=>null,x=>x with { Movie=x.Movie with { IsMovie=false } },x=>x with { Movie=x.Movie with { OriginalTitle="changed" } },
        x=>x with { Movie=x.Movie with { Key=key with { LibraryId="outside" } } } })
    {
        var f=Fresh(); f.Target.Snapshot=change(f.Target.Snapshot!);
        var operation=await new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview);
        Expect(operation.State==OperationState.Blocked && f.Target.Writes==0);
    }
});
await Check("late_adapter_value_conflict_does_not_overwrite",async()=>
{
    var f=Fresh(); f.Target.BeforeWrite=()=>f.Target.SetName("late edit");
    var operation=await new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview);
    Expect(operation.State==OperationState.Conflict && f.Target.Changes==0 && f.Target.Snapshot!.Movie.CurrentName=="late edit");
});
await Check("late_adapter_lock_is_respected",async()=>
{
    var f=Fresh(); f.Target.BeforeWrite=()=>f.Target.Snapshot=f.Target.Snapshot! with { Movie=f.Target.Snapshot!.Movie with { NameLocked=true } };
    var operation=await new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview);
    Expect(operation.State==OperationState.Blocked && f.Target.Changes==0);
});
await Check("intent_survives_crash_without_auto_resume",async()=>
{
    var f=Fresh(); await Throws<SimulatedCrashException>(()=>new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.IntentSaved)).ApplyAsync("a",scope,f.Preview));
    var reopened=new CandidateStore(f.Database); Expect(reopened.GetOperation("a").State==OperationState.Prepared && f.Target.Writes==0);
    var observation=await new NameOperationService(reopened,f.Target).ReconcileAsync("a");
    Expect(observation.State==OperationState.Retryable && f.Target.Writes==0);
    var resumed=await new NameOperationService(reopened,f.Target).ResumeAsync("a",scope); Expect(resumed.State==OperationState.Applied && f.Target.Writes==1);
});
await Check("crash_after_write_mark_before_target_is_retryable",async()=>
{
    var f=Fresh(); await Throws<SimulatedCrashException>(()=>new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.WriteMarked)).ApplyAsync("a",scope,f.Preview));
    Expect(f.Store.GetOperation("a").State==OperationState.Writing && f.Target.Writes==0);
    var result=await new NameOperationService(f.Store,f.Target).ReconcileAsync("a"); Expect(result.State==OperationState.Retryable && f.Target.Writes==0);
});
await Check("successful_write_before_bookkeeping_is_not_repeated",async()=>
{
    var f=Fresh(); await Throws<SimulatedCrashException>(()=>new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.TargetReturned)).ApplyAsync("a",scope,f.Preview));
    Expect(f.Target.Changes==1 && f.Store.GetOperation("a").State==OperationState.Writing);
    var result=await new NameOperationService(f.Store,f.Target).ResumeAsync("a",scope);
    Expect(result.State==OperationState.ObservedExpected && result.ObservedOnly && f.Target.Writes==1);
});
await Check("readback_before_bookkeeping_crash_is_recoverable",async()=>
{
    var f=Fresh(); await Throws<SimulatedCrashException>(()=>new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.BeforeCompletion)).ApplyAsync("a",scope,f.Preview));
    var result=await new NameOperationService(f.Store,f.Target).ReconcileAsync("a"); Expect(result.State==OperationState.ObservedExpected && f.Target.Writes==1);
});
await Check("provider_write_error_before_effect_is_manual_retry",async()=>
{
    var f=Fresh(); f.Target.Mode=WriteMode.ThrowBefore;
    var service=new NameOperationService(f.Store,f.Target); var result=await service.ApplyAsync("a",scope,f.Preview);
    Expect(result.State==OperationState.Retryable && f.Target.Writes==1 && f.Target.Changes==0);
    f.Target.Mode=WriteMode.Normal; result=await service.ResumeAsync("a",scope); Expect(result.State==OperationState.Applied && f.Target.Writes==2);
});
await Check("timeout_after_effect_keeps_uncertain_attribution",async()=>
{
    var f=Fresh(); f.Target.Mode=WriteMode.ThrowAfter;
    var service=new NameOperationService(f.Store,f.Target); var result=await service.ApplyAsync("a",scope,f.Preview);
    Expect(result.State==OperationState.ObservedExpected && result.ObservedOnly && f.Target.Writes==1);
    await Throws<OperationValidationException>(()=>service.RestoreAsync("r",scope,"a"));
    f.Target.Mode=WriteMode.Normal; var restored=await service.RestoreAsync("r",scope,"a",acceptUncertainAttribution:true);
    Expect(restored.State==OperationState.Applied && f.Target.Writes==2);
});
await Check("third_value_after_error_is_conflict",async()=>
{
    var f=Fresh(); f.Target.Mode=WriteMode.ThirdValueThenThrow;
    var result=await new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview);
    Expect(result.State==OperationState.Conflict && f.Store.GetRestorePoint(key)==null);
});
await Check("success_response_without_effect_is_not_applied",async()=>
{
    var f=Fresh(); f.Target.Mode=WriteMode.ReturnWithoutEffect;
    var result=await new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview);
    Expect(result.State==OperationState.Retryable && f.Store.GetRestorePoint(key)==null);
});
await Check("unavailable_readback_preserves_pending_intent",async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target,(point,_)=> { if (point==OperationCheckpoint.TargetReturned) f.Target.FailReads=true; });
    var result=await service.ApplyAsync("a",scope,f.Preview);
    Expect(result.State==OperationState.Writing && result.ErrorCategory=="ReadbackUnavailable" && f.Store.ListUnresolvedOperations().Count==1);
    f.Target.FailReads=false; result=await new NameOperationService(f.Store,f.Target).ReconcileAsync("a"); Expect(result.State==OperationState.ObservedExpected && f.Target.Writes==1);
});
await Check("new_lock_after_write_does_not_hide_success",async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target,(point,_)=> { if (point==OperationCheckpoint.TargetReturned) f.Target.Snapshot=f.Target.Snapshot! with { Movie=f.Target.Snapshot!.Movie with { NameLocked=true } }; });
    var result=await service.ApplyAsync("a",scope,f.Preview); Expect(result.State==OperationState.Applied && result.ErrorCategory=="PostWriteGuard:NameLocked");
    var restore=await new NameOperationService(f.Store,f.Target).RestoreAsync("r",scope,"a"); Expect(restore.State==OperationState.Blocked && f.Target.Writes==1);
});
await Check("restore_refuses_user_edit_after_apply",async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target); await service.ApplyAsync("a",scope,f.Preview); f.Target.SetName("human edit");
    var restore=await service.RestoreAsync("r",scope,"a"); Expect(restore.State==OperationState.Conflict && f.Target.Writes==1 && f.Target.Snapshot!.Movie.CurrentName=="human edit");
});
await Check("switch_english_then_restore_returns_previous_chinese",async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target); await service.ApplyAsync("zh",scope,f.Preview);
    var english=f.Store.SaveGenerated(f.Preview.SourceId!.Value,en,"English",spec); f.Store.Approve(english.Id,english.Revision);
    var preview=new LanguagePreview(f.Store).Build(scope,en,[f.Target.Snapshot!.Movie]).Single(); await service.ApplyAsync("en",scope,preview);
    Expect(f.Store.GetRestorePoint(key)!.Application.BeforeValue=="DEMO · 中文");
    await Throws<OperationValidationException>(()=>service.RestoreAsync("old-restore",scope,"zh"));
    await service.RestoreAsync("restore-en",scope,"en"); Expect(f.Target.Snapshot!.Movie.CurrentName=="DEMO · 中文" && f.Target.Writes==3);
});
await Check("restore_crash_after_effect_is_not_repeated",async()=>
{
    var f=Fresh(); await new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview);
    await Throws<SimulatedCrashException>(()=>new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.TargetReturned)).RestoreAsync("r",scope,"a"));
    var recovered=await new NameOperationService(f.Store,f.Target).ResumeAsync("r",scope);
    Expect(recovered.State==OperationState.ObservedExpected && f.Target.Writes==2 && f.Store.GetRestorePoint(key)!.Consumed);
});
await Check("pending_operation_freezes_source_edits_and_selection",async()=>
{
    var f=Fresh(); await Throws<SimulatedCrashException>(()=>new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.IntentSaved)).ApplyAsync("a",scope,f.Preview));
    var candidate=f.Store.GetCandidate(f.Preview.CandidateId!);
    await Throws<OperationBusyException>(()=>Task.Run(()=>f.Store.Edit(candidate.Id,candidate.Revision,"late edit")));
    await Throws<OperationBusyException>(()=>Task.Run(()=>f.Store.ObserveSource(key,"new original","DEMO · ")));
    await Throws<OperationBusyException>(()=>Task.Run(()=>f.Store.Select(candidate.Id,f.Preview.SelectionRevision!.Value)));
    await Throws<OperationBusyException>(()=>new NameOperationService(f.Store,f.Target).ApplyAsync("b",scope,f.Preview));
    Expect(f.Target.Writes==0);
});
await Check("writer_lease_blocks_other_service_during_io",async()=>
{
    var f=Fresh(); var blocked=false;
    var service=new NameOperationService(f.Store,f.Target,(point,_)=> { if (point==OperationCheckpoint.IntentSaved) { try { new NameOperationService(new CandidateStore(f.Database),f.Target).ReconcileAsync("a").GetAwaiter().GetResult(); } catch (OperationBusyException) { blocked=true; } } });
    await service.ApplyAsync("a",scope,f.Preview); Expect(blocked && f.Target.Writes==1);
});
await Check("cancel_pending_before_effect_releases_edit_lock",async()=>
{
    var f=Fresh(); await Throws<SimulatedCrashException>(()=>new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.IntentSaved)).ApplyAsync("a",scope,f.Preview));
    var cancelled=await new NameOperationService(f.Store,f.Target).CancelPendingAsync("a");
    Expect(cancelled.State==OperationState.Cancelled && f.Target.Writes==0 && f.Store.ListUnresolvedOperations().Count==0);
    var c=f.Store.GetCandidate(f.Preview.CandidateId!); f.Store.Edit(c.Id,c.Revision,"editable again");
});
await Check("cancel_does_not_hide_already_completed_effect",async()=>
{
    var f=Fresh(); await Throws<SimulatedCrashException>(()=>new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.TargetReturned)).ApplyAsync("a",scope,f.Preview));
    var result=await new NameOperationService(f.Store,f.Target).CancelPendingAsync("a"); Expect(result.State==OperationState.ObservedExpected && f.Target.Writes==1);
});
await Check("cancelled_token_before_start_has_no_intent",async()=>
{
    var f=Fresh(); using var cts=new CancellationTokenSource(); cts.Cancel();
    await Throws<OperationCanceledException>(()=>new NameOperationService(f.Store,f.Target).ApplyAsync("a",scope,f.Preview,cts.Token));
    Expect(f.Store.FindOperation("a")==null && f.Target.Writes==0);
});
await Check("schema_v1_migration_preserves_human_candidate",async()=>
{
    var f=Fresh(); var candidate=f.Store.GetCandidate(f.Preview.CandidateId!); f.Store.Edit(candidate.Id,candidate.Revision,"saved human edit");
    using (var db=new SqliteConnection("Data Source="+f.Database)) { db.Open(); using var command=db.CreateCommand(); command.CommandText="DROP TABLE genre_save_receipts; DROP TABLE translation_items; DROP TABLE translation_jobs; DROP TABLE genre_restore_heads; DROP TABLE genre_operations; DROP TABLE genre_mappings; DROP TABLE genre_overrides; DROP TABLE genre_source_heads; DROP TABLE genre_sources; DROP TABLE name_restore_heads; DROP TABLE name_operations; PRAGMA user_version=1;"; command.ExecuteNonQuery(); }
    var migrated=new CandidateStore(f.Database); Expect(migrated.GetCandidate(candidate.Id).Text=="saved human edit" && migrated.ListUnresolvedOperations().Count==0);
    await Task.CompletedTask;
});
await Check("separate_process_recovers_persisted_effect_without_write",async()=>
{
    var f=Fresh(); await Throws<SimulatedCrashException>(()=>new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.TargetReturned)).ApplyAsync("a",scope,f.Preview));
    var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach(var value in new[] {"reconcile-child",f.Database,f.TargetPath,"a"}) start.ArgumentList.Add(value);
    using var process=Process.Start(start)!; if (!process.WaitForExit(20000)) { process.Kill(); throw new TimeoutException(); }
    Expect(process.ExitCode==0 && new CandidateStore(f.Database).GetOperation("a").State==OperationState.ObservedExpected && new FakeNameTarget(f.TargetPath).Writes==1);
});

await Check("restore_rechecks_selected_library_scope",async()=>
{
    var f=Fresh(); var service=new NameOperationService(f.Store,f.Target); await service.ApplyAsync("a",scope,f.Preview);
    await Throws<OperationValidationException>(()=>service.RestoreAsync("r",new("server-a","another-library"),"a"));
    Expect(f.Target.Writes==1 && f.Store.FindOperation("r")==null);
});
await Check("resume_rechecks_selected_library_scope",async()=>
{
    var f=Fresh(); await Throws<SimulatedCrashException>(()=>new NameOperationService(f.Store,f.Target,CrashAt(OperationCheckpoint.IntentSaved)).ApplyAsync("a",scope,f.Preview));
    await Throws<OperationValidationException>(()=>new NameOperationService(f.Store,f.Target).ResumeAsync("a",new("server-a","another-library")));
    Expect(f.Target.Writes==0 && f.Store.GetOperation("a").State==OperationState.Prepared);
});

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1],JsonSerializer.Serialize(new {stage="M3-B title application journal",total=results.Count,passed=results.Count-failed,failed,
    runtime=Environment.Version.ToString(),coreTarget="net9.0",modelRequests=0,productionJellyfinWrites=0,target="persistent synthetic adapter; not Jellyfin",results},new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine($"{results.Count-failed}/{results.Count} operation checks passed.");
return failed==0 ? 0 : 1;
