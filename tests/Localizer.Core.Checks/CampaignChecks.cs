using Localizer.Core;

internal static class CampaignChecks
{
    internal static void Run(string root, Action<string, Action> check)
    {
        var language = TargetLanguage.SimplifiedChinese;
        var key = new MediaKey("campaign-server", "campaign-library", "campaign-movie");
        var scope = new LibraryKey(key.ServerId, key.LibraryId);
        var spec = new GenerationSpec("https://provider.example/v1", "test", "test", "test", "{}", "{}", "Translate this source.");
        (CandidateStore Store, SourceSnapshot Source, Candidate Candidate) Fresh()
        {
            var store = new CandidateStore(Path.Combine(root, "campaign-" + Guid.NewGuid().ToString("N") + ".sqlite3"));
            var source = store.ObserveSource(key, "元の題名", "DEMO · ");
            return (store, source, store.SaveGenerated(source.Id, language, "中文标题", spec));
        }
        check("campaign_automatic_accept_is_idempotent_revision_two", () =>
        {
            var (store, source, candidate) = Fresh();
            var accepted = store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id);
            var replay = store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id);
            Expect(accepted.Approved && accepted.Revision == 2 && !accepted.IsHumanEdited && accepted == replay);
            Expect(store.GetSelected(source.Id, language)!.Revision == 1);
        });
        check("campaign_human_edit_is_preserved_and_cannot_be_autoaccepted", () =>
        {
            var (store, source, candidate) = Fresh();
            var edited = store.Edit(candidate.Id, candidate.Revision, "人工修订");
            Throws<RevisionConflictException>(() => store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id));
            Expect(store.GetCandidate(candidate.Id) == edited && !edited.Approved);
        });
        check("campaign_manual_edit_after_autoaccept_is_preserved", () =>
        {
            var (store, source, candidate) = Fresh();
            var accepted = store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id);
            var edited = store.Edit(candidate.Id, accepted.Revision, "后续修订");
            Throws<RevisionConflictException>(() => store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id));
            Expect(store.GetCandidate(candidate.Id) == edited);
        });
        check("campaign_alternative_candidate_blocks_automatic_accept", () =>
        {
            var (store, source, candidate) = Fresh();
            var alternative = store.SaveGenerated(source.Id, language, "另一候选", spec with { Model = "other" });
            Throws<RevisionConflictException>(() => store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id));
            Expect(!store.GetCandidate(candidate.Id).Approved && !store.GetCandidate(alternative.Id).Approved);
        });
        check("campaign_selection_roundtrip_cannot_restore_autoaccept_eligibility", () =>
        {
            var (store, source, candidate) = Fresh();
            var alternative = store.SaveGenerated(source.Id, language, "另一候选", spec with { Model = "other" });
            var second = store.Select(alternative.Id, 1);
            var third = store.Select(candidate.Id, second.Revision);
            Throws<RevisionConflictException>(() => store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id));
            Expect(third.Revision == 3 && !store.GetCandidate(candidate.Id).Approved);
        });
        check("campaign_stale_source_never_accepts_historical_candidate", () =>
        {
            var (store, source, candidate) = Fresh();
            var changed = store.ObserveSource(key, "変更された題名", source.DisplayPrefix);
            Throws<SourceChangedException>(() => store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id));
            Expect(store.GetCurrentSource(key)!.Id == changed.Id && !store.GetCandidate(candidate.Id).Approved);
        });
        check("campaign_pending_name_write_owns_item_before_acceptance_replay", () =>
        {
            var (store, source, candidate) = Fresh();
            store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id);
            var movie = new ObservedMovie(key, true, false, source.OriginalTitle, source.DisplayPrefix, source.DisplayPrefix + source.OriginalTitle);
            var preview = new LanguagePreview(store).Build(scope, language, [movie]).Single();
            var target = new ReadOnlyTarget(new(movie, true));
            var service = new NameOperationService(store, target, (point, _) =>
            { if (point == OperationCheckpoint.IntentSaved) throw new TestInterruption(); });
            Throws<TestInterruption>(() => service.ApplyAsync(Guid.NewGuid().ToString("N"), scope, preview).GetAwaiter().GetResult());
            Throws<OperationBusyException>(() => store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id));
            Expect(target.Writes == 0 && store.ListUnresolvedOperations().Count == 1);
        });
        check("campaign_other_language_candidate_is_independent", () =>
        {
            var (store, source, candidate) = Fresh();
            var english = store.SaveGenerated(source.Id, TargetLanguage.English, "English title", spec);
            var accepted = store.AcceptCampaignCandidate(key, language, source.Id, candidate.Id);
            Expect(accepted.Approved && !store.GetCandidate(english.Id).Approved);
        });
        check("campaign_unknown_response_stays_reserved_after_job_cancellation", () =>
        {
            var store = new CandidateStore(Path.Combine(root, "campaign-" + Guid.NewGuid().ToString("N") + ".sqlite3"));
            store.ObserveSource(key, "元の題名", "");
            var job = store.CreateTranslationJob(Guid.NewGuid().ToString("N"), scope, language, [new(key, spec)], new(1, 0));
            Expect(store.CampaignTranslationReservations(scope, language).Count == 0);
            var queue = new TranslationQueue(store, new UnknownProvider());
            queue.RunAsync(job.Id).GetAwaiter().GetResult();
            Expect(store.CampaignTranslationReservations(scope, language)[key.ItemId].Single() == job.Id);
            store.RequestTranslationCancel(job.Id);
            queue.RunAsync(job.Id).GetAwaiter().GetResult();
            Expect(store.GetTranslationJob(job.Id).State == TranslationJobState.Cancelled);
            Expect(store.CampaignTranslationReservations(scope, language)[key.ItemId].Single() == job.Id);
            Expect(store.CampaignTranslationReservations(scope, TargetLanguage.English).Count == 0);
            Expect(store.CampaignTranslationReservations(scope with { LibraryId = "outside" }, language).Count == 0);
        });
    }

    private static void Expect(bool value) { if (!value) throw new InvalidOperationException("Campaign assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class TestInterruption : Exception;
    private sealed class ReadOnlyTarget(NameTargetSnapshot snapshot) : INameTarget
    {
        public int Writes { get; private set; }
        public ValueTask<NameTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token) => ValueTask.FromResult<NameTargetSnapshot?>(snapshot);
        public ValueTask<TargetWriteResult> WriteAsync(NameWriteRequest request, CancellationToken token)
        { Writes++; throw new InvalidOperationException("Test must stop at the intent checkpoint."); }
    }
    private sealed class UnknownProvider : ITitleProvider
    {
        public Task<TitleProviderResult> TranslateAsync(TitleProviderRequest request, CancellationToken token) =>
            throw new TitleProviderException(ProviderError.TransportUncertain);
    }
}
