using System.Text.Json;
using Localizer.Core;
using Localizer.Translation;

var root = Path.Combine(Path.GetTempPath(), "jml-overview-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool value) { if (!value) throw new Exception("Check failed: " + checks); checks++; }
void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
try
{
    var media = Path.Combine(root, "example.mkv"); File.WriteAllText(media, "");
    var nfo = Path.ChangeExtension(media, ".nfo");
    const string original = "物語\n販売期間：2026年。配送不可。";
    File.WriteAllText(nfo, "<movie><plot>" + original + "</plot><genre>test</genre></movie>");
    var bytes = File.ReadAllBytes(nfo); var source = NfoOverview.Read(media);
    Check(source.Error is null && source.Text == original);
    var store = new OverviewStore(Path.Combine(root, "overview.db")); var key = new MediaKey("server", "library", "item");
    var preferences = new DisplayPreferenceStore(Path.Combine(root, "display.db"));
    Check(preferences.Read(key, DisplayField.Name) == new DisplayPreference(0, DisplayPreferenceMode.FollowLibrary));
    Check(preferences.Set(key, DisplayField.Name, 0, DisplayPreferenceMode.Original).Revision == 1);
    Check(preferences.Read(key, DisplayField.Overview).Mode == DisplayPreferenceMode.FollowLibrary);
    Check(preferences.Read(new("server", "other", "item"), DisplayField.Name).Revision == 0);
    Check(new DisplayPreferenceStore(Path.Combine(root, "display.db")).Read(key, DisplayField.Name).Mode == DisplayPreferenceMode.Original);
    Throws<RevisionConflictException>(() => preferences.Set(key, DisplayField.Name, 0, DisplayPreferenceMode.FollowLibrary));
    Check(preferences.Set(key, DisplayField.Name, 1, DisplayPreferenceMode.Original).Revision == 1);
    Check(preferences.Set(key, DisplayField.Name, 1, DisplayPreferenceMode.FollowLibrary).Revision == 2);
    Throws<ArgumentException>(() => preferences.Set(key, (DisplayField)99, 0, DisplayPreferenceMode.Original));
    Throws<ArgumentException>(() => preferences.Set(key, DisplayField.Name, 2, (DisplayPreferenceMode)99));
    Throws<InvalidOperationException>(() => new DisplayPreferenceStore(Path.Combine(root, "overview.db")));
    var state = store.Observe(key, 0, source); Check(state.Source!.Version == 1);
    Check(store.Observe(key, state.Revision, source).Revision == state.Revision);
    state = store.Save(key, state.Revision, 1, TargetLanguage.English, "Story\nPromotion and shipping terms.", false, "fake", "test", "v1");
    state = store.Save(key, state.Revision, 1, TargetLanguage.SimplifiedChinese, "故事\n促销与配送说明。", false, "fake", "test", "v1");
    Check(state.Selected(TargetLanguage.English)!.Text.StartsWith("Story") && state.Selected(TargetLanguage.SimplifiedChinese) is not null);
    var stableId = Guid.NewGuid().ToString("N");
    state = store.Save(key, state.Revision, 1, TargetLanguage.English, "Durable received output", false, "test", "test", "v1", candidateId: stableId);
    var stableRevision = state.Revision;
    state = store.Save(key, state.Revision, 1, TargetLanguage.English, "Durable received output", false, "test", "test", "v1", candidateId: stableId);
    Check(state.Revision == stableRevision && state.Candidates.Count(x => x.Id == stableId) == 1);
    Throws<IdempotencyConflictException>(() => store.Save(key, state.Revision, 1, TargetLanguage.English, "Different output", false, "test", "test", "v1", candidateId: stableId));
    var stale = state.Revision;
    state = store.Save(key, state.Revision, 1, TargetLanguage.English, "Manual", true, "human", "manual", "v1");
    Throws<RevisionConflictException>(() => store.Save(key, stale, 1, TargetLanguage.English, "Late", false, "fake", "test", "v1"));
    state = store.Save(key, state.Revision, 1, TargetLanguage.English, "Generated", false, "fake", "test", "v1", true);
    Check(state.Selected(TargetLanguage.English)!.Text == "Manual");
    state = store.Save(key, state.Revision, 1, TargetLanguage.English, "Generated", false, "fake", "test", "v1");
    Check(state.Selected(TargetLanguage.English)!.Text == "Generated");
    state = store.Fail(key, state.Revision, 1, TargetLanguage.English, "rate_limit");
    Check(state.Failures.Length == 1 && state.Selected(TargetLanguage.SimplifiedChinese) is not null);
    File.WriteAllText(nfo, "<movie><plot>新しい物語</plot></movie>");
    state = store.Observe(key, state.Revision, NfoOverview.Read(media));
    Check(state.Source!.Version == 2 && state.Selected(TargetLanguage.English) is null && state.Candidates.Length == 5);
    Throws<SourceChangedException>(() => store.Save(key, state.Revision, 1, TargetLanguage.English, "Stale source", false, "fake", "test", "v1"));
    Check(new OverviewStore(Path.Combine(root, "overview.db")).Read(key).Source!.Version == 2);
    Check(store.Read(new("other-server", "library", "item")).Revision == 0);
    File.WriteAllBytes(nfo, bytes);
    _ = NfoOverview.Read(media); Check(File.ReadAllBytes(nfo).SequenceEqual(bytes));
    File.WriteAllText(nfo, "<!DOCTYPE movie [<!ENTITY x SYSTEM 'file:///not-read'>]><movie><plot>&x;</plot></movie>");
    Check(NfoOverview.Read(media).Error == "overview_nfo_invalid");
    File.WriteAllText(nfo, "<movie><plot>a</plot><plot>b</plot></movie>"); Check(NfoOverview.Read(media).Error == "overview_nfo_invalid");
    File.WriteAllText(nfo, "<movie><plot /></movie>"); Check(NfoOverview.Read(media).Error == "overview_empty");
    var folderNfo = Path.Combine(root, "movie.nfo");
    File.WriteAllText(folderNfo, "<movie><plot>Folder synopsis</plot></movie>");
    Check(NfoOverview.Read(media).Error == "overview_empty"); // Exact empty source must not fall through.
    File.WriteAllText(nfo, "<movie><plot>Exact synopsis</plot></movie>");
    Check(NfoOverview.Read(media).Text == "Exact synopsis");
    File.WriteAllText(nfo, "<broken>"); Check(NfoOverview.Read(media).Error == "overview_nfo_invalid");
    File.Delete(nfo); var fallback = NfoOverview.Read(media);
    Check(fallback.Error is null && fallback.Path == folderNfo && fallback.Text == "Folder synopsis");
    var extra = Path.Combine(root, "example-cd2.MP4"); File.WriteAllText(extra, "");
    Check(NfoOverview.Read(media).Error == "overview_nfo_ambiguous"); // Similar names do not establish ownership.
    Check(NfoOverview.Read(media, [extra]).Text == "Folder synopsis");
    Check(NfoOverview.Read(extra, [media]).Text == "Folder synopsis");
    var third = Path.Combine(root, "example-cd3.mkv"); File.WriteAllText(third, "");
    Check(NfoOverview.Read(media, [extra]).Error == "overview_nfo_ambiguous");
    Check(NfoOverview.Read(media, [extra, third]).Text == "Folder synopsis");
    var unrelated = Path.Combine(root, "other-film.mp4"); File.WriteAllText(unrelated, "");
    Check(NfoOverview.Read(media, [extra, third]).Error == "overview_nfo_ambiguous");
    File.WriteAllText(nfo, "<movie><plot>Exact multipart synopsis</plot></movie>");
    Check(NfoOverview.Read(media, [extra, third]).Text == "Exact multipart synopsis");
    File.WriteAllText(nfo, "<broken>");
    Check(NfoOverview.Read(media, [extra, third]).Error == "overview_nfo_invalid");
    File.Delete(nfo); File.Delete(unrelated); File.Delete(third);
    File.Delete(extra); File.WriteAllText(Path.Combine(root, "poster.jpg"), "");
    Check(NfoOverview.Read(media).Text == "Folder synopsis");
    File.Delete(folderNfo); Check(NfoOverview.Read(media).Error == "overview_nfo_missing");
    Check(NfoOverview.Read(Path.Combine(root, "absent.mkv")).Error == "overview_media_missing");
    File.WriteAllText(nfo, "<movie><plot /></movie>");
    var input = OverviewPipeline.Prepare("太郎の物語。太郎。配送不可。", TargetLanguage.English, ["太郎"]);
    Check(input.Protection.Mapping.Count == 2 && input.Protection.MaskedSource.Contains("配送不可"));
    var output = string.Join("\n", input.Protection.Mapping.Keys) + "\nShipping unavailable.";
    var parsed = OverviewPipeline.Parse(JsonSerializer.Serialize(new { overview = output }), input);
    Check(parsed.Error is null && parsed.Text!.Contains("太郎\n太郎\n"));
    Check(OverviewPipeline.Parse("{\"overview\":\"missing names\"}", input).Error == "invalid_output");
    Check(OverviewPipeline.Parse("{}", input, true).Error == "output_budget");
    Check(OverviewPipeline.Parse("{\"overview\":\"\",\"refusal\":\"Cannot translate\"}", input).Error == "refusal");
    _ = new CandidateStore(Path.Combine(root, "title.db"));
    Throws<InvalidOperationException>(() => new OverviewStore(Path.Combine(root, "title.db")));
    state = store.Save(key, state.Revision, 2, TargetLanguage.English, "Translated synopsis", false, "fake", "test", "v1");
    var fake = new FakeOverviewTarget { Text = "Original display", Hash = state.Source!.Hash };
    var ops = new OverviewOperations(root, store, fake); var applyId = Guid.NewGuid();
    var applied = await ops.ApplyAsync(applyId, key, state.Revision, state.Selected(TargetLanguage.English)!.Id, fake.Text);
    Check(applied.State == "Applied" && fake.Text == "Translated synopsis" && fake.Writes == 1);
    _ = await ops.ApplyAsync(applyId, key, state.Revision, state.Selected(TargetLanguage.English)!.Id, "Original display");
    Check(fake.Writes == 1);
    var restored = await ops.RestoreAsync(Guid.NewGuid(), key, applyId);
    Check(restored.State == "Applied" && fake.Text == "Original display");
    try { await ops.RestoreAsync(Guid.NewGuid(), key, applyId); throw new Exception("Restore reused"); }
    catch (OperationValidationException) { checks++; }
    var conflict = await ops.ApplyAsync(Guid.NewGuid(), key, state.Revision, state.Selected(TargetLanguage.English)!.Id, "Wrong display");
    Check(conflict.State == "Conflict" && fake.Writes == 2);
    fake.Writable = false;
    var blocked = await ops.ApplyAsync(Guid.NewGuid(), key, state.Revision, state.Selected(TargetLanguage.English)!.Id, fake.Text);
    Check(blocked.State == "Blocked" && fake.Writes == 2); fake.Writable = true;
    fake.ThrowAfterWrite = true; var uncertainId = Guid.NewGuid();
    try { await ops.ApplyAsync(uncertainId, key, state.Revision, state.Selected(TargetLanguage.English)!.Id, fake.Text); throw new Exception("No interruption"); }
    catch (IOException) { checks++; }
    Check(ops.Read(uncertainId).State == "Writing");
    var recovered = await new OverviewOperations(root, store, fake).RecoverAsync(uncertainId, key);
    Check(recovered.State == "ObservedApplied" && fake.Writes == 3);
    try { await ops.RestoreAsync(Guid.NewGuid(), key, uncertainId); throw new Exception("Uncertain restore allowed"); }
    catch (OperationValidationException) { checks++; }
    fake.ThrowAfterWrite = false;
    var explicitRestore = await ops.RestoreAsync(Guid.NewGuid(), key, uncertainId, true);
    Check(explicitRestore.State == "Applied" && fake.Text == "Original display");
    var originalTarget = new FakeOverviewTarget { Text = "Translated display", Hash = state.Source!.Hash };
    var originals = new OverviewOperations(Path.Combine(root, "original-operations"), store, originalTarget);
    var originalId = Guid.NewGuid(); var candidatesBefore = JsonSerializer.Serialize(store.Read(key));
    var originalOp = await originals.ApplyOriginalAsync(originalId, key, state.Revision, "Translated display");
    Check(originalOp.State == "Applied" && originalTarget.Text == state.Source.Text && originalOp.CandidateId is null);
    Check(JsonSerializer.Serialize(store.Read(key)) == candidatesBefore);
    Check((await originals.ApplyOriginalAsync(originalId, key, state.Revision, "Translated display")).Id == originalOp.Id && originalTarget.Writes == 1);
    try { await originals.ApplyOriginalAsync(originalId, key, state.Revision, "different"); throw new Exception("Expected conflict"); }
    catch (IdempotencyConflictException) { checks++; }
    Check((await originals.RestoreAsync(Guid.NewGuid(), key, originalId)).State == "Applied" && originalTarget.Text == "Translated display");
    Check((await originals.ApplyOriginalAsync(Guid.NewGuid(), key, state.Revision, "wrong display")).State == "Conflict");
    originalTarget.Hash = "changed";
    Check((await originals.ApplyOriginalAsync(Guid.NewGuid(), key, state.Revision, originalTarget.Text)).State == "Conflict");
    originalTarget.Hash = state.Source.Hash; originalTarget.ThrowAfterWrite = true;
    var lostOriginal = Guid.NewGuid();
    try { await originals.ApplyOriginalAsync(lostOriginal, key, state.Revision, originalTarget.Text); throw new Exception("Expected response loss"); }
    catch (IOException) { checks++; }
    Check(originals.Read(lostOriginal).State == "Writing");
    var writeCount = originalTarget.Writes;
    Check((await originals.RecoverAsync(lostOriginal, key)).State == "ObservedApplied" && originalTarget.Writes == writeCount);
    Check(JsonSerializer.Serialize(store.Read(key)) == candidatesBefore);
    var guardPreferences = new DisplayPreferenceStore(Path.Combine(root, "guard-prefs.db"));
    guardPreferences.Set(key, DisplayField.Overview, 0, DisplayPreferenceMode.Original);
    var guardedTarget = new FakeOverviewTarget { Text = "Prior display", Hash = state.Source.Hash };
    var guard = new Localizer.Plugin.PreferenceOverviewTarget(guardedTarget, guardPreferences, store);
    Check(await guard.WriteAsync(new(key, guardedTarget.Text, "Translation", state.Source.Hash), default) == TargetWriteResult.Blocked && guardedTarget.Writes == 0);
    guardedTarget.OnWrite = () => Throws<OperationBusyException>(() => guardPreferences.Set(key, DisplayField.Overview, 1, DisplayPreferenceMode.FollowLibrary));
    Check(await guard.WriteAsync(new(key, guardedTarget.Text, state.Source.Text, state.Source.Hash), default) == TargetWriteResult.Written);
    guardedTarget.OnWrite = null;
    guardPreferences.Set(key, DisplayField.Overview, 1, DisplayPreferenceMode.FollowLibrary);
    Check(await guard.WriteAsync(new(key, guardedTarget.Text, "Translation", state.Source.Hash), default) == TargetWriteResult.Written);
    guardPreferences.Set(key, DisplayField.Name, 0, DisplayPreferenceMode.Original);
    var guardedName = new GuardNameTarget();
    var nameGuard = new Localizer.Plugin.PreferenceNameTarget(guardedName, guardPreferences);
    Check(await nameGuard.WriteAsync(new(key,"prior","translation","original","ID · "),default)==TargetWriteResult.Blocked && guardedName.Writes==0);
    guardedName.OnWrite=()=>Throws<OperationBusyException>(()=>guardPreferences.Set(key,DisplayField.Name,1,DisplayPreferenceMode.FollowLibrary));
    Check(await nameGuard.WriteAsync(new(key,"prior","ID · original","original","ID · "),default)==TargetWriteResult.Written);
    guardedName.OnWrite=null;guardPreferences.Set(key,DisplayField.Name,1,DisplayPreferenceMode.FollowLibrary);
    Check(await nameGuard.WriteAsync(new(key,"prior","translation","original","ID · "),default)==TargetWriteResult.Written);
    var unprotected = OverviewPipeline.Prepare("物語。配送不可。", TargetLanguage.English, []);
    var handler = new FakeOverviewHttp(System.Net.HttpStatusCode.OK,
        "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{\\\"overview\\\":\\\"Story. Shipping unavailable.\\\"}\"}}]}");
    var translated = await new OverviewProvider(() => "test-key", handler).TranslateAsync((TranslationServiceProfile.GroqDefault() with { Model = "synthetic-model" }), unprotected, default);
    Check(translated.Text == "Story. Shipping unavailable." && handler.Request!.Contains("max_completion_tokens\":3200")
        && handler.Request.Contains("reasoning_effort\":\"none\""));
    var limited = await new OverviewProvider(() => "test-key", new FakeOverviewHttp(System.Net.HttpStatusCode.TooManyRequests, ""))
        .TranslateAsync((TranslationServiceProfile.GroqDefault() with { Model = "synthetic-model" }), unprotected, default);
    Check(limited.Error == "rate_limit");
    var native = new FakeOverviewHttp(System.Net.HttpStatusCode.OK,
        "{\"output\":[{\"type\":\"message\",\"content\":\"{\\\"overview\\\":\\\"Story\\\"}\"}],\"stats\":{\"total_output_tokens\":20}}");
    var local = await new OverviewProvider(() => throw new Exception("Local read cloud credential"), native)
        .TranslateAsync(new("local", "lmstudio", "http://127.0.0.1:1234/api/v1", "test"), unprotected, default);
    Check(local.Text == "Story" && native.Request!.Contains("reasoning\":\"off\""));
    Console.WriteLine($"PASS: {checks} overview source, history, conflicts, language isolation and parser checks.");
}
finally { Directory.Delete(root, recursive: true); }

sealed class GuardNameTarget : INameTarget
{
    public int Writes; public Action? OnWrite;
    public ValueTask<NameTargetSnapshot?> ReadAsync(MediaKey key,CancellationToken token)=>ValueTask.FromResult<NameTargetSnapshot?>(null);
    public ValueTask<TargetWriteResult> WriteAsync(NameWriteRequest request,CancellationToken token)
    {OnWrite?.Invoke();Writes++;return ValueTask.FromResult(TargetWriteResult.Written);}
}
sealed class FakeOverviewTarget : IOverviewTarget
{
    public string Text = ""; public string Hash = ""; public bool Writable = true; public bool ThrowAfterWrite; public int Writes; public Action? OnWrite;
    public ValueTask<OverviewTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token) => ValueTask.FromResult<OverviewTargetSnapshot?>(new(Text, Hash, Writable));
    public ValueTask<TargetWriteResult> WriteAsync(OverviewWrite request, CancellationToken token)
    {
        if (!Writable) return ValueTask.FromResult(TargetWriteResult.Blocked);
        if (request.Expected != Text || request.SourceHash != Hash) return ValueTask.FromResult(TargetWriteResult.Conflict);
        OnWrite?.Invoke(); Text = request.Proposed; Writes++;
        if (ThrowAfterWrite) throw new IOException("Simulated response loss");
        return ValueTask.FromResult(TargetWriteResult.Written);
    }
}
sealed class FakeOverviewHttp(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
{
    public string? Request;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Request = await request.Content!.ReadAsStringAsync(token); return new(status) { Content = new StringContent(body) }; }
}
