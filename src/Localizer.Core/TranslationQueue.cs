namespace Localizer.Core;

/// <summary>Explicit serial runner. Opening a store, listing jobs or restarting cannot send a request.</summary>
public sealed class TranslationQueue(CandidateStore store, ITitleProvider provider,
    Action<TranslationCheckpoint, TranslationItem>? checkpoint = null)
{
    public TranslationJob Reconcile(string id)
    {
        using var lease = store.AcquireTranslationLease();
        return ReconcileCore(id);
    }
    private TranslationJob ReconcileCore(string id)
    {
        var job = store.GetTranslationJob(id);
        foreach (var item in store.GetTranslationItems(id).Where(x => x.State == TranslationItemState.Requesting))
            store.SaveTranslationItem(item with { State = TranslationItemState.Uncertain, ErrorCategory = "InterruptedRequestOutcomeUnknown" });
        return job.State == TranslationJobState.Running ? store.SetTranslationJob(id, TranslationJobState.Interrupted, "WorkerInterrupted") : job;
    }
    public async Task<TranslationJob> RunAsync(string id, TranslationResume? resume = null, CancellationToken token = default)
    {
        using var lease = store.AcquireTranslationLease();
        resume ??= new();
        var job = ReconcileCore(id);
        if (job.State == TranslationJobState.CancelRequested) return store.SetTranslationJob(id, TranslationJobState.Cancelled);
        if (job.State == TranslationJobState.Paused && !resume.RetryFailed && !resume.AcceptUncertainRequest) return job;
        foreach (var item in store.GetTranslationItems(id))
        {
            var retry = (item.State == TranslationItemState.Uncertain && resume.AcceptUncertainRequest)
                || (item.State == TranslationItemState.Failed && resume.RetryFailed);
            if (retry && item.Attempts < job.Policy.MaxAttempts)
                store.SaveTranslationItem(item with { State = TranslationItemState.Pending, ErrorCategory = null });
        }
        if (store.GetTranslationItems(id).Any(x => x.State == TranslationItemState.Uncertain))
            return store.SetTranslationJob(id, TranslationJobState.Paused, "RequestOutcomeUnknownNeedsReview");
        if (job.State == TranslationJobState.Paused && store.GetTranslationItems(id).Any(x => x.State == TranslationItemState.Failed))
            return store.SetTranslationJob(id, TranslationJobState.Paused, "AttemptBudgetExhausted");
        store.SetTranslationJob(id, TranslationJobState.Running);
        while (true)
        {
            if (Cancelled(id, token)) return store.SetTranslationJob(id, TranslationJobState.Cancelled);
            var item = store.GetTranslationItems(id).FirstOrDefault(x => x.State is TranslationItemState.Pending or TranslationItemState.Received);
            if (item is null)
            {
                var errors = store.GetTranslationItems(id).Any(x => x.State is not (TranslationItemState.Succeeded or TranslationItemState.Cached));
                return store.SetTranslationJob(id, errors ? TranslationJobState.CompletedWithErrors : TranslationJobState.Completed);
            }
            if (item.State == TranslationItemState.Received) { FinishReceived(job, item); continue; }
            if (store.GetCurrentSource(item.Source.Key)?.Id != item.Source.Id)
            { store.SaveTranslationItem(item with { State = TranslationItemState.SourceChanged, ErrorCategory = "SourceChanged" }); continue; }
            var cached = store.FindTranslationCandidate(item.Source, job.Language, item.Spec);
            if (cached is not null)
            { store.SaveTranslationItem(item with { State = TranslationItemState.Cached, CandidateId = cached.Id }); continue; }
            if (job.Policy.Adaptive && store.GetTranslationJob(id).NextRequestUtc is { } scheduled
                && DateTimeOffset.Parse(scheduled) - DateTimeOffset.UtcNow > TimeSpan.FromMilliseconds(job.Policy.MaxAutoWaitMs))
                return store.SetTranslationJob(id, TranslationJobState.Paused, "QuotaExceeded");
            if (!await WaitForSlot(id, token)) return store.SetTranslationJob(id, TranslationJobState.Cancelled);
            if (store.GetCurrentSource(item.Source.Key)?.Id != item.Source.Id)
            { store.SaveTranslationItem(item with { State = TranslationItemState.SourceChanged, ErrorCategory = "SourceChanged" }); continue; }
            if (item.Attempts >= job.Policy.MaxAttempts)
                return store.SetTranslationJob(id, TranslationJobState.Paused, "AttemptBudgetExhausted");
            item = item with { State = TranslationItemState.Requesting, Attempts = item.Attempts + 1 };
            store.SaveTranslationItem(item);
            var providerRequest = new TitleProviderRequest(item.Source, job.Language, item.Spec);
            store.SetTranslationJob(id, TranslationJobState.Running, nextRequestUtc: DateTimeOffset.UtcNow.Add(job.Policy.RequestSpacing(providerRequest)).ToString("O"));
            checkpoint?.Invoke(TranslationCheckpoint.RequestSaved, item);
            if (Cancelled(id, token))
            {
                store.SaveTranslationItem(item with { State = TranslationItemState.Pending, Attempts = item.Attempts - 1 });
                return store.SetTranslationJob(id, TranslationJobState.Cancelled);
            }
            TitleProviderResult result;
            using var timeout = new CancellationTokenSource(job.Policy.TimeoutMs);
            using var requestToken = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            try
            {
                var request = provider.TranslateAsync(providerRequest, requestToken.Token);
                while (!request.IsCompleted)
                {
                    await Task.WhenAny(request, Task.Delay(50, CancellationToken.None));
                    if (Cancelled(id, token)) { requestToken.Cancel(); break; }
                }
                result = await request;
            }
            catch (TitleProviderException error)
            {
                var diagnosticResult = new TitleProviderResult(null, error.Category.ToString(), [], Diagnostics: error.Diagnostics);
                if (error.Category is ProviderError.RateLimited or ProviderError.QuotaExceeded)
                {
                    var delay = error.RetryAfter ?? TimeSpan.FromMilliseconds(job.Policy.RetryDelayMs);
                    if (error.Diagnostics is { } d)
                    {
                        // The exhausted bucket identifies which reset applies; request and token resets are not interchangeable.
                        if (d.RequestsRemaining is 0 && d.RequestResetMs is { } rr) delay = Max(delay, TimeSpan.FromMilliseconds(rr));
                        if (d.TokensRemaining is 0 && d.TokenResetMs is { } tr) delay = Max(delay, TimeSpan.FromMilliseconds(tr));
                        if (error.RetryAfter is null && d.TokensRemaining is > 0 && d.TokenResetMs is { } partialReset)
                            delay = Max(delay, TimeSpan.FromMilliseconds(partialReset));
                    }
                    var category = error.Category == ProviderError.QuotaExceeded || (job.Policy.Adaptive && delay.TotalMilliseconds > job.Policy.MaxAutoWaitMs)
                        ? "QuotaExceeded" : "RateLimited";
                    var retry = category == "RateLimited" && item.Attempts < job.Policy.MaxAttempts && delay.TotalMilliseconds <= job.Policy.MaxAutoWaitMs;
                    store.SaveTranslationItem(item with { State = retry ? TranslationItemState.Pending : TranslationItemState.Failed,
                        ErrorCategory = category, Result = diagnosticResult with { ErrorCategory = category } });
                    var next = DateTimeOffset.UtcNow.Add(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
                    var interval = DateTimeOffset.Parse(store.GetTranslationJob(id).NextRequestUtc!);
                    if (next < interval) next = interval;
                    store.SetTranslationJob(id, retry ? TranslationJobState.Running : TranslationJobState.Paused, category, next.ToString("O"));
                    if (retry) continue;
                    return store.GetTranslationJob(id);
                }
                var uncertain = error.Category == ProviderError.TransportUncertain;
                store.SaveTranslationItem(item with { State = uncertain ? TranslationItemState.Uncertain : TranslationItemState.Failed,
                    ErrorCategory = error.Category.ToString(), Result = diagnosticResult });
                if (error.Category == ProviderError.RequestRejected) continue;
                return store.SetTranslationJob(id, Cancelled(id, token) ? TranslationJobState.Cancelled : TranslationJobState.Paused, error.Category.ToString());
            }
            catch (Exception)
            {
                store.SaveTranslationItem(item with { State = TranslationItemState.Uncertain,
                    ErrorCategory = timeout.IsCancellationRequested ? "TimeoutOutcomeUnknown" : "RequestOutcomeUnknown" });
                return store.SetTranslationJob(id, Cancelled(id, token) ? TranslationJobState.Cancelled : TranslationJobState.Paused, "RequestOutcomeUnknown");
            }
            item = item with { State = TranslationItemState.Received, Result = result };
            store.SaveTranslationItem(item); // Persist response before candidate insertion; no repeated provider call after this point.
            if (job.Policy.Adaptive)
                store.SetTranslationJob(id, TranslationJobState.Running,
                    nextRequestUtc: DateTimeOffset.UtcNow.Add(job.Policy.RequestSpacing(providerRequest, result.Diagnostics)).ToString("O"));
            checkpoint?.Invoke(TranslationCheckpoint.ResponseSaved, item);
            FinishReceived(job, item);
        }
    }
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
    private void FinishReceived(TranslationJob job, TranslationItem item)
    {
        var result = item.Result!;
        if (result.ErrorCategory is not null || string.IsNullOrWhiteSpace(result.Title))
        { store.SaveTranslationItem(item with { State = TranslationItemState.Invalid, ErrorCategory = result.ErrorCategory ?? "InvalidProviderResult" }); return; }
        var candidate = store.SaveGenerated(item.Source.Id, job.Language, result.Title, item.Spec);
        checkpoint?.Invoke(TranslationCheckpoint.CandidateSaved, item);
        var changed = store.GetCurrentSource(item.Source.Key)?.Id != item.Source.Id;
        store.SaveTranslationItem(item with { State = changed ? TranslationItemState.SourceChanged : TranslationItemState.Succeeded,
            CandidateId = candidate.Id, ErrorCategory = changed ? "LateResultForHistoricalSource" : null });
    }
    private bool Cancelled(string id, CancellationToken token) => token.IsCancellationRequested || store.GetTranslationJob(id).State == TranslationJobState.CancelRequested;
    private async Task<bool> WaitForSlot(string id, CancellationToken token)
    {
        while (true)
        {
            if (Cancelled(id, token)) return false;
            var due = store.GetTranslationJob(id).NextRequestUtc;
            var delay = due is null ? TimeSpan.Zero : DateTimeOffset.Parse(due) - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero) return true;
            await Task.Delay(delay > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : delay, CancellationToken.None);
        }
    }
}
