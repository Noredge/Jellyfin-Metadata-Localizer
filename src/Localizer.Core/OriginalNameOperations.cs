using System.Text.Json;

namespace Localizer.Core;

public sealed partial class CandidateStore
{
    internal static string OriginalRequestFingerprint(LibraryKey scope, MediaKey key, long sourceId, string expectedDisplay)
        => Identity.Hash(JsonSerializer.Serialize(new { Kind = "Original", scope, key, sourceId, expectedDisplay }));

    internal NameOperation PrepareOriginal(string id, LibraryKey scope, MediaKey key, long sourceId,
        string expectedDisplay, NameTargetSnapshot? current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id); key.Validate();
        if (key.ServerId != scope.ServerId || key.LibraryId != scope.LibraryId)
            throw new OperationValidationException("LibraryScopeChanged");
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var fingerprint = OriginalRequestFingerprint(scope, key, sourceId, expectedDisplay);
        if (FindOperation(db, tx, id) is { } existing)
            return existing.RequestFingerprint == fingerprint ? existing : throw new IdempotencyConflictException();
        EnsureNotBusy(db, tx, key); EnsureGenreNotBusy(db, tx, key);
        var source = CurrentSource(db, tx, key);
        if (source is null || source.Id != sourceId) throw new OperationValidationException("SourceChanged");
        if (string.IsNullOrWhiteSpace(source.OriginalTitle)) throw new OperationValidationException("OriginalTitleMissing");
        // Language is a legacy journal field; OriginalDisplay means it does not select a translation.
        var operation = new NameOperation(id, fingerprint, key, OperationKind.Apply, OperationState.Prepared,
            source.Id, null, null, null, TargetLanguage.SimplifiedChinese, expectedDisplay,
            source.DisplayPrefix + source.OriginalTitle, source.OriginalTitle, source.DisplayPrefix,
            null, false, false, null, Now(), Now(), OriginalDisplay: true);
        var failure = NameOperationService.TargetFailure(operation, current);
        if (failure is not null) operation = operation with { State = OperationState.Blocked, ErrorCategory = failure };
        else if (current!.Movie.CurrentName != expectedDisplay)
            operation = operation with { State = OperationState.Conflict, ErrorCategory = "PreviewCurrentValueChanged" };
        else if (current.Movie.CurrentName == operation.PlannedValue) operation = operation with { State = OperationState.NoChange };
        InsertOperation(db, tx, operation); tx.Commit(); return operation;
    }
}
