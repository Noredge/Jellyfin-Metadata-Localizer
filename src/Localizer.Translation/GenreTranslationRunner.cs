using Localizer.Core;

namespace Localizer.Translation;

/// <summary>Resumable batch execution. Host owns worker lifetime; no automatic uncertain retries.</summary>
public sealed class GenreTranslationRunner(GenreTranslationStore store,
    Func<TranslationServiceProfile, IReadOnlyList<GenreTranslationTerm>, CancellationToken, Task<GenreTranslationResult>> translate)
{
    public async Task<GenreTranslationTask> RunAsync(Guid id, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var task = store.Get(id);
            if (task.State != "Ready") return task;
            var connection = task.Connection ?? throw new InvalidOperationException("Missing frozen connection.");
            if (task.PromptVersion != GenreTranslationPipeline.Version) throw new InvalidOperationException("Unsupported dictionary prompt.");
            var profile = new TranslationServiceProfile(task.ServiceId, connection.Kind, connection.Endpoint, task.Model,
                connection.MaxOutputTokens, UseJsonResponseFormat: connection.UseJsonResponseFormat);
            profile.Validate();
            var words = task.Items.Where(x => x.State == "Pending").Select(x => x.Source.Original)
                .Distinct(StringComparer.Ordinal).Take(GenreTranslationPipeline.BatchLimit).ToArray();
            var indices = Enumerable.Range(0, task.Items.Length).Where(i => task.Items[i].State == "Pending"
                && words.Contains(task.Items[i].Source.Original, StringComparer.Ordinal)).ToArray();
            var batch = words.Select((word, n) => new GenreTranslationTerm($"G{n + 1:D4}", word,
                indices.Where(i => task.Items[i].Source.Original == word).Select(i => task.Items[i].Source.Language).ToArray())).ToArray();
            _ = GenreTranslationPipeline.Payload(batch);
            task = store.BeginBatch(id, task.Revision, indices);
            GenreTranslationResult result;
            try { result = await translate(profile, batch, token); }
            catch (OperationCanceledException) { result = new([], "network_uncertain"); }
            catch (Exception ex) when (ex is HttpRequestException or IOException) { result = new([], "network_uncertain"); }
            // Unexpected errors leave the persisted Sending claim for explicit startup recovery.
            var uncertain = result.Error == "network_uncertain";
            var pause = result.Error is "rate_limit" or "authentication" or "permission" or "credential_unavailable" or "service_unavailable";
            var validKeys = batch.SelectMany(t => t.Languages.Select(l => (t.Id, l))).ToHashSet();
            if (result.Error is null && (result.Candidates.Length != validKeys.Count
                || result.Candidates.Select(x => (x.Id, x.Language)).Distinct().Count() != validKeys.Count
                || result.Candidates.Any(x => !validKeys.Contains((x.Id, x.Language))))) result = new([], "invalid_output");
            var outputs = indices.Select(i =>
            {
                var source = task.Items[i].Source;
                var term = batch.Single(x => x.Original == source.Original);
                var candidate = result.Candidates.SingleOrDefault(x => x.Id == term.Id && x.Language == source.Language);
                var error = result.Error ?? candidate?.Error;
                var text = candidate?.Text;
                // Malformed values remain failures without breaking persistence for the entire task.
                if (text is not null && (string.IsNullOrWhiteSpace(text) || text.Length > 500 || text.Any(char.IsControl))) text = null;
                if (text is null && error is null) error = "invalid_text";
                return new GenreTranslationTaskItem(source, uncertain ? "Uncertain" : error is null ? "Generated" : "Failed", text, error);
            }).ToArray();
            task = store.FinishBatch(id, task.Revision, outputs, uncertain, pause);
            if (task.State != "Ready") return task;
        }
    }
}
