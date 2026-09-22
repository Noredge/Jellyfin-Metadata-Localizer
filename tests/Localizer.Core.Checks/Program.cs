using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Localizer.Core;
using Microsoft.Data.Sqlite;

if (args.Length == 3 && args[0] == "verify-restart")
{
    var db = new CandidateStore(args[1]);
    var restored = db.GetCandidate(args[2]);
    if (restored.Text != "人工中文" || !restored.IsHumanEdited || !restored.Approved) return 2;
    return 0;
}

if (args.Length != 2) throw new ArgumentException("Usage: checks <scratch-directory> <report.json>");
var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var results = new List<object>();
var failures = 0;
var cn = TargetLanguage.SimplifiedChinese;
var en = TargetLanguage.English;
var key = new MediaKey("server-a","library-a","movie-a");
var scope = new LibraryKey(key.ServerId,key.LibraryId);
var spec = new GenerationSpec("https://provider.example/v1","qwen-baseline","prompt-1","rules-1","{\"temperature\":0.2}","{}","Translate the supplied title without adding facts.");

void Check(string name, Action test)
{
    try { test(); results.Add(new { name, passed=true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; results.Add(new { name, passed=false, error=error.GetType().Name }); Console.WriteLine("FAIL " + name + " " + error.GetType().Name + ": " + error.Message); }
}
void Expect(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
(CandidateStore Store,SourceSnapshot Source) Fresh()
{
    var store = new CandidateStore(Path.Combine(root,Guid.NewGuid().ToString("N")+".sqlite3"));
    return (store,store.ObserveSource(key,"元の題名","DEMO-001 · "));
}
ObservedMovie Movie(string current="DEMO-001 · 元の題名") => new(key,true,false,"元の題名","DEMO-001 · ",current);

Check("source_reobserve_reuses_version", () =>
{
    var (s,source)=Fresh();
    Expect(s.ObserveSource(key,source.OriginalTitle,source.DisplayPrefix).Id==source.Id);
});
Check("languages_have_independent_candidates_and_fingerprints", () =>
{
    var (s,source)=Fresh();
    var a=s.SaveGenerated(source.Id,cn,"中文候选",spec);
    var b=s.SaveGenerated(source.Id,en,"English candidate",spec);
    Expect(a.Id!=b.Id && a.InputFingerprint!=b.InputFingerprint);
    Expect(s.GetSelected(source.Id,cn)!.Candidate.Text=="中文候选");
    Expect(s.GetSelected(source.Id,en)!.Candidate.Text=="English candidate");
});
Check("same_generation_reuses_first_result_without_overwrite", () =>
{
    var (s,source)=Fresh();
    var a=s.SaveGenerated(source.Id,cn,"第一次",spec);
    var b=s.SaveGenerated(source.Id,cn,"另一次随机输出",spec);
    Expect(a.Id==b.Id && b.GeneratedText=="第一次" && s.ListCandidates(source.Id,cn).Count==1);
});
Check("human_edit_and_other_language_survive_regeneration", () =>
{
    var (s,source)=Fresh();
    var a=s.SaveGenerated(source.Id,cn,"机器中文",spec);
    var b=s.SaveGenerated(source.Id,en,"English original",spec);
    a=s.Edit(a.Id,a.Revision,"人工中文");
    a=s.Approve(a.Id,a.Revision);
    var newer=s.SaveGenerated(source.Id,cn,"新规则中文",spec with { RulesVersion="rules-2" });
    Expect(s.GetSelected(source.Id,cn)!.Candidate.Id==a.Id && s.GetCandidate(a.Id).Text=="人工中文");
    Expect(s.GetCandidate(a.Id).GeneratedText=="机器中文" && s.GetCandidate(b.Id).Text=="English original");
    Expect(newer.Id!=a.Id && !newer.Approved);
});
Check("approval_is_invalidated_by_edit", () =>
{
    var (s,source)=Fresh(); var a=s.SaveGenerated(source.Id,cn,"初稿",spec);
    a=s.Approve(a.Id,a.Revision); var approved=a;
    a=s.Edit(a.Id,a.Revision,"修改稿");
    Expect(!a.Approved && a.Revision>approved.Revision);
    Throws<RevisionConflictException>(()=>s.Approve(a.Id,approved.Revision));
});
Check("two_editors_cannot_silently_overwrite", () =>
{
    var (s,source)=Fresh(); var a=s.SaveGenerated(source.Id,cn,"原稿",spec);
    s.Edit(a.Id,a.Revision,"编辑者一");
    Throws<RevisionConflictException>(()=>s.Edit(a.Id,a.Revision,"编辑者二"));
    Expect(s.GetCandidate(a.Id).Text=="编辑者一");
});
Check("explicit_selection_has_revision_guard", () =>
{
    var (s,source)=Fresh(); var a=s.SaveGenerated(source.Id,cn,"甲",spec);
    var b=s.SaveGenerated(source.Id,cn,"乙",spec with { Model="other" });
    var old=s.GetSelected(source.Id,cn)!;
    var selected=s.Select(b.Id,old.Revision);
    Expect(selected.Candidate.Id==b.Id && selected.Revision==old.Revision+1);
    Throws<RevisionConflictException>(()=>s.Select(a.Id,old.Revision));
});
Check("changed_source_hides_old_languages_but_retains_edits", () =>
{
    var (s,source)=Fresh(); var a=s.SaveGenerated(source.Id,cn,"旧译文",spec);
    a=s.Edit(a.Id,a.Revision,"旧人工稿");
    var current=s.ObserveSource(key,"新原文",source.DisplayPrefix);
    Expect(current.Id!=source.Id && current.Version==source.Version+1);
    Expect(s.GetSelected(current.Id,cn)==null && s.GetSelected(current.Id,en)==null);
    Expect(s.GetCandidate(a.Id).Text=="旧人工稿");
    Throws<SourceChangedException>(()=>s.Select(a.Id,1));
});
Check("late_provider_result_does_not_replace_new_source", () =>
{
    var (s,old)=Fresh(); var current=s.ObserveSource(key,"更新原文",old.DisplayPrefix);
    s.SaveGenerated(old.Id,en,"Delayed old translation",spec);
    Expect(s.GetCurrentSource(key)!.Id==current.Id && s.GetSelected(current.Id,en)==null);
});
Check("returning_source_does_not_resurrect_old_selection", () =>
{
    var (s,old)=Fresh(); s.SaveGenerated(old.Id,cn,"旧候选",spec);
    s.ObserveSource(key,"另一个原文",old.DisplayPrefix);
    var again=s.ObserveSource(key,old.OriginalTitle,old.DisplayPrefix);
    Expect(again.Version==3 && again.Id!=old.Id && s.GetSelected(again.Id,cn)==null);
});
Check("server_library_item_identity_are_all_scoped", () =>
{
    var (s,a)=Fresh();
    foreach (var other in new[] { key with { ServerId="server-b" },key with { LibraryId="library-b" },key with { ItemId="movie-b" } })
    {
        var b=s.ObserveSource(other,a.OriginalTitle,a.DisplayPrefix);
        Expect(b.Id!=a.Id);
        s.SaveGenerated(b.Id,cn,"其他条目",spec);
    }
    Expect(s.GetSelected(a.Id,cn)==null);
});
Check("model_rules_and_context_change_identity_without_switching", () =>
{
    var (s,source)=Fresh(); var initial=s.SaveGenerated(source.Id,en,"Initial",spec);
    foreach (var changed in new[] {spec with { Model="m2" },spec with { RulesVersion="r2" },spec with { ContextJson="{\"name\":\"sample\"}" },spec with { ParametersJson="{\"temperature\":0.3}" },spec with { PromptVersion="p2" },spec with { Endpoint="https://other.example/v1" },spec with { PromptText="A changed actual prompt with the same version label." }})
        Expect(s.SaveGenerated(source.Id,en,"Alternative",changed).Id!=initial.Id);
    Expect(s.ListCandidates(source.Id,en).Count==8 && s.GetSelected(source.Id,en)!.Candidate.Id==initial.Id);
});
Check("preview_missing_language_does_not_generate_or_select_chinese", () =>
{
    var (s,source)=Fresh(); s.SaveGenerated(source.Id,cn,"中文",spec);
    var preview=new LanguagePreview(s).Build(scope,en,[Movie()]).Single();
    Expect(preview.Status==PreviewStatus.MissingCandidate && preview.ProposedName==null);
    Expect(s.ListCandidates(source.Id,en).Count==0);
});
Check("preview_requires_approval_and_preserves_prefix", () =>
{
    var (s,source)=Fresh(); var a=s.SaveGenerated(source.Id,en,"English title",spec);
    var service=new LanguagePreview(s);
    Expect(service.Build(scope,en,[Movie()]).Single().Status==PreviewStatus.NeedsApproval);
    a=s.Approve(a.Id,a.Revision);
    var preview=service.Build(scope,en,[Movie()]).Single();
    Expect(preview.Status==PreviewStatus.ReadyForReview && preview.ProposedName=="DEMO-001 · English title");
    Expect(preview.ExpectedCurrentName==Movie().CurrentName && preview.CandidateRevision==a.Revision);
});
Check("preview_toggle_reuses_each_human_edited_language", () =>
{
    var (s,source)=Fresh(); var a=s.SaveGenerated(source.Id,cn,"机器中文",spec); var b=s.SaveGenerated(source.Id,en,"Machine English",spec);
    a=s.Edit(a.Id,a.Revision,"人工中文"); a=s.Approve(a.Id,a.Revision);
    b=s.Edit(b.Id,b.Revision,"Edited English"); b=s.Approve(b.Id,b.Revision);
    var service=new LanguagePreview(s);
    for (var i=0;i<5;i++)
    {
        Expect(service.Build(scope,cn,[Movie("DEMO-001 · Edited English")]).Single().ProposedName=="DEMO-001 · 人工中文");
        Expect(service.Build(scope,en,[Movie("DEMO-001 · 人工中文")]).Single().ProposedName=="DEMO-001 · Edited English");
    }
    Expect(s.ListCandidates(source.Id,cn).Count==1 && s.ListCandidates(source.Id,en).Count==1);
});
Check("preview_no_change_is_distinct_from_ready", () =>
{
    var (s,source)=Fresh(); var a=s.SaveGenerated(source.Id,cn,"中文",spec); s.Approve(a.Id,a.Revision);
    Expect(new LanguagePreview(s).Build(scope,cn,[Movie("DEMO-001 · 中文")]).Single().Status==PreviewStatus.AlreadyMatches);
});
Check("preview_rejects_lock_scope_type_and_source_drift", () =>
{
    var (s,_)=Fresh(); var p=new LanguagePreview(s);
    Expect(p.Build(scope,en,[Movie() with { NameLocked=true }]).Single().Status==PreviewStatus.Locked);
    Expect(p.Build(scope,en,[Movie() with { IsMovie=false }]).Single().Status==PreviewStatus.OutOfScope);
    Expect(p.Build(scope,en,[Movie() with { Key=key with { LibraryId="outside" } }]).Single().Status==PreviewStatus.OutOfScope);
    Expect(p.Build(scope,en,[Movie() with { OriginalTitle="刷新后的原文" }]).Single().Status==PreviewStatus.SourceChanged);
    Expect(p.Build(scope,en,[Movie() with { DisplayPrefix="NEW · " }]).Single().Status==PreviewStatus.SourceChanged);
    Expect(p.Build(scope,en,[Movie() with { Key=key with { ItemId="missing" } }]).Single().Status==PreviewStatus.MissingSource);
    Throws<ArgumentException>(()=>p.Build(scope,en,[Movie(),Movie()]));
});
Check("blank_input_and_unknown_languages_are_rejected", () =>
{
    var (s,source)=Fresh();
    Throws<ArgumentException>(()=>s.SaveGenerated(source.Id,cn," ",spec));
    Throws<ArgumentOutOfRangeException>(()=>s.SaveGenerated(source.Id,(TargetLanguage)99,"text",spec));
    Throws<ArgumentException>(()=>s.ObserveSource(key," ",""));
    Throws<ArgumentException>(()=>s.SaveGenerated(source.Id,cn,"text",spec with { ContextJson="[]" }));
});
Check("quotes_and_unicode_roundtrip_without_sql_interpretation", () =>
{
    var (s,source)=Fresh(); const string value="a'); DROP TABLE sources; -- 中文 🐈";
    var a=s.SaveGenerated(source.Id,cn,value,spec);
    Expect(s.GetCandidate(a.Id).Text==value && s.GetCurrentSource(key)!.Id==source.Id);
});
Check("separate_process_retains_approved_human_edit", () =>
{
    var path=Path.Combine(root,Guid.NewGuid().ToString("N")+".sqlite3"); var s=new CandidateStore(path);
    var source=s.ObserveSource(key,"原文",""); var a=s.SaveGenerated(source.Id,cn,"机器",spec);
    a=s.Edit(a.Id,a.Revision,"人工中文"); a=s.Approve(a.Id,a.Revision);
    var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("verify-restart"); start.ArgumentList.Add(path); start.ArgumentList.Add(a.Id);
    using var process=Process.Start(start)!;
    if (!process.WaitForExit(20000)) { process.Kill(); throw new TimeoutException(); }
    Expect(process.ExitCode==0);
});
Check("unrelated_database_is_not_adopted", () =>
{
    var path=Path.Combine(root,Guid.NewGuid().ToString("N")+".sqlite3");
    using (var db=new SqliteConnection("Data Source="+path)) { db.Open(); using var cmd=db.CreateCommand(); cmd.CommandText="CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('preserve');"; cmd.ExecuteNonQuery(); }
    Throws<InvalidOperationException>(()=>new CandidateStore(path));
    using var verify=new SqliteConnection("Data Source="+path); verify.Open(); using var check=verify.CreateCommand(); check.CommandText="SELECT value FROM unrelated;";
    Expect((string)check.ExecuteScalar()! == "preserve");
});
Check("two_concurrent_edits_have_exactly_one_winner", () =>
{
    var (s,source)=Fresh(); var a=s.SaveGenerated(source.Id,cn,"draft",spec); var wins=0; var conflicts=0;
    Parallel.For(0,2,index=> { try { s.Edit(a.Id,a.Revision,"editor "+index); Interlocked.Increment(ref wins); } catch (RevisionConflictException) { Interlocked.Increment(ref conflicts); } });
    Expect(wins==1 && conflicts==1 && s.GetCandidate(a.Id).Revision==2);
});

Check("m12_atomic_replacement_preserves_history_and_exact_replay", () =>
{
    var (s, source) = Fresh(); var old = s.SaveGenerated(source.Id, cn, "old", spec);
    old = s.Edit(old.Id, old.Revision, "human"); old = s.Approve(old.Id, old.Revision);
    var baseline = s.GetReplacementBaseline(source.Id, cn); var revision = s.GetSelected(source.Id, cn)!.Revision;
    var next = s.SaveGenerated(source.Id, cn, "new", spec with { PromptVersion = "m12-new" });
    var selected = s.AcceptReplacementCandidate(key, cn, source.Id, next.Id, baseline, revision);
    Expect(selected.Candidate.Approved && selected.Candidate.Revision == 2 && selected.Revision == revision + 1);
    Expect(s.AcceptReplacementCandidate(key, cn, source.Id, next.Id, baseline, revision) == selected);
    Expect(s.GetCandidate(old.Id) == old && s.ListCandidates(source.Id, cn).Count == 2);
});
Check("m12_concurrent_human_edit_blocks_replacement_without_approval", () =>
{
    var (s, source) = Fresh(); var old = s.SaveGenerated(source.Id, cn, "old", spec);
    var baseline = s.GetReplacementBaseline(source.Id, cn); var next = s.SaveGenerated(source.Id, cn, "new", spec with { PromptVersion = "m12-new" });
    s.Edit(old.Id, old.Revision, "new human edit");
    Throws<RevisionConflictException>(() => s.AcceptReplacementCandidate(key, cn, source.Id, next.Id, baseline, 1));
    Expect(!s.GetCandidate(next.Id).Approved && s.GetSelected(source.Id, cn)!.Candidate.Text == "new human edit");
});
Check("m12_unrelated_generated_candidate_invalidates_frozen_baseline", () =>
{
    var (s, source) = Fresh(); var baseline = s.GetReplacementBaseline(source.Id, cn);
    var next = s.SaveGenerated(source.Id, cn, "new", spec);
    s.SaveGenerated(source.Id, cn, "other", spec with { PromptVersion = "other" });
    Throws<RevisionConflictException>(() => s.AcceptReplacementCandidate(key, cn, source.Id, next.Id, baseline, 0));
    Expect(!s.GetCandidate(next.Id).Approved);
});
Check("m12_first_candidate_acceptance_and_language_isolation", () =>
{
    var (s, source) = Fresh(); var baseline = s.GetReplacementBaseline(source.Id, cn);
    var next = s.SaveGenerated(source.Id, cn, "new", spec); var english = s.SaveGenerated(source.Id, en, "English", spec);
    var selected = s.AcceptReplacementCandidate(key, cn, source.Id, next.Id, baseline, 0);
    Expect(selected.Revision == 1 && selected.Candidate.Approved && s.GetCandidate(english.Id) == english);
});
Check("m12_accepted_selection_changed_away_and_back_rejects_replay", () =>
{
    var (s, source) = Fresh(); var old = s.SaveGenerated(source.Id, cn, "old", spec);
    var baseline = s.GetReplacementBaseline(source.Id, cn); var next = s.SaveGenerated(source.Id, cn, "new", spec with { PromptVersion = "next" });
    var selected = s.AcceptReplacementCandidate(key, cn, source.Id, next.Id, baseline, 1);
    var alternate = s.Select(old.Id, selected.Revision); s.Select(next.Id, alternate.Revision);
    Throws<RevisionConflictException>(() => s.AcceptReplacementCandidate(key, cn, source.Id, next.Id, baseline, 1));
});
CampaignChecks.Run(root, Check);
PreviewChecks.Run(root, Check);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1],JsonSerializer.Serialize(new { stage="M3-A candidate storage and language preview", total=results.Count, passed=results.Count-failures, failed=failures,
    runtime=Environment.Version.ToString(), coreTarget="net9.0", providerRequests=0, jellyfinWrites=0, results },new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine($"{results.Count-failures}/{results.Count} checks passed.");
return failures==0 ? 0 : 1;
