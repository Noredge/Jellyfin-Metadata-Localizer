namespace Localizer.Core;

public sealed class GenreOperationService(CandidateStore store, IGenreTarget target,
    Action<OperationCheckpoint,GenreOperation>? checkpoint = null)
{
    public async Task<GenreOperation> ApplyAsync(string operationId, LibraryKey scope, GenrePreviewEntry preview, CancellationToken token=default)
    {
        using var lease=store.AcquireWriterLease();
        var fingerprint=CandidateStore.GenreApplyRequestFingerprint(scope,preview);
        var existing=store.FindGenreOperation(operationId);
        if (existing is not null) return SameRequest(existing,fingerprint);
        token.ThrowIfCancellationRequested();
        var current=await target.ReadAsync(preview.Key,token);
        var operation=store.PrepareGenreApply(operationId,scope,preview,current);
        checkpoint?.Invoke(OperationCheckpoint.IntentSaved,operation);
        return operation.State==OperationState.Prepared ? await ExecutePrepared(operation,token) : operation;
    }

    public async Task<GenreOperation> RestoreAsync(string operationId, LibraryKey scope, string applyOperationId,
        bool acceptUncertainAttribution=false, CancellationToken token=default)
    {
        using var lease=store.AcquireWriterLease();
        var fingerprint=CandidateStore.GenreRestoreRequestFingerprint(scope,applyOperationId,acceptUncertainAttribution);
        var existing=store.FindGenreOperation(operationId);
        if (existing is not null) return SameRequest(existing,fingerprint);
        token.ThrowIfCancellationRequested();
        var application=store.GetGenreOperation(applyOperationId);
        if (application.Key.ServerId!=scope.ServerId || application.Key.LibraryId!=scope.LibraryId)
            throw new OperationValidationException("LibraryScopeChanged");
        var current=await target.ReadAsync(application.Key,token);
        var operation=store.PrepareGenreRestore(operationId,scope,applyOperationId,acceptUncertainAttribution,current);
        checkpoint?.Invoke(OperationCheckpoint.IntentSaved,operation);
        return operation.State==OperationState.Prepared ? await ExecutePrepared(operation,token) : operation;
    }

    // Explicit recovery after the dispatcher is quiescent. This method NEVER writes to the target.
    public async Task<GenreOperation> ReconcileAsync(string operationId, CancellationToken token=default)
    {
        using var lease=store.AcquireWriterLease();
        var operation=store.GetGenreOperation(operationId);
        if (!IsPending(operation.State)) return operation;
        return await Observe(operation,false,null,token);
    }

    // Resume is deliberate. Merely reopening the store or reading an operation cannot restart a write.
    public async Task<GenreOperation> ResumeAsync(string operationId, LibraryKey scope, CancellationToken token=default)
    {
        using var lease=store.AcquireWriterLease();
        var operation=store.GetGenreOperation(operationId);
        if (operation.Key.ServerId!=scope.ServerId || operation.Key.LibraryId!=scope.LibraryId)
            throw new OperationValidationException("LibraryScopeChanged");
        if (!IsPending(operation.State)) return operation;
        operation=await Observe(operation,false,null,token);
        return operation.State==OperationState.Retryable ? await ExecutePrepared(operation,token) : operation;
    }

    public async Task<GenreOperation> CancelPendingAsync(string operationId, CancellationToken token=default)
    {
        using var lease=store.AcquireWriterLease();
        var operation=store.GetGenreOperation(operationId);
        if (!IsPending(operation.State)) return operation;
        operation=await Observe(operation,false,null,token);
        return operation.State==OperationState.Retryable ?
            store.CompleteGenreOperation(operation.Id,OperationState.Cancelled,false,"CancelledAfterObservedBeforeValue") : operation;
    }

    private async Task<GenreOperation> ExecutePrepared(GenreOperation operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var localFailure=store.ValidateGenreOperation(operation.Id);
        if (localFailure is not null) return store.CompleteGenreOperation(operation.Id,OperationState.Blocked,false,localFailure);
        GenreTargetSnapshot? current;
        try { current=await target.ReadAsync(operation.Key,token); }
        catch (Exception) when (!token.IsCancellationRequested) { return store.NoteGenreOperationError(operation.Id,"TargetReadFailed"); }
        var failure=TargetFailure(operation,current);
        if (failure is not null) return store.CompleteGenreOperation(operation.Id,OperationState.Blocked,false,failure);
        if (GenreValues.Equal(current!.Values,operation.PlannedValues))
            return store.CompleteGenreOperation(operation.Id,OperationState.ObservedExpected,true,"ExpectedValueObservedWithoutWrite");
        if (!GenreValues.Equal(current.Values,operation.BeforeValues))
            return store.CompleteGenreOperation(operation.Id,OperationState.Conflict,false,"CurrentValueChanged");
        token.ThrowIfCancellationRequested();
        operation=store.MarkGenreWriting(operation.Id);
        checkpoint?.Invoke(OperationCheckpoint.WriteMarked,operation);
        TargetWriteResult result;
        try
        {
            result=await target.WriteAsync(new(operation.Key,operation.BeforeValues,operation.PlannedValues),token);
        }
        catch (Exception)
        {
            // A failed/cancelled call can already have taken effect. Persist uncertainty before a bounded readback.
            store.NoteGenreOperationError(operation.Id,token.IsCancellationRequested ? "WriteCancelledOutcomeUnknown" : "WriteFailedOutcomeUnknown");
            using var readback=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await Observe(operation,false,"WriteOutcomeUnknown",readback.Token);
        }
        checkpoint?.Invoke(OperationCheckpoint.TargetReturned,operation);
        if (result==TargetWriteResult.Conflict) return store.CompleteGenreOperation(operation.Id,OperationState.Conflict,false,"AdapterValueConflict");
        if (result==TargetWriteResult.Blocked) return store.CompleteGenreOperation(operation.Id,OperationState.Blocked,false,"AdapterGuardBlocked");
        using var verify=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await Observe(operation,result==TargetWriteResult.Written,null,verify.Token);
    }

    private async Task<GenreOperation> Observe(GenreOperation operation, bool acknowledgedWrite, string? error, CancellationToken token)
    {
        GenreTargetSnapshot? current;
        try { current=await target.ReadAsync(operation.Key,token); }
        catch (Exception) { return store.NoteGenreOperationError(operation.Id,"ReadbackUnavailable"); }
        // Observation is read-only: a newly enabled lock or Saver must not hide a completed write.
        if (current is null || current.Key!=operation.Key || !current.IsMovie)
            return store.NoteGenreOperationError(operation.Id,"ReadbackItemUnavailable");
        var failure=TargetFailure(operation,current);
        var value=current.Values;
        var state=GenreValues.Equal(value,operation.PlannedValues) ? acknowledgedWrite ? OperationState.Applied : OperationState.ObservedExpected :
            GenreValues.Equal(value,operation.BeforeValues) ? OperationState.Retryable : OperationState.Conflict;
        checkpoint?.Invoke(OperationCheckpoint.BeforeCompletion,operation);
        return store.CompleteGenreOperation(operation.Id,state,state==OperationState.ObservedExpected,
            state==OperationState.Conflict ? "UnexpectedCurrentValue" : error ?? (failure is null ? null : "PostWriteGuard:"+failure));
    }

    internal static string? TargetFailure(GenreOperation operation, GenreTargetSnapshot? current)
    {
        if (current is null) return "ItemMissing";
        if (current.Key!=operation.Key || !current.IsMovie) return "ItemOutOfScope";
        if (current.Locked) return "GenresLocked";
        if (!current.MetadataSaversExplicitlyDisabled) return "UnsupportedSaverConfiguration";
        return null;
    }
    internal static bool IsPending(OperationState state) => state is OperationState.Prepared or OperationState.Writing or OperationState.Retryable;
    private static GenreOperation SameRequest(GenreOperation existing, string fingerprint) => existing.RequestFingerprint==fingerprint ? existing : throw new IdempotencyConflictException();
}
