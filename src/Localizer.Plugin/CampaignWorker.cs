using Localizer.Core;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;

namespace Localizer.Plugin;

// The default delegates to the existing serial/global-leased production worker. The isolated test
// probe can replace this DI service; no browser setting, URL or production endpoint selects a fake.
public sealed class CampaignTranslationRunner(TranslationWorker worker) : ICampaignTranslationRunner
{
    public bool IsRunning(string id) => worker.IsRunning(id);
    public void Run(string id, TranslationResume resume) => worker.Run(id, resume);
}

public sealed class CampaignWorker(IApplicationPaths paths, ILibraryManager library, IApplicationHost host,
    TranslationWorker translationWorker, BatchWorker batchWorker, ICampaignTranslationRunner translations) : IHostedService
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task? running;
    private Guid? active;
    private string Root => Path.Combine(paths.DataPath, "metadata-localizer");
    private CampaignStore Journal => new(Root);
    private CandidateStore Store => new(Path.Combine(Root, "candidates.db"));
    public bool IsRunning(Guid id) { lock (gate) return active == id && running is { IsCompleted: false }; }
    public void Run(Guid id, TranslationResume resume)
    {
        lock (gate)
        {
            if (shutdown.IsCancellationRequested || running is { IsCompleted: false }) throw new OperationBusyException();
            var campaign = Journal.Read(id);
            if (campaign.State == "Completed") return;
            if (campaign.State is "Cancelled" or "CancelRequested") throw new OperationValidationException("campaign_cancelled");
            if (Journal.List().Any(x => x.Id != id && x.State is "Running" or "PauseRequested" or "CancelRequested" or "Paused" or "Interrupted"))
                throw new OperationBusyException();
            Journal.Change(id, x => x with { State = "Running", ErrorCategory = null });
            active = id;
            running = Task.Run(() => Execute(id, resume));
        }
    }

    private async Task Execute(Guid id, TranslationResume resume)
    {
        try
        {
            // Controller construction here is a context-free facade over the existing scoped guards.
            var operations = new AdminController(library, host, paths, translationWorker, batchWorker);
            for (var ordinal = 0; ordinal < Journal.Read(id).Items.Length; ordinal++)
            {
                shutdown.Token.ThrowIfCancellationRequested();
                var campaign = Journal.Read(id);
                if (campaign.State == "CancelRequested") { Cancel(id); return; }
                if (campaign.State == "PauseRequested") { Pause(id, null); return; }
                var item = campaign.Items[ordinal];
                if (item.State is "Applied" or "Saved" or "Review" or "Skipped") continue;
                if (item.State == "Failed" && !resume.RetryFailed) continue;
                if (item.State == "Uncertain" && !resume.AcceptUncertainRequest)
                { Pause(id, "RequestOutcomeUnknownNeedsReview"); return; }
                try
                {
                    if (campaign.Mode == "field_display" && item.Language == "original")
                    { await operations.ApplyOriginalField(campaign, ordinal, shutdown.Token); continue; }
                    if (item.Field is "Name" or "Overview")
                    {
                        var key = new MediaKey(campaign.Scope.ServerId, campaign.Scope.LibraryId, item.ItemId.ToString("N"));
                        var field = item.Field == "Name" ? DisplayField.Name : DisplayField.Overview;
                        var pendingName = Store.FindOperation(item.OperationId)?.State is OperationState.Prepared or OperationState.Writing or OperationState.Retryable;
                        var pendingOverview = item.Field == "Overview" && operations.HasPendingOverviewWrite(Guid.Parse(campaign.Scope.LibraryId), key, item.OperationId);
                        if (!pendingName && !pendingOverview && new DisplayPreferenceStore(Path.Combine(Root, "display-preferences.db")).Read(key, field).Mode == DisplayPreferenceMode.Original)
                        { Journal.ChangeItem(id, ordinal, x => x with { State = "Skipped", ErrorCategory = "original_display_preserved" }); continue; }
                    }
                    if (item.Field == "Overview")
                    {
                        var policy = campaign.Service?.Policy() ?? new TranslationPolicy(2, 0, 120000, 3000);
                        if (!await WaitForSlot(id, policy)) return;
                        var pause = await operations.ProcessOverviewCampaign(campaign.Mode == "field_display" ? campaign with { DisplayLanguage = item.Language } : campaign, ordinal, resume, shutdown.Token);
                        Journal.Change(id, x => x with { NextRequestUtc = DateTimeOffset.UtcNow.AddMilliseconds(policy.IntervalMs).ToString("O") });
                        if (pause is not null) { Pause(id, pause); return; }
                        continue;
                    }
                    if (item.Field == "Genres")
                    { await operations.ApplyLibraryGenre(campaign, ordinal, resume.RetryFailed, shutdown.Token); continue; }
                    if (campaign.Mode is "apply_saved" or "refresh_names" or "library_display" or "field_display")
                    {
                        if (campaign.Mode == "refresh_names") operations.PrepareNameRefreshItem(campaign, ordinal);
                        var saved = await operations.ApplySavedLanguageItem(campaign, ordinal, resume.RetryFailed, shutdown.Token);
                        var applied = saved.State is OperationState.Applied or OperationState.ObservedExpected or OperationState.NoChange;
                        Journal.ChangeItem(id, ordinal, x => x with { State = applied ? "Applied" : "Failed",
                            ErrorCategory = applied ? null : saved.ErrorCategory ?? saved.State.ToString() });
                        continue;
                    }
                    // A stored candidate bypasses preparation and all cloud work on write retry.
                    if (item.CandidateId is null)
                    {
                        item = operations.PrepareCampaignItem(campaign, ordinal);
                        var store = Store;
                        var input = store.GetTranslationItems(item.JobId).Single();
                        var priorJob = store.GetTranslationJob(item.JobId);
                        if (DateTimeOffset.TryParse(priorJob.NextRequestUtc, out var priorDue))
                            Journal.Change(id, x => !DateTimeOffset.TryParse(x.NextRequestUtc, out var currentDue) || priorDue > currentDue
                                ? x with { NextRequestUtc = priorDue.ToString("O") } : x);
                        if (input.State is not (TranslationItemState.Succeeded or TranslationItemState.Cached))
                        {
                            // Respect both the last job's provider timestamp and a durable between-job
                            // cooldown. Waiting here is cancellable/pauseable and does not hold a write lease.
                            if (!await WaitForSlot(id, priorJob.Policy)) return;
                            shutdown.Token.ThrowIfCancellationRequested();
                            // Recheck frozen source/name/candidates after the cooldown and before dispatch.
                            item = operations.PrepareCampaignItem(Journal.Read(id), ordinal);
                            Journal.ChangeItem(id, ordinal, x => x with { State = "Translating", ErrorCategory = null });
                            try { translations.Run(item.JobId, resume); }
                            catch (OperationBusyException) { Pause(id, "TranslationWorkerBusy"); return; }
                            while (translations.IsRunning(item.JobId))
                                await Task.Delay(200, shutdown.Token);
                            var job = store.GetTranslationJob(item.JobId);
                            var due = job.Policy.Adaptive ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddMilliseconds(job.Policy.IntervalMs);
                            if (DateTimeOffset.TryParse(job.NextRequestUtc, out var recorded) && recorded > due) due = recorded;
                            Journal.Change(id, x => x with { NextRequestUtc = due.ToString("O") });
                            input = store.GetTranslationItems(item.JobId).Single();
                        }
                        if (input.State is TranslationItemState.Requesting or TranslationItemState.Uncertain)
                        {
                            Journal.ChangeItem(id, ordinal, x => x with { State = "Uncertain", ErrorCategory = input.ErrorCategory ?? "RequestOutcomeUnknown" });
                            Pause(id, "RequestOutcomeUnknownNeedsReview"); return;
                        }
                        if (input.State is not (TranslationItemState.Succeeded or TranslationItemState.Cached) || input.CandidateId is null)
                        {
                            var failure = input.ErrorCategory ?? store.GetTranslationJob(item.JobId).ErrorCategory ?? "TranslationNotCompleted";
                            Journal.ChangeItem(id, ordinal, x => x with { State = "Failed", ErrorCategory = failure });
                            // Authentication, rate limit and worker failures should not consume every remaining row.
                            if (failure != "RequestRejected" && input.State is TranslationItemState.Failed or TranslationItemState.Pending or TranslationItemState.Received)
                            { Pause(id, failure); return; }
                            continue;
                        }
                        item = Journal.ChangeItem(id, ordinal, x => x with
                            { State = "Translated", CandidateId = input.CandidateId, ErrorCategory = null,
                                TranslationOrigin = input.State == TranslationItemState.Cached ? "Reused" : "Generated" }).Items[ordinal];
                    }
                    if (campaign.Mode == "review")
                    {
                        Journal.ChangeItem(id, ordinal, x => x with { State = "Review", ErrorCategory = null });
                        continue;
                    }
                    if (campaign.Mode == "library_translate")
                    { await operations.FinishLibraryTitle(Journal.Read(id), ordinal, resume.RetryFailed, shutdown.Token); continue; }
                    // A stored candidate may have been journaled just before a crash; recover the
                    // provider's already-persisted cooldown even when no translation rerun is needed.
                    var completedJob = Store.GetTranslationJob(item.JobId);
                    if (DateTimeOffset.TryParse(completedJob.NextRequestUtc, out var completedDue))
                        Journal.Change(id, x => !DateTimeOffset.TryParse(x.NextRequestUtc, out var currentDue) || completedDue > currentDue
                            ? x with { NextRequestUtc = completedDue.ToString("O") } : x);
                    var result = await operations.ApplyCampaignItem(Journal.Read(id), ordinal, resume.RetryFailed, shutdown.Token);
                    var success = result.State is OperationState.Applied or OperationState.ObservedExpected or OperationState.NoChange;
                    Journal.ChangeItem(id, ordinal, x => x with
                        { State = success ? "Applied" : "Failed", ErrorCategory = success ? null : result.ErrorCategory ?? result.State.ToString() });
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException)
                {
                    var current = Journal.Read(id).Items[ordinal];
                    var category = error is OperationValidationException validation ? validation.Category :
                        error is KeyNotFoundException ? "item_missing_or_out_of_scope" : "source_or_candidate_changed_preserved";
                    Journal.ChangeItem(id, ordinal, x => x with
                        { State = current.CandidateId is null ? "Skipped" : "Failed", ErrorCategory = category });
                }
            }
            var last = Journal.Read(id);
            if (last.State == "CancelRequested") { Cancel(id); return; }
            Journal.Change(id, x => x with { State = last.Items.Any(i => i.State is "Failed" or "Uncertain") ? "CompletedWithErrors" : "Completed",
                ErrorCategory = last.Items.Any(i => i.State is "Failed" or "Uncertain") ? "SomeItemsNeedAttention" : null });
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        { TryInterrupt(id, "ServerStoppedResumeRequired"); }
        catch (Exception)
        {
            // Exception messages may contain paths/provider details; only a fixed category is persisted.
            TryInterrupt(id, "CampaignInterruptedCheckLocalState");
        }
    }

    private async Task<bool> WaitForSlot(Guid id, TranslationPolicy policy)
    {
        while (true)
        {
            shutdown.Token.ThrowIfCancellationRequested();
            var campaign = Journal.Read(id);
            if (campaign.State == "CancelRequested") { Cancel(id); return false; }
            if (campaign.State == "PauseRequested") { Pause(id, null); return false; }
            var due = DateTimeOffset.TryParse(campaign.NextRequestUtc, out var next) ? next : DateTimeOffset.MinValue;
            // A server crash may occur after a model response and before the campaign's cooldown update.
            // Translation jobs persist their next request time before sending, so recover that lower bound.
            foreach (var item in campaign.Items.Where(x => x.State is "Translating" or "Translated" or "Accepting" or "Applying"))
            {
                try
                {
                    var job = Store.GetTranslationJob(item.JobId);
                    if (DateTimeOffset.TryParse(job.NextRequestUtc, out var recorded) && recorded > due) due = recorded;
                }
                catch (KeyNotFoundException) { }
            }
            var delay = due - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero) return true;
            if (policy.Adaptive && delay.TotalMilliseconds > policy.MaxAutoWaitMs)
            { Pause(id, "QuotaExceeded"); return false; }
            await Task.Delay(delay > TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : delay, shutdown.Token);
        }
    }
    private void Pause(Guid id, string? error) => Journal.Change(id, x => x.State == "CancelRequested"
        ? x with { State = "Cancelled", ErrorCategory = null } : x with { State = "Paused", ErrorCategory = error });
    private void Cancel(Guid id) => Journal.Change(id, x => x with { State = "Cancelled", ErrorCategory = null });
    private void TryInterrupt(Guid id, string category)
    {
        try { Journal.Change(id, x => x with { State = "Interrupted", ErrorCategory = category }); }
        catch (Exception) { /* Inactive Running is also conservatively exposed as Interrupted by the read API. */ }
    }
    // Deliberately do not resume on host startup. Browser close is harmless, host restart requires intent.
    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken token)
    {
        Task? task;
        lock (gate) { shutdown.Cancel(); task = running; }
        if (task is not null) { try { await task.WaitAsync(token); } catch (OperationCanceledException) { } }
    }
}
