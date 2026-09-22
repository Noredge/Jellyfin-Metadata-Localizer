using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Localizer.Core;

public sealed partial class CandidateStore
{
    internal static string GenreApplyRequestFingerprint(LibraryKey scope, GenrePreviewEntry preview) =>
        Identity.Hash(JsonSerializer.Serialize(new { Field = "Genres", Kind = "Apply", scope, preview }));
    internal static string GenreRestoreRequestFingerprint(LibraryKey scope, string applicationId, bool accepted) =>
        Identity.Hash(JsonSerializer.Serialize(new { Field = "Genres", Kind = "Restore", scope, applicationId, accepted }));
    public GenreOperation? FindGenreOperation(string id) { using var db = Open(); return FindGenreOperation(db, null, id); }
    public GenreOperation GetGenreOperation(string id) => FindGenreOperation(id) ?? throw new KeyNotFoundException("Genre operation does not exist.");
    public IReadOnlyList<GenreOperation> ListGenreOperations(MediaKey key, int limit = 20)
    {
        if (limit is < 1 or > 100) throw new ArgumentException("Invalid operation list limit.");
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT payload_json FROM genre_operations WHERE server_id=$s AND library_id=$l AND item_id=$i
            ORDER BY rowid DESC LIMIT $n;
            """, ("$s", key.ServerId), ("$l", key.LibraryId), ("$i", key.ItemId), ("$n", limit));
        using var r = cmd.ExecuteReader(); var records = new List<GenreOperation>();
        while (r.Read()) records.Add(JsonSerializer.Deserialize<GenreOperation>(r.GetString(0))!);
        return records;
    }
    public GenreRestorePoint? GetGenreRestorePoint(MediaKey key) { using var db = Open(); return GenreRestoreHead(db, null, key); }
    public IReadOnlyList<GenreOperation> ListUnresolvedGenreOperations()
    {
        using var db = Open();
        using var cmd = Command(db, null, "SELECT payload_json FROM genre_operations WHERE state IN ('Prepared','Writing','Retryable') ORDER BY rowid;");
        using var r = cmd.ExecuteReader(); var records = new List<GenreOperation>();
        while (r.Read()) records.Add(JsonSerializer.Deserialize<GenreOperation>(r.GetString(0))!);
        return records;
    }
    internal GenreOperation PrepareGenreApply(string id, LibraryKey scope, GenrePreviewEntry preview, GenreTargetSnapshot? current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (preview.Status is not (PreviewStatus.ReadyForReview or PreviewStatus.AlreadyMatches)) throw new OperationValidationException("PreviewNotReady");
        if (preview.Key.ServerId != scope.ServerId || preview.Key.LibraryId != scope.LibraryId) throw new OperationValidationException("LibraryScopeChanged");
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var fingerprint = GenreApplyRequestFingerprint(scope, preview);
        var existing = FindGenreOperation(db, tx, id);
        if (existing is not null) return existing.RequestFingerprint == fingerprint ? existing : throw new IdempotencyConflictException();
        EnsureGenreNotBusy(db, tx, preview.Key);
        EnsureNotBusy(db, tx, preview.Key);
        var source = CurrentGenreSource(db, tx, preview.Key);
        if (source is null || source.Id != preview.SourceId) throw new OperationValidationException("GenreSourceChanged");
        var mapping = preview.Original
            ? new GenreMapping(Identity.Hash(JsonSerializer.Serialize(source)), source.Id, preview.Language, source.OriginalValues, [])
            : ComputeGenreMapping(db, tx, source, preview.Language);
        if (mapping.Id != preview.MappingId) throw new OperationValidationException("GenreDictionaryChanged");
        if (!mapping.Values.SequenceEqual(preview.ProposedValues, StringComparer.Ordinal)
            || !mapping.UnknownValues.SequenceEqual(preview.UnknownValues, StringComparer.Ordinal))
            throw new OperationValidationException("GenrePreviewChanged");
        var operation = new GenreOperation(id, fingerprint, preview.Key, OperationKind.Apply, OperationState.Prepared,
            source.Id, mapping.Id, preview.Language, GenreValues.Copy(current?.Values ?? preview.ExpectedValues),
            GenreValues.Copy(mapping.Values), null, false, false, null, Now(), Now(), preview.Original);
        var failure = GenreOperationService.TargetFailure(operation, current);
        if (failure is not null) operation = operation with { State = OperationState.Blocked, ErrorCategory = failure };
        else if (GenreValues.Equal(current!.Values, operation.PlannedValues)) operation = operation with { State = OperationState.NoChange };
        else if (!GenreValues.Equal(current.Values, preview.ExpectedValues))
            operation = operation with { State = OperationState.Conflict, ErrorCategory = "PreviewCurrentValueChanged" };
        InsertGenreOperation(db, tx, operation); tx.Commit(); return operation;
    }
    internal GenreOperation PrepareGenreRestore(string id, LibraryKey scope, string parentId, bool accepted, GenreTargetSnapshot? current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var parent = FindGenreOperation(db, tx, parentId) ?? throw new KeyNotFoundException("Genre application not found.");
        if (parent.Key.ServerId != scope.ServerId || parent.Key.LibraryId != scope.LibraryId) throw new OperationValidationException("LibraryScopeChanged");
        var fingerprint = GenreRestoreRequestFingerprint(scope, parentId, accepted);
        var existing = FindGenreOperation(db, tx, id);
        if (existing is not null) return existing.RequestFingerprint == fingerprint ? existing : throw new IdempotencyConflictException();
        EnsureGenreNotBusy(db, tx, parent.Key);
        EnsureNotBusy(db, tx, parent.Key);
        var head = GenreRestoreHead(db, tx, parent.Key);
        if (parent.Kind != OperationKind.Apply || head?.Application.Id != parent.Id) throw new OperationValidationException("RestorePointSupersededOrMissing");
        if (parent.ObservedOnly && !accepted) throw new OperationValidationException("UncertainAttributionNeedsReview");
        var operation = new GenreOperation(id, fingerprint, parent.Key, OperationKind.Restore, OperationState.Prepared,
            parent.SourceId, null, parent.Language, GenreValues.Copy(parent.PlannedValues), GenreValues.Copy(parent.BeforeValues),
            parent.Id, accepted, false, null, Now(), Now());
        var failure = GenreOperationService.TargetFailure(operation, current);
        if (failure is not null) operation = operation with { State = OperationState.Blocked, ErrorCategory = failure };
        else if (head!.Consumed || GenreValues.Equal(current!.Values, operation.PlannedValues)) operation = operation with { State = OperationState.NoChange };
        else if (!GenreValues.Equal(current.Values, operation.BeforeValues)) operation = operation with { State = OperationState.Conflict, ErrorCategory = "CurrentValueChangedAfterApply" };
        InsertGenreOperation(db, tx, operation);
        if (operation.State == OperationState.NoChange) UpdateGenreRestoreHead(db, tx, operation);
        tx.Commit(); return operation;
    }
    internal string? ValidateGenreOperation(string id)
    { using var db = Open(); using var tx = db.BeginTransaction(deferred: false); return ValidateGenreOperation(db, tx, FindGenreOperation(db, tx, id)!); }
    private static string? ValidateGenreOperation(SqliteConnection db, SqliteTransaction tx, GenreOperation operation)
    {
        var source = CurrentGenreSource(db, tx, operation.Key);
        if (source?.Id != operation.SourceId) return "GenreSourceChanged";
        if (operation.Kind == OperationKind.Restore)
        {
            var head = GenreRestoreHead(db, tx, operation.Key);
            return head is null || head.Application.Id != operation.ParentApplyId || head.Consumed ? "RestorePointSupersededOrConsumed" : null;
        }
        if (operation.Original) return GenreValues.Equal(source.OriginalValues, operation.PlannedValues)
            && Identity.Hash(JsonSerializer.Serialize(source)) == operation.MappingId ? null : "GenreSourceChanged";
        return ComputeGenreMapping(db, tx, source, operation.Language).Id == operation.MappingId ? null : "GenreDictionaryChanged";
    }
    internal GenreOperation MarkGenreWriting(string id)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var operation = FindGenreOperation(db, tx, id)!;
        if (!GenreOperationService.IsPending(operation.State) || operation.State == OperationState.Writing) throw new OperationBusyException();
        var failure = ValidateGenreOperation(db, tx, operation);
        if (failure is not null) throw new OperationValidationException(failure);
        operation = operation with { State = OperationState.Writing, ErrorCategory = null, UpdatedUtc = Now() };
        SaveGenreOperation(db, tx, operation); tx.Commit(); return operation;
    }
    internal GenreOperation NoteGenreOperationError(string id, string category)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var operation = FindGenreOperation(db, tx, id)!;
        operation = operation with { ErrorCategory = category, UpdatedUtc = Now() };
        SaveGenreOperation(db, tx, operation); tx.Commit(); return operation;
    }
    internal GenreOperation CompleteGenreOperation(string id, OperationState state, bool observedOnly, string? error)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var operation = FindGenreOperation(db, tx, id)!;
        if (!GenreOperationService.IsPending(operation.State)) return operation;
        operation = operation with { State = state, ObservedOnly = observedOnly, ErrorCategory = error, UpdatedUtc = Now() };
        SaveGenreOperation(db, tx, operation);
        if (state is OperationState.Applied or OperationState.ObservedExpected) UpdateGenreRestoreHead(db, tx, operation);
        tx.Commit(); return operation;
    }
    private static void UpdateGenreRestoreHead(SqliteConnection db, SqliteTransaction tx, GenreOperation operation)
    {
        if (operation.Kind == OperationKind.Apply)
            Execute(db, tx, """
                INSERT INTO genre_restore_heads VALUES($s,$l,$i,$id,0)
                ON CONFLICT(server_id,library_id,item_id) DO UPDATE SET apply_id=excluded.apply_id,consumed=0;
                """, ("$s", operation.Key.ServerId), ("$l", operation.Key.LibraryId), ("$i", operation.Key.ItemId), ("$id", operation.Id));
        else Execute(db, tx, "UPDATE genre_restore_heads SET consumed=1 WHERE server_id=$s AND library_id=$l AND item_id=$i AND apply_id=$parent;",
            ("$s", operation.Key.ServerId), ("$l", operation.Key.LibraryId), ("$i", operation.Key.ItemId), ("$parent", operation.ParentApplyId));
    }
    private static GenreRestorePoint? GenreRestoreHead(SqliteConnection db, SqliteTransaction? tx, MediaKey key)
    {
        using var cmd = Command(db, tx, """
            SELECT o.payload_json,h.consumed FROM genre_restore_heads h JOIN genre_operations o ON o.id=h.apply_id
            WHERE h.server_id=$s AND h.library_id=$l AND h.item_id=$i;
            """, ("$s", key.ServerId), ("$l", key.LibraryId), ("$i", key.ItemId));
        using var r = cmd.ExecuteReader(); return r.Read() ? new(JsonSerializer.Deserialize<GenreOperation>(r.GetString(0))!, r.GetBoolean(1)) : null;
    }
    private static GenreOperation? FindGenreOperation(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        var json = Scalar(db, tx, "SELECT payload_json FROM genre_operations WHERE id=$id;", ("$id", id)) as string;
        return json is null ? null : JsonSerializer.Deserialize<GenreOperation>(json)!;
    }
    private static void InsertGenreOperation(SqliteConnection db, SqliteTransaction tx, GenreOperation operation) =>
        Execute(db, tx, "INSERT INTO genre_operations VALUES($id,$s,$l,$i,$state,$j);", ("$id", operation.Id),
            ("$s", operation.Key.ServerId), ("$l", operation.Key.LibraryId), ("$i", operation.Key.ItemId),
            ("$state", operation.State.ToString()), ("$j", JsonSerializer.Serialize(operation)));
    private static void SaveGenreOperation(SqliteConnection db, SqliteTransaction tx, GenreOperation operation) =>
        Execute(db, tx, "UPDATE genre_operations SET state=$state,payload_json=$j WHERE id=$id;", ("$id", operation.Id),
            ("$state", operation.State.ToString()), ("$j", JsonSerializer.Serialize(operation)));
}
