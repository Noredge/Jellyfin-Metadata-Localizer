using System.Text;
using System.Text.Json;
using Localizer.Core;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class LocalRuleChecks
{
    internal static void Run(string root, Action<string, bool> check)
    {
        var directory = Path.Combine(root, "local-rule-pack"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, LocalRulePack.FileName);
        string Pack(string instruction = "Translate the supplied meaning naturally.") => JsonSerializer.Serialize(new
        {
            format = 1,
            titleRules = new Dictionary<string, object> { ["en"] = new { version = "fixture-v1", instructions = instruction,
                rules = new[] { new { id = "season", pattern = "season", instruction = "Preserve season numbering." } } } },
            genreTerms = new[] { new { original = "Fixture genre", chinese = "合成分类", english = "Fixture genre" } }
        });
        bool Invalid(string text)
        {
            try { LocalRulePack.Parse(Encoding.UTF8.GetBytes(text)); return false; }
            catch (InvalidOperationException e) { return e.Message == "LocalRulesInvalid"; }
        }
        check("missing local rules preserve public defaults", !LocalRulePack.Load(directory).Loaded);
        File.WriteAllText(path, Pack());
        var provider = new LocalRulePackProvider(directory); var rules = provider.Read();
        check("local pack loads explicit title language and complete dictionary", rules.Loaded && rules.Titles.Keys.SequenceEqual(["en"])
            && rules.Genres?.Count == 1 && rules.Genres["Fixture genre"].Chinese == "合成分类");
        check("unconfigured title language keeps default fallback available", !rules.Titles.ContainsKey("zh-Hans"));
        check("custom prompt identity includes a stable content fingerprint", rules.Titles["en"].Version.StartsWith("fixture-v1/local-", StringComparison.Ordinal));
        var firstVersion = rules.Titles["en"].Version;
        check("Windows UTF-8 BOM preserves the same prompt identity", LocalRulePack.Parse(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Pack())).ToArray()).Titles["en"].Version == firstVersion);
        File.WriteAllText(path, Pack("Preserve all source facts and natural target phrasing."));
        check("running installation freezes local rules until restart", ReferenceEquals(provider.Read(), rules) && provider.Read().Titles["en"].Version == firstVersion);
        var updated = new LocalRulePackProvider(directory).Read();
        check("same human version with changed prompt gets new request provenance", updated.Titles["en"].Version != firstVersion);
        using (var db = new CheckDatabase(directory))
        {
            var store = new ConfigurationStore(db.Value); store.SeedBuiltin();
            check("configured local dictionary replaces installation defaults", store.Terms().Length == 1 && store.Terms()[0].Original == "Fixture genre");
            store.SaveTerm("Fixture genre", "zh-Hans", "人工版本", 0, "local-manual"); store.SeedBuiltin();
            check("local dictionary seeding preserves manual selections", store.Terms()[0].Chinese == "人工版本");
            store.RestoreBuiltin("Fixture genre", "zh-Hans", 1);
            check("restore builtin uses the current local rule pack", store.Terms()[0].Chinese == "合成分类");
            using var read = db.Value.OpenRead();
            check("local rules never submit work or provider requests", Scalar<long>(read, null, "SELECT (SELECT COUNT(*) FROM work_items)+(SELECT COUNT(*) FROM request_attempts)") == 0);
        }
        var databaseBytes = File.ReadAllBytes(Path.Combine(directory, "metadata-localizer.db"));
        File.WriteAllText(path, "{\"format\":1,\"apiKey\":\"SYNTHETIC_NOT_A_CREDENTIAL\"}");
        var lazy = new LocalRulePackProvider(directory); var rejected = false;
        try { lazy.Read(); } catch (InvalidOperationException e) { rejected = e.Message == "LocalRulesInvalid"; }
        check("invalid local config is lazily rejected with a safe diagnostic", rejected);
        try { _ = new UnifiedDatabase(directory); } catch (InvalidOperationException) { }
        check("invalid local config fails before any database write", File.ReadAllBytes(Path.Combine(directory, "metadata-localizer.db")).SequenceEqual(databaseBytes));
        foreach (var (name, bad) in new[]
        {
            ("unsupported format", "{\"format\":2}"),
            ("duplicate properties", "{\"format\":1,\"Format\":1}"),
            ("unknown properties", "{\"format\":1,\"endpoint\":\"https://example.org\"}"),
            ("unsupported title language", "{\"format\":1,\"titleRules\":{\"fr\":{}}}"),
            ("missing title instructions", Pack().Replace("Translate the supplied meaning naturally.", "", StringComparison.Ordinal)),
            ("invalid regex", Pack().Replace("\"pattern\":\"season\"", "\"pattern\":\"[\"", StringComparison.Ordinal)),
            ("duplicate genre originals", "{\"format\":1,\"genreTerms\":[{\"original\":\"X\",\"chinese\":\"甲\",\"english\":\"X\"},{\"original\":\"X\",\"chinese\":\"乙\",\"english\":\"Y\"}]}"),
            ("null title rule", "{\"format\":1,\"titleRules\":{\"en\":null}}")
        }) check("local rules reject " + name, Invalid(bad));
        check("local rule input is bounded before parsing", Invalid(new string(' ', LocalRulePack.MaximumBytes + 1)));
        File.WriteAllText(path, Pack());
        check("corrected config loads in a new process scope", new LocalRulePackProvider(directory).Read().Loaded);
        File.Delete(path); Directory.CreateDirectory(path);
        rejected = false; try { LocalRulePack.Load(directory); } catch (InvalidOperationException e) { rejected = e.Message == "LocalRulesInvalid"; }
        check("a directory cannot silently hide an invalid local rules file", rejected);
        Directory.Delete(path);
    }
    private sealed class CheckDatabase(string root) : IDisposable
    {
        public UnifiedDatabase Value { get; } = new(root, true);
        public void Dispose() => Value.ReleaseReadPool();
    }
}
