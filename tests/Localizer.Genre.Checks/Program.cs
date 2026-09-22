using System.Text.Json;
using Localizer.Core;
using Microsoft.Data.Sqlite;

var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
var results = new List<object>(); var failed = 0;
var key = new MediaKey("server", "library", "movie"); var scope = new LibraryKey(key.ServerId, key.LibraryId);
var zh = TargetLanguage.SimplifiedChinese; var en = TargetLanguage.English;
(CandidateStore Store, FakeGenreTarget Target, GenrePreviewEntry Preview, string Database) Fresh(string[]? source = null)
{
    var db = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");
    var store = new CandidateStore(db); var values = source ?? ["ドラマ", "アクション", "未知", "ドラマ"];
    store.ObserveGenreSource(key, values);
    var target = new FakeGenreTarget(new(key, true, false, true, values.ToArray()));
    return (store, target, store.BuildGenrePreview(scope, zh, target.Current), db);
}
void Expect(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
async Task Throws<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
async Task Check(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; results.Add(new { name, passed = false, error = error.GetType().Name }); Console.WriteLine("FAIL " + name + ": " + error); }
}
Action<OperationCheckpoint, GenreOperation> Crash(OperationCheckpoint when) => (point, _) => { if (point == when) throw new IOException("injected interruption"); };

