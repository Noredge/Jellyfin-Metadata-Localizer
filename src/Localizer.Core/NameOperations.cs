namespace Localizer.Core;

public enum OperationKind { Apply, Restore }
public enum OperationState { Prepared, Writing, Retryable, Applied, ObservedExpected, NoChange, Conflict, Blocked, Cancelled }
public enum OperationCheckpoint { IntentSaved, WriteMarked, TargetReturned, BeforeCompletion }
// Conflict/Blocked mean the adapter rejected the operation BEFORE changing any target field.
public enum TargetWriteResult { Written, AlreadyMatches, Conflict, Blocked }

public sealed record NameTargetSnapshot(ObservedMovie Movie, bool MetadataSaversExplicitlyDisabled);
public sealed record NameWriteRequest(MediaKey Key, string ExpectedName, string ProposedName,
    string ExpectedOriginalTitle, string ExpectedDisplayPrefix);

/// <summary>The adapter must recheck scope, type, lock, source and Saver settings immediately before writing only Name.</summary>
public interface INameTarget
{
    ValueTask<NameTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken cancellationToken);
    ValueTask<TargetWriteResult> WriteAsync(NameWriteRequest request, CancellationToken cancellationToken);
}

public sealed record NameOperation(string Id, string RequestFingerprint, MediaKey Key, OperationKind Kind,
    OperationState State, long SourceId, string? CandidateId, long? CandidateRevision, long? SelectionRevision,
    TargetLanguage Language, string BeforeValue, string PlannedValue, string OriginalTitle, string DisplayPrefix,
    string? ParentApplyId, bool AcceptedUncertainAttribution, bool ObservedOnly, string? ErrorCategory,
    string CreatedUtc, string UpdatedUtc, bool OriginalDisplay = false);

public sealed record RestorePoint(NameOperation Application, bool Consumed);
public sealed class OperationBusyException() : InvalidOperationException("An unresolved operation already owns this item, or a writer is active.");
public sealed class IdempotencyConflictException() : InvalidOperationException("Operation ID was reused with a different request.");
public sealed class OperationValidationException(string category) : InvalidOperationException(category)
{
    public string Category { get; } = category;
}

