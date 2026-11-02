using System.Text.Json;
using System.Text.RegularExpressions;
using Localizer.Core;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class Rc2Checks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        foreach (var field in new[] { "Name", "Overview" })
        using (var f = new ExecutorFixture(Path.Combine(root, "rc2-unknown-" + field)))
        {
            var media = f.Add("Alice");
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "network_uncertain", OutcomeUnknown: true)); };
            f.Submit([media], [field]); await f.Drain();
            var oldEvidence = f.Text("SELECT id||'|'||error_code||'|'||elapsed_ms FROM request_attempts");
            new ConfigurationStore(f.Database).SavePerson(Id(), 0, "Alice", "阿莉", "Alice", []);
            f.Transport.Version = "changed-prompt";
            var connection = f.Settings.Connection()!;
            f.Settings.SaveConnection(connection.Revision, connection.Endpoint, "changed-model", 5000, 1000, connection.CredentialReference, "{}");
            f.Restart();
            var replacement = f.Submit([media], [field], true).WorkIds.Single(); await f.Drain();
            check("unknown consent survives restart and changed prompt model and person rules: " + field,
                f.Transport.Calls == 1 && f.Work.Find(replacement) is { State: "paused", Error: "input_outcome_unknown" } && f.Count("SELECT COUNT(*) FROM request_attempts") == 1);
            var denied = false;
            try { f.Work.Retry(replacement); } catch (InvalidOperationException e) when (e.Message == "UnknownOutcomeConsentRequired") { denied = true; }
            check("replacement without its own attempt still requires consent: " + field, denied && f.Count("SELECT uncertainty_acknowledged FROM request_attempts") == 0);
            f.Transport.Handler = null; f.Work.Retry(replacement, acceptUnknown: true); await f.Drain();
            check("accepted replacement sends exactly once: " + field, f.Transport.Calls == 2 && f.Work.Find(replacement)!.State == "completed");
            check("acceptance retains original unknown evidence: " + field,
                f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='unknown' AND uncertainty_acknowledged=1") == 1
                && f.Text("SELECT id||'|'||error_code||'|'||elapsed_ms FROM request_attempts WHERE state='unknown'") == oldEvidence);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "rc2-unknown-term")))
        {
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "network_uncertain", OutcomeUnknown: true)); };
            f.Work.SubmitTerms(Id(), "dictionary", f.Settings.Read().Revision, ["共享词"], true); await f.Drain();
            f.Transport.Version = "changed-term-prompt";
            var next = f.Work.SubmitTerms(Id(), "dictionary", f.Settings.Read().Revision, ["共享词"], true).WorkIds.Single(); await f.Drain();
            check("shared dictionary unknown cannot be bypassed with a new fingerprint", f.Transport.Calls == 1 && f.Work.Find(next)!.State == "paused");
            var denied = false;
            try { f.Work.Retry(next); } catch (InvalidOperationException e) when (e.Message == "UnknownOutcomeConsentRequired") { denied = true; }
            check("shared dictionary replacement requires consent", denied);
            f.Transport.Handler = null; f.Work.Retry(next, true); await f.Drain();
            check("shared dictionary consent resumes without discarding history", f.Transport.Calls == 2 && f.Work.Find(next)!.State == "completed" && f.Count("SELECT COUNT(*) FROM request_attempts") == 2);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "rc2-unknown-scope")))
        {
            var media = f.Add("scope");
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "network_uncertain", OutcomeUnknown: true)); };
            f.Submit([media], ["Name"]); await f.Drain(); f.Transport.Handler = null;
            f.Submit([media], ["Overview"]); await f.Drain();
            var settings = f.Settings.Read(); f.Settings.Save(settings.Revision, "en", settings.Fields, settings.ManagedLibraries, true);
            f.Submit([media], ["Name"]); await f.Drain();
            check("unknown scope permits another field and another target language", f.Transport.Calls == 3);
            settings = f.Settings.Read(); f.Settings.Save(settings.Revision, "zh-Hans", settings.Fields, settings.ManagedLibraries, true);
            var back = f.Submit([media], ["Name"], true).WorkIds.Single(); await f.Drain();
            check("returning to original target language does not clear unknown consent", f.Transport.Calls == 3 && f.Work.Find(back)!.State == "paused");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "rc2-unknown-cache-display")))
        {
            var media = f.Add("display");
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply(null, "network_uncertain", OutcomeUnknown: true)); };
            f.Submit([media], ["Name"]); await f.Drain(); f.Transport.Handler = null;
            var field = f.Content.ReadField(media, "Name")!;
            f.Host.Change(f.Key(media), "Name", x => x with { Writable = false, DisplayError = "field_locked" });
            f.Content.SaveManual(field, "zh-Hans", "已保存译文", true, f.Host.Get(f.Key(media), "Name").Display, "known-manual"); await f.Drain();
            var display = f.Work.List().Single(x => x.State == "failed").Id;
            f.Host.Change(f.Key(media), "Name", x => x with { Writable = true, DisplayError = null });
            f.Work.Retry(display); await f.Drain();
            check("display-only retry neither requires nor clears unrelated unknown consent", f.Transport.Calls == 1 && f.Work.Find(display)!.State == "completed"
                && f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='unknown' AND uncertainty_acknowledged=0") == 1);
            var reuse = f.Submit([media], ["Name"]).WorkIds.Single();
            f.Database.Write((db, tx) => Execute(db, tx, "UPDATE work_items SET state='paused',issue_kind='translation',error_code='input_outcome_unknown' WHERE id=$id", ("$id", reuse)));
            var noSavedReference = f.Work.Find(reuse)!.TranslationId is null;
            f.Work.Retry(reuse); await f.Drain();
            check("retry can reuse a selected cache without acknowledging a prior unknown request", noSavedReference && f.Transport.Calls == 1 && f.Work.Find(reuse)!.State == "completed"
                && f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='unknown' AND uncertainty_acknowledged=0") == 1);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "rc2-person-validation")))
        {
            var config = new ConfigurationStore(f.Database); var id = Id();
            var first = config.SavePerson(id, 0, "Alpha", "甲", "Alpha", ["Bob", "Alpha", "Bob"]);
            check("same-identity aliases are normalized without losing the rule", first.Aliases.SequenceEqual(["Bob"]));
            foreach (var (original, aliases) in new[] { ("Bob", Array.Empty<string>()), ("Gamma", new[] { "Bob" }), ("Gamma", new[] { "Alpha" }) })
            {
                var denied = false;
                try { config.SavePerson(Id(), 0, original, "乙", null, aliases); } catch (PersonAliasConflictException) { denied = true; }
                check("cross-person original and alias collisions are rejected: " + original + aliases.Length, denied && config.People().Length == 1);
            }
            var other = config.SavePerson(Id(), 0, "Delta", "丁", null, []);
            var editDenied = false;
            try { config.SavePerson(id, first.Revision, "Alpha", "改名", null, ["Delta"]); } catch (PersonAliasConflictException) { editDenied = true; }
            check("rejected edit retains revision names and aliases", editDenied && JsonSerializer.Serialize(config.People().Single(x => x.Id == id)) == JsonSerializer.Serialize(first));
            var fixedRule = config.SavePerson(id, first.Revision, "Alpha", "甲", null, []);
            var reuse = config.SavePerson(other.Id, other.Revision, "Delta", "丁", null, ["Bob"]);
            check("explicitly released alias can be reassigned", fixedRule.Aliases.Length == 0 && reuse.Aliases.Single() == "Bob");
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "rc2-legacy-person-conflict")))
        {
            var media = f.Add("Bob"); var config = new ConfigurationStore(f.Database); var alpha = Id();
            config.SavePerson(alpha, 0, "Alpha", "甲", null, ["Bob"]);
            // Existing records from an older version are retained, not silently rewritten.
            f.Database.Write((db, tx) => Execute(db, tx, "INSERT INTO person_name_rules VALUES($id,'Bob','乙',NULL,'[]',1,'{}')", ("$id", Id())));
            var work = f.Submit([media], ["Name"]).WorkIds.Single(); await f.Drain();
            check("legacy ambiguity fails before any model request or display write", f.Transport.Calls == 0 && f.Host.Writes == 0 && f.Work.Find(work) is { State: "failed", Error: "person_alias_conflict" } && config.People().Length == 2);
            config.SavePerson(alpha, 1, "Alpha", "甲", null, []);
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply("乙的旅行", HttpStatus: 200)); };
            f.Work.Retry(work); await f.Drain();
            check("correcting legacy ambiguity allows safe retry", f.Transport.Calls == 1 && f.Host.Get(f.Key(media), "Name").Display.Contains("乙", StringComparison.Ordinal) && !f.Host.Get(f.Key(media), "Name").Display.Contains("甲", StringComparison.Ordinal));
            var history = f.Count("SELECT COUNT(*) FROM translation_versions");
            f.Database.Write((db, tx) => Execute(db, tx, "UPDATE person_name_rules SET aliases_json='[\"Bob\"]' WHERE id=$id", ("$id", alpha)));
            f.Host.Change(f.Key(media), "Name", x => x with { Display = x.Prefix + x.Original }); f.Observe(media, "Name");
            var cached = f.Submit([media], ["Name"]).WorkIds.Single(); await f.Drain();
            check("ambiguous cached rendering cannot apply the wrong person or alter history", f.Transport.Calls == 1 && f.Work.Find(cached) is { State: "failed", Error: "person_alias_conflict" } && f.Host.Get(f.Key(media), "Name").Display.EndsWith("Bob", StringComparison.Ordinal) && f.Count("SELECT COUNT(*) FROM translation_versions") == history);
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "rc2-frozen-name-collision")))
        {
            var media = f.Add("Alice Bob"); var config = new ConfigurationStore(f.Database); var alice = Id();
            config.SavePerson(alice, 0, "Alice", "同名", null, []); config.SavePerson(Id(), 0, "Bob", "同名", null, []);
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply("同名和同名", HttpStatus: 200)); };
            f.Submit([media], ["Name"]); await f.Drain();
            var original = f.Host.Get(f.Key(media), "Name").Display; var versions = f.Count("SELECT COUNT(*) FROM translation_versions");
            config.SavePerson(alice, 1, "Alice", "甲", null, []);
            var work = f.Submit([media], ["Name"]).WorkIds.Single(); await f.Drain();
            check("ambiguous historical display names never guess identities while rerendering", f.Transport.Calls == 1 && f.Host.Get(f.Key(media), "Name").Display == original
                && f.Work.Find(work) is { State: "failed", Error: "person_template_required" } && f.Count("SELECT COUNT(*) FROM translation_versions") == versions);
        }
        foreach (var (text, name, expected) in new[] {
            ("The Anniversary", "Ann", false), ("Air", "Ai", false), ("Ann, Bob", "Ann", true),
            ("Ann_1", "Ann", false), ("Ann2", "Ann", false), ("Ann\u0301a", "Ann", false),
            ("Renée", "Ren", false), ("Éva returns", "Éva", true), ("Évaluation", "Éva", false),
            ("Jean-Luc returns", "Jean-Luc", true), ("Alice回来了", "Alice", true), ("山田愛が帰る", "山田愛", true) })
        {
            var matched = PersonNameMatching.Contains(text, name);
            check("name boundary parity for literal and regex: " + text + "/" + name,
                matched == expected && Regex.IsMatch(text, PersonNameMatching.Pattern(name), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)) == expected);
        }
    }
}