await Check("exact_sets_ignore_only_order_and_duplicates", () =>
{
    Expect(GenreValues.Equal(["b", "a", "a"], ["a", "b"]));
    Expect(!GenreValues.Equal(["A"], ["a"]) && !GenreValues.Equal(["é"], ["e\u0301"]) && !GenreValues.Equal(["a "], ["a"]));
    return Task.CompletedTask;
});
await Check("source_reordering_reuses_version_and_original_representation", () =>
{
    var f = Fresh(); var before = f.Store.GetCurrentGenreSource(key)!;
    var after = f.Store.ObserveGenreSource(key, ["未知", "アクション", "ドラマ"]);
    Expect(before.Id == after.Id && after.OriginalValues.SequenceEqual(before.OriginalValues)); return Task.CompletedTask;
});
await Check("unknowns_preserved_and_languages_independent", () =>
{
    var f = Fresh(); var english = f.Store.GetGenreMapping(key, en);
    Expect(f.Preview.UnknownValues.SequenceEqual(["未知"]) && f.Preview.ProposedValues.Contains("未知"));
    Expect(english.Values.Contains("Drama") && f.Preview.ProposedValues.Contains("剧情") && english.Id != f.Preview.MappingId);
    return Task.CompletedTask;
});
await Check("override_revisions_survive_reopen_and_reject_stale_editor", async () =>
{
    var f = Fresh(); f.Store.SetGenreOverride(zh, "未知", 0, "自定义");
    var store = new CandidateStore(f.Database); Expect(store.GetGenreMapping(key, zh).UnknownValues.Length == 0);
    await Throws<RevisionConflictException>(() => { store.SetGenreOverride(zh, "未知", 0, "旧页面"); return Task.CompletedTask; });
    var cleared = store.SetGenreOverride(zh, "未知", 1, null); Expect(cleared.Revision == 2 && store.GetGenreMapping(key, zh).UnknownValues.Length == 1);
});
await Check("unrelated_dictionary_edits_do_not_invalidate_mapping", () =>
{
    var f = Fresh(); f.Store.SetGenreOverride(zh, "未使用", 0, "unused"); f.Store.SetGenreOverride(en, "ドラマ", 0, "Series");
    Expect(f.Store.GetGenreMapping(key, zh).Id == f.Preview.MappingId); return Task.CompletedTask;
});
await Check("only_exact_duplicate_outputs_are_removed", () =>
{
    var f = Fresh(["one", "two", "three", "four"]);
    f.Store.SetGenreOverride(zh, "one", 0, "类别"); f.Store.SetGenreOverride(zh, "two", 0, "类别");
    f.Store.SetGenreOverride(zh, "three", 0, "category"); f.Store.SetGenreOverride(zh, "four", 0, "Category");
    Expect(f.Store.GetGenreMapping(key, zh).Values.Length == 3); return Task.CompletedTask;
});
await Check("dictionary_change_invalidates_frozen_preview", async () =>
{
    var f = Fresh(); f.Store.SetGenreOverride(zh, "ドラマ", 0, "剧情片");
    await Throws<OperationValidationException>(() => new GenreOperationService(f.Store, f.Target).ApplyAsync("a", scope, f.Preview)); Expect(f.Target.Writes == 0);
});
await Check("apply_restore_preserves_raw_order_and_duplicates", async () =>
{
    var f = Fresh(); var original = f.Target.Current.Values.ToArray(); var service = new GenreOperationService(f.Store, f.Target);
    Expect((await service.ApplyAsync("a", scope, f.Preview)).State == OperationState.Applied);
    Expect((await service.RestoreAsync("r", scope, "a")).State == OperationState.Applied && f.Target.Current.Values.SequenceEqual(original));
});
await Check("idempotency_and_nochange_preserve_restore_point", async () =>
{
    var f = Fresh(); var service = new GenreOperationService(f.Store, f.Target);
    await service.ApplyAsync("a", scope, f.Preview); await service.ApplyAsync("a", scope, f.Preview);
    Expect((await service.ApplyAsync("b", scope, f.Store.BuildGenrePreview(scope, zh, f.Target.Current))).State == OperationState.NoChange);
    Expect(f.Target.Writes == 1 && f.Store.GetGenreRestorePoint(key)!.Application.Id == "a");
    await Throws<IdempotencyConflictException>(() => service.ApplyAsync("a", scope, f.Preview with { ExpectedValues = ["tampered"] }));
});
await Check("permutation_after_preview_is_not_conflict_and_restores_actual_before", async () =>
{
    var f = Fresh(); f.Target.Current = f.Target.Current with { Values = ["未知", "ドラマ", "アクション"] };
    var before = f.Target.Current.Values.ToArray(); var service = new GenreOperationService(f.Store, f.Target);
    await service.ApplyAsync("a", scope, f.Preview); await service.RestoreAsync("r", scope, "a");
    Expect(f.Target.Current.Values.SequenceEqual(before));
});
await Check("external_value_change_conflicts", async () =>
{
    var f = Fresh(); f.Target.Current = f.Target.Current with { Values = ["manual"] };
    Expect((await new GenreOperationService(f.Store, f.Target).ApplyAsync("a", scope, f.Preview)).State == OperationState.Conflict && f.Target.Writes == 0);
});
await Check("genre_source_change_does_not_reuse_old_preview", async () =>
{
    var f = Fresh(); f.Store.ObserveGenreSource(key, ["new source"]);
    await Throws<OperationValidationException>(() => new GenreOperationService(f.Store, f.Target).ApplyAsync("a", scope, f.Preview));
});
await Check("tampered_output_and_unknown_list_rejected", async () =>
{
    var f = Fresh(); var service = new GenreOperationService(f.Store, f.Target);
    await Throws<OperationValidationException>(() => service.ApplyAsync("a", scope, f.Preview with { ProposedValues = ["tampered"] }));
    await Throws<OperationValidationException>(() => service.ApplyAsync("b", scope, f.Preview with { UnknownValues = [] }));
});
await Check("lock_saver_type_and_scope_guards", async () =>
{
    foreach (var mode in new[] { "lock", "saver", "type", "scope" })
    {
        var f = Fresh(); f.Target.Current = mode switch { "lock" => f.Target.Current with { Locked = true },
            "saver" => f.Target.Current with { MetadataSaversExplicitlyDisabled = false }, "type" => f.Target.Current with { IsMovie = false },
            _ => f.Target.Current with { Key = key with { LibraryId = "other" } } };
        Expect((await new GenreOperationService(f.Store, f.Target).ApplyAsync("a", scope, f.Preview)).State == OperationState.Blocked && f.Target.Writes == 0);
    }
});
await Check("late_adapter_guard_prevents_write", async () =>
{
    var f = Fresh(); f.Target.BeforeWrite = () => f.Target.Current = f.Target.Current with { Locked = true };
    Expect((await new GenreOperationService(f.Store, f.Target).ApplyAsync("a", scope, f.Preview)).State == OperationState.Blocked && f.Target.Writes == 0);
});
await Check("intent_and_writing_interruptions_require_explicit_resume", async () =>
{
    foreach (var point in new[] { OperationCheckpoint.IntentSaved, OperationCheckpoint.WriteMarked })
    {
        var f = Fresh(); await Throws<IOException>(() => new GenreOperationService(f.Store, f.Target, Crash(point)).ApplyAsync("a", scope, f.Preview));
        var service = new GenreOperationService(new CandidateStore(f.Database), f.Target);
        Expect((await service.ReconcileAsync("a")).State == OperationState.Retryable && f.Target.Writes == 0);
        Expect((await service.ResumeAsync("a", scope)).State == OperationState.Applied && f.Target.Writes == 1);
    }
});
await Check("written_and_readback_interruptions_reconcile_without_rewrite", async () =>
{
    foreach (var point in new[] { OperationCheckpoint.TargetReturned, OperationCheckpoint.BeforeCompletion })
    {
        var f = Fresh(); await Throws<IOException>(() => new GenreOperationService(f.Store, f.Target, Crash(point)).ApplyAsync("a", scope, f.Preview));
        var service = new GenreOperationService(new CandidateStore(f.Database), f.Target);
        Expect((await service.ReconcileAsync("a")).State == OperationState.ObservedExpected && f.Target.Writes == 1);
        await Throws<OperationValidationException>(() => service.RestoreAsync("r", scope, "a"));
        Expect((await service.RestoreAsync("r", scope, "a", true)).State == OperationState.Applied);
    }
});
await Check("write_failures_before_after_third_and_no_effect_are_distinct", async () =>
{
    foreach (var (mode, expected) in new[] { ("throw-before", OperationState.Retryable), ("throw-after", OperationState.ObservedExpected),
        ("third", OperationState.Conflict), ("no-effect", OperationState.Retryable), ("unreadable", OperationState.Writing) })
    {
        var f = Fresh(); f.Target.Mode = mode;
        Expect((await new GenreOperationService(f.Store, f.Target).ApplyAsync("a", scope, f.Preview)).State == expected);
    }
});
await Check("pending_genres_freeze_related_source_and_dictionary_only", async () =>
{
    var f = Fresh(); await Throws<IOException>(() => new GenreOperationService(f.Store, f.Target, Crash(OperationCheckpoint.IntentSaved)).ApplyAsync("a", scope, f.Preview));
    await Throws<OperationBusyException>(() => { f.Store.ObserveGenreSource(key, ["new"]); return Task.CompletedTask; });
    await Throws<OperationBusyException>(() => { f.Store.SetGenreOverride(zh, "ドラマ", 0, "new"); return Task.CompletedTask; });
    f.Store.SetGenreOverride(zh, "unrelated", 0, "allowed");
    var title = f.Store.ObserveSource(key, "title source", "prefix "); Expect(title.OriginalTitle == "title source");
});
await Check("title_and_genre_sources_and_restore_points_are_independent", async () =>
{
    var f = Fresh(); var title = f.Store.ObserveSource(key, "title source", "");
    var c = f.Store.SaveGenerated(title.Id, zh, "human candidate", new("synthetic", "fixture", "1", "1", "{}", "{}", "test"));
    f.Store.Approve(c.Id, c.Revision); await new GenreOperationService(f.Store, f.Target).ApplyAsync("a", scope, f.Preview);
    f.Store.SetGenreOverride(zh, "ドラマ", 0, "剧情片");
    Expect(f.Store.GetSelected(title.Id, zh)!.Candidate.Text == "human candidate" && f.Store.GetRestorePoint(key) is null && f.Store.GetGenreRestorePoint(key) is not null);
});
await Check("user_edit_after_apply_prevents_restore", async () =>
{
    var f = Fresh(); var service = new GenreOperationService(f.Store, f.Target); await service.ApplyAsync("a", scope, f.Preview);
    f.Target.Current = f.Target.Current with { Values = ["user edit"] };
    Expect((await service.RestoreAsync("r", scope, "a")).State == OperationState.Conflict && f.Target.Writes == 1);
});
await Check("english_switch_restores_chinese_and_consumes_point", async () =>
{
    var f = Fresh(); var service = new GenreOperationService(f.Store, f.Target); await service.ApplyAsync("zh", scope, f.Preview);
    var chinese = f.Target.Current.Values.ToArray(); await service.ApplyAsync("en", scope, f.Store.BuildGenrePreview(scope, en, f.Target.Current));
    await Throws<OperationValidationException>(() => service.RestoreAsync("old", scope, "zh"));
    await service.RestoreAsync("r", scope, "en"); var count = f.Target.Writes;
    Expect((await service.RestoreAsync("again", scope, "en")).State == OperationState.NoChange && f.Target.Writes == count && GenreValues.Equal(f.Target.Current.Values, chinese));
});
await Check("restore_interruption_and_cancel_do_not_hide_effect", async () =>
{
    var f = Fresh(); await new GenreOperationService(f.Store, f.Target).ApplyAsync("a", scope, f.Preview);
    await Throws<IOException>(() => new GenreOperationService(f.Store, f.Target, Crash(OperationCheckpoint.TargetReturned)).RestoreAsync("r", scope, "a"));
    Expect((await new GenreOperationService(f.Store, f.Target).CancelPendingAsync("r")).State == OperationState.ObservedExpected && f.Target.Writes == 2);
});
await Check("cancel_before_write_releases_pending_inputs", async () =>
{
    var f = Fresh(); await Throws<IOException>(() => new GenreOperationService(f.Store, f.Target, Crash(OperationCheckpoint.IntentSaved)).ApplyAsync("a", scope, f.Preview));
    Expect((await new GenreOperationService(f.Store, f.Target).CancelPendingAsync("a")).State == OperationState.Cancelled);
    f.Store.SetGenreOverride(zh, "ドラマ", 0, "剧情片"); Expect(f.Target.Writes == 0);
});
await Check("restore_and_resume_require_current_scope", async () =>
{
    var f = Fresh(); var wrong = new LibraryKey("server", "other");
    await Throws<IOException>(() => new GenreOperationService(f.Store, f.Target, Crash(OperationCheckpoint.IntentSaved)).ApplyAsync("a", scope, f.Preview));
    var service = new GenreOperationService(f.Store, f.Target);
    await Throws<OperationValidationException>(() => service.ResumeAsync("a", wrong));
    await service.ResumeAsync("a", scope); await Throws<OperationValidationException>(() => service.RestoreAsync("r", wrong, "a"));
});
await Check("empty_source_is_valid_but_blank_members_are_rejected", async () =>
{
    var f = Fresh([]); Expect(f.Preview.ProposedValues.Length == 0 && f.Preview.Status == PreviewStatus.AlreadyMatches);
    await Throws<ArgumentException>(() => { f.Store.ObserveGenreSource(key, [" "]); return Task.CompletedTask; });
});
await Check("unresolved_field_must_be_reconciled_before_other_field_write", async () =>
{
    var f = Fresh(); var source = f.Store.ObserveSource(key, "original", "");
    var c = f.Store.SaveGenerated(source.Id, zh, "translated", new("fixture", "test", "1", "1", "{}", "{}", "prompt")); f.Store.Approve(c.Id, c.Revision);
    var movie = new ObservedMovie(key, true, false, "original", "", "original");
    var nameTarget = new ReadOnlyNameFixture(new(movie, true)); var namePreview = new LanguagePreview(f.Store).Build(scope, zh, [movie]).Single();
    await Throws<IOException>(() => new GenreOperationService(f.Store, f.Target, Crash(OperationCheckpoint.IntentSaved)).ApplyAsync("g", scope, f.Preview));
    await Throws<OperationBusyException>(() => new NameOperationService(f.Store, nameTarget).ApplyAsync("n", scope, namePreview));
    await new GenreOperationService(f.Store, f.Target).CancelPendingAsync("g");
    await Throws<IOException>(() => new NameOperationService(f.Store, nameTarget, (point, _) => { if (point == OperationCheckpoint.IntentSaved) throw new IOException(); }).ApplyAsync("n", scope, namePreview));
    await Throws<OperationBusyException>(() => new GenreOperationService(f.Store, f.Target).ApplyAsync("g2", scope, f.Preview));
    Expect(f.Store.FindGenreOperation("g2") is null && f.Target.Writes == 0);
});
await Check("schema_two_migration_preserves_title_candidate", () =>
{
    var f = Fresh(); var source = f.Store.ObserveSource(key, "original", "");
    var c = f.Store.SaveGenerated(source.Id, zh, "saved", new("test", "fixture", "1", "1", "{}", "{}", "prompt"));
    using (var db = new SqliteConnection("Data Source=" + f.Database))
    {
        db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE genre_save_receipts; DROP TABLE translation_items; DROP TABLE translation_jobs; DROP TABLE genre_restore_heads; DROP TABLE genre_operations; DROP TABLE genre_mappings; DROP TABLE genre_overrides; DROP TABLE genre_source_heads; DROP TABLE genre_sources; PRAGMA user_version=2;"; cmd.ExecuteNonQuery();
    }
    var reopened = new CandidateStore(f.Database); Expect(reopened.GetCandidate(c.Id).Text == "saved" && reopened.GetCurrentGenreSource(key) is null);
    return Task.CompletedTask;
});

