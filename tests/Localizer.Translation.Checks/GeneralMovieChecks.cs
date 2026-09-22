using System.Text.Json;
using Localizer.Core;
using Localizer.Translation;

internal static class GeneralMovieChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> check)
    {
        static void Expect(bool value) { if (!value) throw new InvalidOperationException("General movie assertion failed."); }
        static async Task Reject(Func<Task> action)
        {
            try { await action(); }
            catch (ArgumentException) { return; }
            catch (TitleProviderException error) when (error.Category == ProviderError.Configuration) { return; }
            throw new InvalidOperationException("Expected configuration rejection.");
        }
        var rules = new TitleRules("general-test", "Preserve names and numbers.", []);
        var source = new SourceSnapshot(1, new("synthetic", "movies", "item"), 1, "A journey", "", "hash");
        static string Response(string model, string title) => JsonSerializer.Serialize(new { model,
            choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { title }) } } } });
        await check("general_titles_accept_short_numeric_and_non_ascii_names", () =>
        {
            foreach (var language in new[] { TargetLanguage.English, TargetLanguage.SimplifiedChinese })
            foreach (var title in new[] { "Up", "It", "M", "1917", "WALL-E", "Été", "你好" })
            {
                var item = source with { OriginalTitle = title };
                var spec = TitlePipeline.Prepare(item, language, [], rules);
                var parsed = TitlePipeline.Parse(Response(spec.Model, title), new(item, language, spec));
                Expect(parsed.ErrorCategory is null && parsed.Title == title);
            }
            foreach (var title in new[] { "", "...", "\ufffd", "A\u0000B" })
            {
                var spec = TitlePipeline.Prepare(source, TargetLanguage.English, [], rules);
                Expect(TitlePipeline.Parse(Response(spec.Model, title), new(source, TargetLanguage.English, spec)).ErrorCategory == "InvalidText");
            }
            return Task.CompletedTask;
        });
        await check("general_latin_names_do_not_match_inside_other_words", () =>
        {
            var protection = TitlePipeline.Protect("Annual reunion: Ann meets Li in Life. Éva travels; Évasion waits.", ["Ann", "Li", "Éva"]);
            Expect(protection.Mapping.Count == 3 && protection.MaskedSource.Contains("Annual")
                && protection.MaskedSource.Contains("Life") && protection.MaskedSource.Contains("Évasion"));
            var item = source with { OriginalTitle = "Annual reunion with Ann" };
            var spec = TitlePipeline.Prepare(item, TargetLanguage.English, ["Ann"], rules);
            Expect(TitlePipeline.Parse(Response(spec.Model, "Annual reunion with __JML_PERSON_A__"), new(item, TargetLanguage.English, spec)).ErrorCategory is null);
            Expect(TitlePipeline.Parse(Response(spec.Model, "与__JML_PERSON_A__重逢"), new(item, TargetLanguage.SimplifiedChinese, spec)).ErrorCategory is null);
            Expect(TitlePipeline.Protect("Li's journey with Jean-Luc.", ["Li", "Jean-Luc"]).Mapping.Count == 2);
            Expect(TitlePipeline.Protect("山田太郎と太郎の旅", ["山田太郎", "太郎"]).Mapping.Count == 2);
            return Task.CompletedTask;
        });
        await check("general_prompts_preserve_supplied_languages_and_resource_rules", () =>
        {
            foreach (var text in new[] { "A journey", "一次旅行", "ひとつの旅" })
            {
                var item = source with { OriginalTitle = text };
                var spec = TitlePipeline.Prepare(item, TargetLanguage.English, [], rules);
                Expect(spec.PromptText.Contains("from its original language") && TitlePipeline.ValidateContext(new(item, TargetLanguage.English, spec)).MaskedSource == text);
                Expect(OverviewPipeline.Prepare(text, TargetLanguage.English, []).Prompt.Contains("from its original language"));
            }
            Expect(GenreTranslationPipeline.Prompt.Contains("from their original language"));
            foreach (var tag in new[] { "zh-Hans", "en" })
            {
                var value = JsonSerializer.Deserialize<TitleRules>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "title-rules." + tag + ".json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                Expect(value.Version.StartsWith("general-title-rules-", StringComparison.Ordinal) && value.Rules.Length == 0);
            }
            return Task.CompletedTask;
        });
        await check("general_unconfigured_models_never_send_or_load_credentials", async () =>
        {
            var profiles = new[] { TranslationServiceProfile.OpenAiDefault(), TranslationServiceProfile.GroqDefault(),
                new TranslationServiceProfile("local", "lmstudio", "http://127.0.0.1:1234/api/v1", TranslationServiceProfile.UnconfiguredModel),
                new TranslationServiceProfile("custom", "openai-compatible", "https://api.example.com/v1", TranslationServiceProfile.UnconfiguredModel) };
            foreach (var profile in profiles)
            {
                profile.Validate(); Expect(!profile.IsConfigured);
                var spec = TitlePipeline.Prepare(source, TargetLanguage.English, [], rules);
                await Reject(() => { profile.Apply(spec); return Task.CompletedTask; });
                var configured = profile with { Model = "synthetic-model" };
                var frozen = configured.Apply(spec) with { Model = TranslationServiceProfile.UnconfiguredModel };
                var keys = 0; var cloud = new StubHttp(); var local = new StubHttp(); var ai = new StubHttp();
                using var router = new TranslationServiceRouter(_ => { keys++; return ValueTask.FromResult("test"); }, cloud, local,
                    _ => { keys++; return ValueTask.FromResult("test"); }, ai);
                await Reject(() => router.TranslateAsync(new(source, TargetLanguage.English, frozen), default));
                var overview = new OverviewProvider(() => { keys++; return "test"; }, cloud);
                await Reject(() => overview.TranslateAsync(profile, OverviewPipeline.Prepare("A journey", TargetLanguage.English, []), default));
                var genres = new GenreTranslationProvider(() => { keys++; return "test"; }, cloud);
                await Reject(() => genres.TranslateAsync(profile, [new("G0001", "Drama", [TargetLanguage.SimplifiedChinese])], default));
                Expect(keys == 0 && cloud.Calls == 0 && local.Calls == 0 && ai.Calls == 0);
            }
        });
    }
}
