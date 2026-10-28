using System.Text.Json;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class ExecutorChecks
{
    internal static async Task Run(string root)
    {
        if (!Path.IsPathFullyQualified(root) || Directory.Exists(root)) throw new ArgumentException("Use a new absolute test directory");
        Directory.CreateDirectory(root); var checks = new List<object>();
        void Check(string name, bool valid)
        { checks.Add(new { name, passed = valid }); Console.WriteLine((valid ? "PASS " : "FAIL ") + name); if (!valid) throw new InvalidOperationException(name); }
        try
        {
            using (var f = new ExecutorFixture(Path.Combine(root, "basic")))
            {
                var a = f.Add("a", "山を歩く。", "共通", "山"); var b = f.Add("b", "海を歩く。", "共通", "海");
                var batch = f.Submit([a, b]); await f.Drain();
                Check("two films share one in-flight generation per genre term", f.Transport.Calls == 7 && f.Transport.Inputs.Count(x => x.Field == "Term" && x.Text == "共通") == 1);
                Check("all media fields independently complete and display", f.Work.List(batch.Id).All(x => x.State == "completed") && f.Host.Writes == 6);
                Check("request rows represent actual sends only", f.Count("SELECT COUNT(*) FROM request_attempts WHERE state='succeeded'") == 7 && f.Count("SELECT COUNT(*) FROM request_attempts") == 7);
                Check("genre parents release the execution position for shared children", f.Count("SELECT COUNT(*) FROM work_dependencies") == 4);
                Check("single-request concurrency preserved", f.Transport.MaximumActive == 1);
                var s = f.Settings.Read(); f.Settings.Save(s.Revision, "en", s.Fields, s.ManagedLibraries, true);
                var english = f.Submit([a, b]); await f.Drain();
                Check("English uses the same queue and term sharing", f.Transport.Calls == 14 && f.Work.List(english.Id).All(x => x.State == "completed"));
                s = f.Settings.Read(); f.Settings.Save(s.Revision, "zh-Hans", s.Fields, s.ManagedLibraries, true);
                var chinese = f.Submit([a, b]); await f.Drain();
                Check("Chinese English Chinese reuses saved Chinese without HTTP", f.Transport.Calls == 14 && f.Work.List(chinese.Id).All(x => x.State == "completed"));
                var oldVersions = f.Count("SELECT COUNT(*) FROM translation_versions");
                var connection = f.Settings.Connection()!; f.Settings.SaveConnection(connection.Revision, connection.Endpoint, "different-model", 5000, 1000, connection.CredentialReference, "{}"); f.Transport.Version = "fake-v2";
                f.Submit([a, b]); await f.Drain();
                Check("model and prompt changes do not invalidate existing selections", f.Transport.Calls == 14 && f.Count("SELECT COUNT(*) FROM translation_versions") == oldVersions);
                var title = f.Content.ReadField(a, "Name")!;
                f.Host.Change(f.Key(a), "Name", x => x with { Prefix = "NEW · ", Display = "NEW · " + x.Original }); var prefix = f.Observe(a, "Name");
                f.Submit([a], ["Name"]); await f.Drain();
                Check("title prefix edit only rerenders the existing body", prefix.SourceId == title.SourceId && f.Transport.Calls == 14 && f.Host.Get(f.Key(a), "Name").Display.StartsWith("NEW · 中文 ", StringComparison.Ordinal));
                var genres = f.Content.ReadField(a, "Genres")!;
                f.Host.Change(f.Key(a), "Genres", x => x with { Original = "[\"山\",\"共通\",\"山\"]", Display = "[\"山\",\"共通\",\"山\"]" }); var reordered = f.Observe(a, "Genres");
                f.Submit([a], ["Genres"]); await f.Drain();
                Check("genre order and duplicates change rendering without requests", genres.SourceId == reordered.SourceId && f.Transport.Calls == 14 && JsonSerializer.Deserialize<string[]>(f.Host.Get(f.Key(a), "Genres").Display)!.SequenceEqual(new[] { "中文 山", "中文 共通" }));
                var empty = f.Add("empty", ""); var skipped = f.Submit([empty], ["Overview"]); await f.Drain();
                Check("empty synopsis creates neither pending work nor HTTP", skipped.WorkIds.Length == 0 && f.Transport.Calls == 14);
                var keep = f.Content.ReadField(a, "Name")!; f.Content.KeepOriginal(keep, f.Host.Get(f.Key(a), "Name").Display);
                var original = f.Submit([a], ["Name"], operation: "display"); await f.Drain();
                Check("keep original uses the same display executor", f.Work.List(original.Id).All(x => x.State == "completed") && f.Host.Get(f.Key(a), "Name").Display == "NEW · " + keep.Content);
                s = f.Settings.Read(); f.Settings.Save(s.Revision, "en", s.Fields, s.ManagedLibraries, true); f.Submit([a], ["Name"]); await f.Drain();
                Check("global language switch does not release keep-original choice", f.Transport.Calls == 14 && f.Host.Get(f.Key(a), "Name").Display == "NEW · " + keep.Content);
                Check("all queue and request references remain valid", f.Count("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0 && f.Text("PRAGMA integrity_check") == "ok");
            }
            await ExecutorRaceChecks.Run(root, Check);
            await ExecutorFailureChecks.Run(root, Check);
            await ExecutorConfigurationChecks.Run(root, Check);
            await ExecutorProcessChecks.Run(root, Check);
            await Private14Checks.Run(root, Check);
            await Private16Checks.Run(root, Check);
            await Private17Checks.Run(root, Check);
            await Private18Checks.Run(root, Check);
            await Private19Checks.Run(root, Check);
            await Private20Checks.Run(root, Check);
            await Private21Checks.Run(root, Check);
            await Private22Checks.Run(root, Check);
            PublicReleaseChecks.Run(root, Check);
            File.WriteAllText(Path.Combine(root, "executor-checks.json"), JsonSerializer.Serialize(new { status = "passed", checks, realModelRequests = 0, productionHostWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "executor-checks.json"), JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true })); throw;
        }
    }
}