await Check("catalog_sources_use_current_heads_and_server_scope_after_reopen", () =>
{
    var f = Fresh(["old"]);
    var latest = f.Store.ObserveGenreSource(key, ["new", "new"]);
    var second = f.Store.ObserveGenreSource(new(key.ServerId, "other-library", "other-item"), []);
    f.Store.ObserveGenreSource(new("other-server", key.LibraryId, key.ItemId), ["not-in-scope"]);
    var found = new CandidateStore(f.Database).ListCurrentGenreSources(key.ServerId);
    Expect(found.Count == 2 && found.Any(x => x.Id == latest.Id && x.OriginalValues.SequenceEqual(new[] {"new", "new"}))
        && found.Any(x => x.Id == second.Id && x.OriginalValues.Length == 0)
        && found.All(x => x.Key.ServerId == key.ServerId && !x.OriginalValues.Contains("old")));
    return Task.CompletedTask;
});

await Check("expanded_dictionary_keeps_overrides_and_bilingual_switching", async () =>
{
    var f = Fresh(["アドベンチャー", "短編", "アニメーション"]);
    Expect(f.Preview.UnknownValues.Length == 0 && f.Preview.ProposedValues.Contains("冒险"));
    Expect(f.Store.GetGenreMapping(key,en).Values.Contains("Adventure"));
    f.Store.SetGenreOverride(zh,"短編",0,"自定义短片");
    Expect(f.Store.GetGenreMapping(key,zh).Values.Contains("自定义短片") && f.Store.GetGenreMapping(key,en).Values.Contains("Short"));
    var service = new GenreOperationService(f.Store,f.Target);
    await service.ApplyAsync("zh",scope,f.Store.BuildGenrePreview(scope,zh,f.Target.Current));
    await service.ApplyAsync("en",scope,f.Store.BuildGenrePreview(scope,en,f.Target.Current));
    Expect(f.Target.Current.Values.Contains("Adventure") && !f.Target.Current.Values.Contains("冒险"));
    await service.RestoreAsync("restore-en",scope,"en");
    Expect(f.Target.Current.Values.Contains("自定义短片"));
});
await Check("discovery_suppresses_own_translations_but_detects_new_values", () =>
{
    var f = Fresh(["ドラマ"]);
    string? Lookup(string term,TargetLanguage language) => f.Store.GetGenreDictionaryEntry(language,term).Effective;
    Expect(GenreDiscovery.ObservedTerms(["Drama","新词","新词"],["ドラマ"],Lookup).SequenceEqual(new[]{"ドラマ","新词"}));
    Expect(GenreDiscovery.ObservedTerms(["剧情"],["ドラマ"],Lookup).SequenceEqual(new[]{"ドラマ"}));
    Expect(GenreDiscovery.ObservedTerms(["Drama"],null,Lookup).SequenceEqual(new[]{"Drama"}));
    Expect(GenreDiscovery.ObservedTerms(["Action"],["ドラマ"],Lookup).Contains("Action"));
    Expect(f.Store.GetCurrentGenreSource(key)!.OriginalValues.SequenceEqual(new[]{"ドラマ"}));
    return Task.CompletedTask;
});
await Check("embedded_dictionary_has_complete_unique_bilingual_entries", () =>
{
    using var stream = typeof(GenreValues).Assembly.GetManifestResourceStream("Localizer.Genres.tsv")!;
    using var reader = new StreamReader(stream); reader.ReadLine();
    var terms = new List<string>(); var f = Fresh();
    while(reader.ReadLine() is {} line)
    {
        var fields = line.Split('\t'); terms.Add(fields[0]);
        Expect(fields.Length == 3 && fields.All(x => !string.IsNullOrWhiteSpace(x)));
        Expect(f.Store.GetGenreDictionaryEntry(zh,fields[0]).Effective == fields[1]);
        Expect(f.Store.GetGenreDictionaryEntry(en,fields[0]).Effective == fields[2]);
    }
    Expect(terms.Count >= 30 && terms.Distinct(StringComparer.Ordinal).Count() == terms.Count);
    Expect(f.Store.GetGenreDictionaryEntry(en,"短編 ").Unknown);
    return Task.CompletedTask;
});

