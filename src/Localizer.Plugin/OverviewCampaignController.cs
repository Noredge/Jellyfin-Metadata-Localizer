using Localizer.Jellyfin;
using System.Text.Json;
using Localizer.Core;
using Localizer.Translation;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record OverviewCampaignReceipt(string State, string? Text = null, string? Error = null);

public sealed partial class AdminController
{
    private CampaignItem PlanOverview(Guid folder, Movie movie, TargetLanguage language, bool generate,
        bool retranslate, bool preserveManual, string? displayLanguage)
    {
        var item = LibraryItem(movie, language.Tag(), "Overview");
        var key = Key(folder, movie.Id); var store = Overviews(); var read = JellyfinNfoOverview.Read(movie);
        if (read.Error is not null) return item with { State = "Skipped", ErrorCategory = read.Error };
        var state = store.Read(key);
        state = store.Observe(key, state.Revision, read);
        var selected = state.Selected(language);
        var names = library.GetPeople(movie).Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)
            && state.Source!.Text.Contains(x, StringComparison.Ordinal));
        var plan = new OverviewCampaignPlan(state.Source!.Version, state.Source.Hash, movie.Overview ?? "",
            selected?.Id, OverviewPipeline.Prepare(state.Source.Text, language, names));
        if (!generate && selected is null) return item with { State = "Skipped", ErrorCategory = "overview_translation_missing", OverviewPlan = plan };
        var reuse = !generate || selected is not null && (!retranslate || preserveManual && selected.HumanEdited);
        return item with { OverviewPlan = plan, CandidateId = reuse ? selected?.Id : null, TranslationOrigin = reuse ? "Reused" : null,
            State = reuse && displayLanguage != language.Tag() ? "Saved" : "Prepared" };
    }

    private static void WriteCampaignOverviewReceipt(string path, OverviewCampaignReceipt value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
        System.IO.File.Move(temp, path, true);
    }

    // Returns a pause category for infrastructure/uncertain failures; semantic failures let other rows continue.
    [NonAction]
    internal async Task<string?> ProcessOverviewCampaign(CampaignRecord campaign, int ordinal, TranslationResume resume, CancellationToken token)
    {
        if (!await OverviewGenerationGate.WaitAsync(0, token)) return "OverviewWorkerBusy";
        try
        {
            var journal = Campaigns(); var item = journal.Read(campaign.Id).Items[ordinal];
            var folder = Guid.Parse(campaign.Scope.LibraryId); var key = Key(folder, item.ItemId);
            var plan = item.OverviewPlan ?? throw new SourceChangedException();
            var language = Languages.Parse(item.Language!); var store = Overviews(); var state = store.Read(key);
            RequireOverviewSource(folder, item.ItemId, state);
            if (state.Source!.Version != plan.SourceVersion || state.Source.Hash != plan.SourceHash) throw new SourceChangedException();
            if (item.CandidateId is null)
            {
                var path = Path.Combine(Root, "overview-campaign-generations", item.JobId + ".json");
                var receipt = System.IO.File.Exists(path) ? JsonSerializer.Deserialize<OverviewCampaignReceipt>(System.IO.File.ReadAllText(path)) : null;
                if (receipt?.State is "Sending" or "Uncertain")
                {
                    if (!resume.AcceptUncertainRequest)
                    { journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Uncertain", ErrorCategory = "RequestOutcomeUnknown" }); return "RequestOutcomeUnknownNeedsReview"; }
                    // Explicit consent to a new request. Old uncertain receipt remains on disk.
                    item = journal.ChangeItem(campaign.Id, ordinal, x => x with { JobId = Guid.NewGuid().ToString("N") }).Items[ordinal];
                    path = Path.Combine(Root, "overview-campaign-generations", item.JobId + ".json"); receipt = null;
                }
                if (receipt?.State == "Failed")
                {
                    if (!resume.RetryFailed) return null;
                    item = journal.ChangeItem(campaign.Id, ordinal, x => x with { JobId = Guid.NewGuid().ToString("N") }).Items[ordinal];
                    path = Path.Combine(Root, "overview-campaign-generations", item.JobId + ".json"); receipt = null;
                }
                var own = state.Candidates.SingleOrDefault(x => x.Id == item.JobId);
                if (own is null && state.Selected(language)?.Id != plan.BaselineCandidateId)
                    throw new OperationValidationException("overview_candidate_changed_preserved");
                if (own is null && receipt is null)
                {
                    var service = campaign.Service ?? throw new ArgumentException();
                    journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Translating", ErrorCategory = null });
                    WriteCampaignOverviewReceipt(path, new("Sending"));
                    var output = await new OverviewProvider(() => ProviderCredential.LoadFor(Root, service))
                        .TranslateAsync(service, plan.Input, token);
                    receipt = new(output.Error is null ? "Received" : output.Error is "network" or "interrupted" ? "Uncertain" : "Failed", output.Text, output.Error);
                    WriteCampaignOverviewReceipt(path, receipt);
                }
                if (own is null && receipt?.State != "Received")
                {
                    var error = receipt?.Error ?? "interrupted";
                    state = store.Read(key);
                    if (state.Source?.Version == plan.SourceVersion) store.Fail(key, state.Revision, plan.SourceVersion, language, error);
                    var uncertain = receipt?.State == "Uncertain";
                    journal.ChangeItem(campaign.Id, ordinal, x => x with { State = uncertain ? "Uncertain" : "Failed", ErrorCategory = error });
                    return uncertain ? "RequestOutcomeUnknownNeedsReview" : error is "rate_limit" or "authentication" or "service_unavailable" ? error : null;
                }
                state = store.Read(key); RequireOverviewSource(folder, item.ItemId, state);
                if (state.Source!.Version != plan.SourceVersion || state.Source.Hash != plan.SourceHash) throw new SourceChangedException();
                if (own is null)
                {
                    if (state.Selected(language)?.Id != plan.BaselineCandidateId) throw new OperationValidationException("overview_candidate_changed_preserved");
                    state = store.Save(key, state.Revision, plan.SourceVersion, language, receipt!.Text!, false,
                        campaign.Service!.Id, campaign.Service.Model, plan.Input.PromptVersion, campaign.PreserveHumanEdits, item.JobId);
                    own = state.Candidates.Single(x => x.Id == item.JobId);
                }
                if (state.Selected(language)?.Id != own.Id) throw new OperationValidationException("overview_candidate_changed_preserved");
                item = journal.ChangeItem(campaign.Id, ordinal, x => x with { CandidateId = own.Id, State = "Translated", ErrorCategory = null, TranslationOrigin = "Generated" }).Items[ordinal];
            }
            if ((campaign.IndependentDisplay ? campaign.OverviewDisplayLanguage : campaign.DisplayLanguage) != item.Language)
            { journal.ChangeItem(campaign.Id, ordinal, x => x with { State = "Saved", ErrorCategory = null }); return null; }
            var writes = OverviewWrites(folder); var id = Guid.Parse(item.OperationId);
            var existing = writes.List(key).SingleOrDefault(x => x.Id == item.OperationId);
            if (existing?.State is "Prepared" or "Writing") existing = await writes.RecoverAsync(id, key, token);
            if (resume.RetryFailed && existing?.State is "Blocked" or "NotApplied")
            {
                item = journal.ChangeItem(campaign.Id, ordinal, x => x with {
                    PreviousOperationIds = [..(x.PreviousOperationIds ?? []), x.OperationId], OperationId = Guid.NewGuid().ToString("N") }).Items[ordinal];
                id = Guid.Parse(item.OperationId); existing = null;
            }
            if (existing is null)
            {
                state = store.Read(key);
                if (state.Selected(language)?.Id != item.CandidateId) throw new OperationValidationException("overview_candidate_changed_preserved");
                existing = await writes.ApplyAsync(id, key, state.Revision, item.CandidateId!, plan.ExpectedDisplay, token);
            }
            var success = existing.State is "Applied" or "ObservedApplied" or "NoChange";
            journal.ChangeItem(campaign.Id, ordinal, x => x with { State = success ? "Applied" : "Failed", ErrorCategory = success ? null : "overview_write_" + existing.State });
            return null;
        }
        finally { OverviewGenerationGate.Release(); }
    }
}
