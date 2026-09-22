using System.Text.Json;
using Localizer.Core;
using Localizer.Translation;

internal static class PersonNameChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> check, string root)
    {
        static void Expect(bool condition) { if (!condition) throw new InvalidOperationException("Person name assertion failed"); }
        static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        var rules = new TitleRules("names-test", "", []);
        var person = new PersonNameEntry(Guid.NewGuid().ToString("N"), "山田太郎", ["太郎"], "山田太郎甲", "Taro Yamada");
        var names = new PersonNameSnapshot(1, [person]);
        var source = new SourceSnapshot(1, new("server", "library", "movie"), 1, "山田太郎と太郎の旅", "CAT ", "hash");
        static string Response(string model, string title) => JsonSerializer.Serialize(new { model,
            choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { title }) } } } });
        await check("person_registry_discovery_is_stable_and_aliases_are_not_duplicate_people", () =>
        {
            var store = new PersonNameStore(Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"));
            var one = store.Discover(["山田太郎", "山田太郎"]); Expect(one.Entries.Length == 1);
            var saved = store.Save(one.Revision, one.Entries[0] with { Aliases = ["太郎"], EnglishName = "Taro Yamada" });
            var again = store.Discover(["太郎", "山田太郎"]); Expect(again.Revision == saved.Revision && again.Entries[0].Id == one.Entries[0].Id);
            Throws<RevisionConflictException>(() => store.Save(one.Revision, saved.Entries[0]));
            Throws<ArgumentException>(() => store.Save(saved.Revision, new(Guid.NewGuid().ToString("N"), "別人", ["太郎"])));
            Expect(store.Read().Revision == saved.Revision); return Task.CompletedTask;
        });
        await check("person_snapshot_freezes_display_for_both_backends_and_languages", () =>
        {
            foreach (var language in new[] { TargetLanguage.SimplifiedChinese, TargetLanguage.English })
            foreach (var service in new[] { (TranslationServiceProfile.GroqDefault() with { Model = "synthetic-model" }), new TranslationServiceProfile("local", "lmstudio", "http://127.0.0.1:1234/api/v1", "local-model") })
            {
                var spec = service.Apply(TitlePipeline.Prepare(source, language, ["山田太郎"], rules, names));
                var changed = names with { Revision = 2, Entries = [person with { ChineseName = "另一个显示名", EnglishName = "Other Name" }] };
                var result = TitlePipeline.Parse(Response(spec.Model, "__JML_PERSON_A__ 与 __JML_PERSON_B__ adventure"), new(source, language, spec));
                Expect(result.ErrorCategory is null && result.NameTemplate is not null);
                Expect(result.Title == person.Display(language) + " 与 " + person.Display(language) + " adventure");
                Expect(TitlePipeline.RenderNames(result.NameTemplate!, TitlePipeline.ValidateContext(new(source, language, spec)), language, changed)
                    == changed.Entries[0].Display(language) + " 与 " + changed.Entries[0].Display(language) + " adventure");
            }
            return Task.CompletedTask;
        });
        await check("person_alias_match_is_longest_first_and_limited_to_linked_people", () =>
        {
            var other = new PersonNameEntry(Guid.NewGuid().ToString("N"), "別人", ["旅"], "替换", "Other");
            var spec = TitlePipeline.Prepare(source, TargetLanguage.English, ["山田太郎"], rules, new(2, [person, other]));
            var context = TitlePipeline.ValidateContext(new(source, TargetLanguage.English, spec));
            Expect(context.MaskedSource == "__JML_PERSON_A__と__JML_PERSON_B__の旅" && context.Mapping.Count == 2);
            return Task.CompletedTask;
        });
        await check("person_names_reject_reserved_tokens_duplicates_and_unknown_placeholders", () =>
        {
            Throws<ArgumentException>(() => new PersonNameSnapshot(1, [person with { EnglishName = "__JML_PERSON_B__" }]).Validate());
            Throws<ArgumentException>(() => new PersonNameSnapshot(1, [person with { Aliases = ["太郎", "太郎"] }]).Validate());
            var spec = TitlePipeline.Prepare(source, TargetLanguage.English, ["山田太郎"], rules, names);
            Expect(TitlePipeline.Parse(Response(spec.Model, "__JML_PERSON_A__ and __JML_PERSON_B__ plus __JML_PERSON_C__"), new(source, TargetLanguage.English, spec)).ErrorCategory == "PlaceholderMismatch");
            return Task.CompletedTask;
        });
        await check("normal_english_negation_is_not_refusal_but_actual_refusal_is", () =>
        {
            var plain = source with { OriginalTitle = "自由な旅" };
            var spec = TitlePipeline.Prepare(plain, TargetLanguage.English, [], rules, PersonNameSnapshot.Empty);
            foreach (var text in new[] { "I can't help but travel", "I cannot resist this journey", "I can't wait to travel" })
                Expect(TitlePipeline.Parse(Response(spec.Model, text), new(plain, TargetLanguage.English, spec)).ErrorCategory is null);
            Expect(TitlePipeline.Parse(Response(spec.Model, "I cannot translate this content"), new(plain, TargetLanguage.English, spec)).ErrorCategory == "Refused");
            Expect(TitlePipeline.Parse(Response(spec.Model, "I can't help with this request"), new(plain, TargetLanguage.English, spec)).ErrorCategory == "Refused");
            return Task.CompletedTask;
        });
        await check("legacy_name_template_recovery_preserves_reordered_people_without_guessing", () =>
        {
            var legacySource = source with { OriginalTitle = "山田太郎と鈴木花子の旅" };
            var spec = TitlePipeline.Prepare(legacySource, TargetLanguage.English, ["山田太郎", "鈴木花子"], rules);
            var context = TitlePipeline.ValidateContext(new(legacySource, TargetLanguage.English, spec));
            var text = "鈴木花子 travels with 山田太郎";
            var template = PersonNameRendering.RecoverTemplate(text, context);
            Expect(TitlePipeline.RenderNames(template, context, TargetLanguage.English) == text);
            Throws<ArgumentException>(() => PersonNameRendering.RecoverTemplate("A guessed actor name travels", context));
            return Task.CompletedTask;
        });
        await check("refresh_creates_separate_candidate_and_recovers_without_second_derivation", () =>
        {
            var store = new CandidateStore(Path.Combine(root, Guid.NewGuid().ToString("N") + ".db"));
            var original = store.ObserveSource(source.Key, source.OriginalTitle, source.DisplayPrefix);
            var spec = TitlePipeline.Prepare(original, TargetLanguage.English, ["山田太郎", "太郎"], rules);
            var old = store.SaveGenerated(original.Id, TargetLanguage.English, "山田太郎 and 太郎 travel", spec);
            old = store.Approve(old.Id, old.Revision);
            var refresh = PersonNameRendering.Refresh(original, old, names, null, Guid.NewGuid());
            Expect(refresh.Text == "Taro Yamada and Taro Yamada travel");
            var intent = new PersonNameRefresh(old.Id, old.Revision, 1, refresh.Text);
            var derived = store.DeriveNamedCandidate(original.Key, original.Id, TargetLanguage.English, intent, refresh.Spec);
            var replay = store.DeriveNamedCandidate(original.Key, original.Id, TargetLanguage.English, intent, refresh.Spec);
            Expect(derived == replay && replay.Revision == 2 && replay.Candidate.Approved && !replay.Candidate.IsHumanEdited);
            Expect(store.GetCandidate(old.Id) == old && store.ListCandidates(original.Id, TargetLanguage.English).Count == 2);
            var revised = names with { Revision = 2, Entries = [person with { EnglishName = "T. Yamada" }] };
            var next = PersonNameRendering.Refresh(original, derived.Candidate, revised, null, Guid.NewGuid());
            Expect(next.Text == "T. Yamada and T. Yamada travel");
            store.Edit(derived.Candidate.Id, derived.Candidate.Revision, "Human changed title");
            Throws<RevisionConflictException>(() => store.DeriveNamedCandidate(original.Key, original.Id, TargetLanguage.English, intent, refresh.Spec));
            return Task.CompletedTask;
        });
        await check("refresh_preserves_human_edits_stale_selection_and_changed_sources", () =>
        {
            var store = new CandidateStore(Path.Combine(root, Guid.NewGuid().ToString("N") + ".db"));
            var original = store.ObserveSource(source.Key, source.OriginalTitle, source.DisplayPrefix);
            var spec = TitlePipeline.Prepare(original, TargetLanguage.English, ["山田太郎", "太郎"], rules);
            var candidate = store.SaveGenerated(original.Id, TargetLanguage.English, "山田太郎 and 太郎 travel", spec);
            candidate = store.Approve(candidate.Id, candidate.Revision);
            var refresh = PersonNameRendering.Refresh(original, candidate, names, null, Guid.NewGuid());
            Throws<RevisionConflictException>(() => store.DeriveNamedCandidate(original.Key, original.Id, TargetLanguage.English,
                new(candidate.Id, candidate.Revision, 999, refresh.Text), refresh.Spec));
            var edited = store.Edit(candidate.Id, candidate.Revision, "人工 title");
            Throws<ArgumentException>(() => PersonNameRendering.Refresh(original, edited, names, null, Guid.NewGuid()));
            store.ObserveSource(source.Key, "変更原文", source.DisplayPrefix);
            Throws<SourceChangedException>(() => store.DeriveNamedCandidate(original.Key, original.Id, TargetLanguage.English,
                new(candidate.Id, candidate.Revision, 1, refresh.Text), refresh.Spec));
            Expect(store.ListCandidates(original.Id, TargetLanguage.English).Count == 1); return Task.CompletedTask;
        });
    }
}