await Check("nfo_reader_matches_exact_file_and_keeps_exact_genre_values", () =>
{
    var directory=Path.Combine(root,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var media=Path.Combine(directory,"movie-a.mp4");File.WriteAllText(media,"");
    var nfo=Path.ChangeExtension(media,".nfo");File.WriteAllText(nfo,"<movie><genre>短編</genre><genre>短編</genre><genre>A &amp; B</genre></movie>");
    var read=NfoGenres.Read(media);Expect(read.Error is null && read.Path==nfo && read.Values.SequenceEqual(new[]{"短編","短編","A & B"}));
    File.WriteAllText(nfo,"<movie><genre>旅行</genre></movie>");Expect(NfoGenres.Read(media).Fingerprint!=read.Fingerprint);
    return Task.CompletedTask;
});
await Check("nfo_reader_rejects_ambiguity_empty_invalid_and_dtd", () =>
{
    var directory=Path.Combine(root,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var media=Path.Combine(directory,"a.mp4");File.WriteAllText(media,"");
    var exact=Path.ChangeExtension(media,".nfo");var folder=Path.Combine(directory,"movie.nfo");
    Expect(NfoGenres.Read(media).Error=="genre_nfo_missing");
    File.WriteAllText(folder,"<movie><genre>短編</genre></movie>");Expect(NfoGenres.Read(media).Error is null);
    File.WriteAllText(Path.Combine(directory,"b.mkv"),"");Expect(NfoGenres.Read(media).Error=="genre_nfo_ambiguous");
    Expect(NfoGenres.Read(media,[Path.Combine(directory,"b.mkv")]).Error is null);
    Expect(NfoGenres.Read(media,[Path.Combine(directory,"not-b.mkv")]).Error=="genre_nfo_ambiguous");
    File.WriteAllText(exact,"<movie><genre>旅行</genre></movie>");Expect(NfoGenres.Read(media).Error=="genre_nfo_ambiguous");
    File.Delete(folder);
    foreach(var xml in new[]{"<movie>","<episode><genre>A</genre></episode>","<movie><genre> </genre></movie>","<movie><genre><x>nested</x></genre></movie>","<!DOCTYPE movie [<!ENTITY value SYSTEM 'file:///should-not-read'>]><movie><genre>&value;</genre></movie>"})
    {File.WriteAllText(exact,xml);Expect(NfoGenres.Read(media).Error=="genre_nfo_invalid");}
    File.WriteAllText(exact,"<movie/>");Expect(NfoGenres.Read(media).Error=="genre_nfo_empty");
    File.WriteAllBytes(exact,new byte[4*1024*1024+1]);Expect(NfoGenres.Read(media).Error=="genre_nfo_too_large");
    return Task.CompletedTask;
});
await Check("nfo_updates_accept_known_display_and_preserve_manual_conflicts", () =>
{
    Expect(NfoGenres.DisplayCanBeUpdated(["Short"],["旅行"],["短編"],["Short"]));
    Expect(NfoGenres.DisplayCanBeUpdated(["旅行"],["旅行"],["短編"],null));
    Expect(!NfoGenres.DisplayCanBeUpdated(["manual"],["旅行"],["短編"],["Short"]));
    Expect(!NfoGenres.DisplayCanBeUpdated(["Short"],["短編"],null,null));
    return Task.CompletedTask;
});

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(new { stage = "M3-D Genre mapping and journal", total = results.Count,
    passed = results.Count - failed, failed, modelRequests = 0, productionJellyfinWrites = 0, runtime = Environment.Version.ToString(), results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count-failed}/{results.Count} Genre checks passed.");
return failed == 0 ? 0 : 1;