/// <summary>Serial journaled Name operations. No provider calls; the injected adapter controls target I/O.</summary>
public sealed class NameOperationService(CandidateStore store, INameTarget target,
    Action<OperationCheckpoint,NameOperation>? checkpoint = null)
{
    public async Task<NameOperation> ApplyOriginalAsync(string operationId, LibraryKey scope, MediaKey key,
        long sourceId, string expectedDisplay, CancellationToken token = default)
    {
        using var lease = store.AcquireWriterLease();
        var fingerprint = CandidateStore.OriginalRequestFingerprint(scope, key, sourceId, expectedDisplay);
        if (store.FindOperation(operationId) is { } existing) return SameRequest(existing, fingerprint);
        token.ThrowIfCancellationRequested();
        var current = await target.ReadAsync(key, token);
        var operation = store.PrepareOriginal(operationId, scope, key, sourceId, expectedDisplay, current);
        checkpoint?.Invoke(OperationCheckpoint.IntentSaved, operation);
        return operation.State == OperationState.Prepared ? await ExecutePrepared(operation, token) : operation;
    }
    public async Task<NameOperation> ApplyAsync(string operationId, LibraryKey scope, LanguagePreviewEntry preview, CancellationToken token=default)
    {
        using var lease=store.AcquireWriterLease();
        var fingerprint=CandidateStore.ApplyRequestFingerprint(scope,preview);
        var existing=store.FindOperation(operationId);
        if (existing is not null) return SameRequest(existing,fingerprint);
        token.ThrowIfCancellationRequested();
        var current=await target.ReadAsync(preview.Key,token);
        var operation=store.PrepareApply(operationId,scope,preview,current);
        checkpoint?.Invoke(OperationCheckpoint.IntentSaved,operation);
        return operation.State==OperationState.Prepared ? await ExecutePrepared(operation,token) : operation;
    }

    public async Task<NameOperation> RestoreAsync(string operationId, LibraryKey scope, string applyOperationId,
        bool acceptUncertainAttribution=false, CancellationToken token=default)
    {
        using var lease=store.AcquireWriterLease();
        var fingerprint=CandidateStore.RestoreRequestFingerprint(scope,applyOperationId,acceptUncertainAttribution);
        var existing=store.FindOperation(operationId);
        if (existing is not null) return SameRequest(existing,fingerprint);
        token.ThrowIfCancellationRequested();
        var application=store.GetOperation(applyOperationId);
        if (application.Key.ServerId!=scope.ServerId || application.Key.LibraryId!=scope.LibraryId)
            throw new OperationValidationException("LibraryScopeChanged");
        var current=await target.ReadAsync(application.Key,token);
        var operation=store.PrepareRestore(operationId,scope,applyOperationId,acceptUncertainAttribution,current);
        checkpoint?.Invoke(OperationCheckpoint.IntentSaved,operation);
        return operation.State==OperationState.Prepared ? await ExecutePrepared(operation,token) : operation;
    }

    // Explicit recovery after the dispatcher is quiescent. This method NEVER writes to the target.
    public async Task<NameOperation> ReconcileAsync(string operationId, CancellationToken token=default)
    {
        using var lease=store.AcquireWriterLease();
        var operation=store.GetOperation(operationId);
        if (!IsPending(operation.State)) return operation;
        return await Observe(operation,false,null,token);
    }

    // Resume is deliberate. Merely reopening the store or reading an operation cannot restart a write.
    public async Task<NameOperation> ResumeAsync(string operationId, LibraryKey scope, CancellationToken token=default)
    {
        using var lease=store.AcquireWriterLease();
        var operation=store.GetOperation(operationId);
        if (operation.Key.ServerId!=scope.ServerId || operation.Key.LibraryId!=scope.LibraryId)
            throw new OperationValidationException("LibraryScopeChanged");
        if (!IsPending(operation.State)) return operation;
        operation=await Observe(operation,false,null,token);
        return operation.State==OperationState.Retryable ? await ExecutePrepared(operation,token) : operation;
    }

    public async Task<NameOperation> CancelPendingAsync(string operationId, CancellationToken token=default)
    {
        using var lease=store.AcquireWriterLease();
        var operation=store.GetOperation(operationId);
        if (!IsPending(operation.State)) return operation;
        operation=await Observe(operation,false,null,token);
        return operation.State==OperationState.Retryable ?
            store.CompleteOperation(operation.Id,OperationState.Cancelled,false,"CancelledAfterObservedBeforeValue") : operation;
    }

    private async Task<NameOperation> ExecutePrepared(NameOperation operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var localFailure=store.ValidateOperation(operation.Id);
        if (localFailure is not null) return store.CompleteOperation(operation.Id,OperationState.Blocked,false,localFailure);
        NameTargetSnapshot? current;
        try { current=await target.ReadAsync(operation.Key,token); }
        catch (Exception) when (!token.IsCancellationRequested) { return store.NoteOperationError(operation.Id,"TargetReadFailed"); }
        var failure=TargetFailure(operation,current);
        if (failure is not null) return store.CompleteOperation(operation.Id,OperationState.Blocked,false,failure);
        if (current!.Movie.CurrentName==operation.PlannedValue)
            return store.CompleteOperation(operation.Id,OperationState.ObservedExpected,true,"ExpectedValueObservedWithoutWrite");
        if (current.Movie.CurrentName!=operation.BeforeValue)
            return store.CompleteOperation(operation.Id,OperationState.Conflict,false,"CurrentValueChanged");
        token.ThrowIfCancellationRequested();
        operation=store.MarkWriting(operation.Id);
        checkpoint?.Invoke(OperationCheckpoint.WriteMarked,operation);
        TargetWriteResult result;
        try
        {
            result=await target.WriteAsync(new(operation.Key,operation.BeforeValue,operation.PlannedValue,
                operation.OriginalTitle,operation.DisplayPrefix),token);
        }
        catch (Exception)
        {
            // A failed/cancelled call can already have taken effect. Persist uncertainty before a bounded readback.
            store.NoteOperationError(operation.Id,token.IsCancellationRequested ? "WriteCancelledOutcomeUnknown" : "WriteFailedOutcomeUnknown");
            using var readback=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await Observe(operation,false,"WriteOutcomeUnknown",readback.Token);
        }
        checkpoint?.Invoke(OperationCheckpoint.TargetReturned,operation);
        if (result==TargetWriteResult.Conflict) return store.CompleteOperation(operation.Id,OperationState.Conflict,false,"AdapterValueConflict");
        if (result==TargetWriteResult.Blocked) return store.CompleteOperation(operation.Id,OperationState.Blocked,false,"AdapterGuardBlocked");
        using var verify=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await Observe(operation,result==TargetWriteResult.Written,null,verify.Token);
    }

    private async Task<NameOperation> Observe(NameOperation operation, bool acknowledgedWrite, string? error, CancellationToken token)
    {
        NameTargetSnapshot? current;
        try { current=await target.ReadAsync(operation.Key,token); }
        catch (Exception) { return store.NoteOperationError(operation.Id,"ReadbackUnavailable"); }
        // Observation is read-only: a newly enabled lock or Saver must not hide a completed write.
        if (current is null || current.Movie.Key!=operation.Key || !current.Movie.IsMovie)
            return store.NoteOperationError(operation.Id,"ReadbackItemUnavailable");
        var failure=TargetFailure(operation,current);
        var value=current.Movie.CurrentName;
        var state=value==operation.PlannedValue ? acknowledgedWrite ? OperationState.Applied : OperationState.ObservedExpected :
            value==operation.BeforeValue ? OperationState.Retryable : OperationState.Conflict;
        checkpoint?.Invoke(OperationCheckpoint.BeforeCompletion,operation);
        return store.CompleteOperation(operation.Id,state,state==OperationState.ObservedExpected,
            state==OperationState.Conflict ? "UnexpectedCurrentValue" : error ?? (failure is null ? null : "PostWriteGuard:"+failure));
    }

    internal static string? TargetFailure(NameOperation operation, NameTargetSnapshot? current)
    {
        if (current is null) return "ItemMissing";
        if (current.Movie.Key!=operation.Key || !current.Movie.IsMovie) return "ItemOutOfScope";
        if (current.Movie.NameLocked) return "NameLocked";
        if (!current.MetadataSaversExplicitlyDisabled) return "UnsupportedSaverConfiguration";
        if (current.Movie.OriginalTitle!=operation.OriginalTitle || current.Movie.DisplayPrefix!=operation.DisplayPrefix) return "SourceChanged";
        return null;
    }
    internal static bool IsPending(OperationState state) => state is OperationState.Prepared or OperationState.Writing or OperationState.Retryable;
    private static NameOperation SameRequest(NameOperation existing, string fingerprint) => existing.RequestFingerprint==fingerprint ? existing : throw new IdempotencyConflictException();
}
