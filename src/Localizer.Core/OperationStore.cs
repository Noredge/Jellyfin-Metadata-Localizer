using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Localizer.Core;

public sealed partial class CandidateStore
{
    private static void CreateOperationSchema(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db,tx,"""
            CREATE TABLE name_operations (
                id TEXT PRIMARY KEY, server_id TEXT NOT NULL, library_id TEXT NOT NULL, item_id TEXT NOT NULL,
                state TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE UNIQUE INDEX one_pending_name_operation ON name_operations(server_id,library_id,item_id)
                WHERE state IN ('Prepared','Writing','Retryable');
            CREATE TABLE name_restore_heads (
                server_id TEXT NOT NULL, library_id TEXT NOT NULL, item_id TEXT NOT NULL,
                apply_id TEXT NOT NULL REFERENCES name_operations(id), consumed INTEGER NOT NULL CHECK(consumed IN (0,1)),
                PRIMARY KEY(server_id,library_id,item_id));
            PRAGMA user_version=2;
            """);
    }

    internal IDisposable AcquireWriterLease()
    {
        try { return new FileStream(databasePath+".writer.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch (IOException) { throw new OperationBusyException(); }
    }

    private static void EnsureNotBusy(SqliteConnection db, SqliteTransaction tx, MediaKey key)
    {
        if (Scalar(db,tx,"""
            SELECT id FROM name_operations WHERE server_id=$s AND library_id=$l AND item_id=$i
            AND state IN ('Prepared','Writing','Retryable') LIMIT 1;
            """,("$s",key.ServerId),("$l",key.LibraryId),("$i",key.ItemId)) is not null) throw new OperationBusyException();
    }

    internal static string ApplyRequestFingerprint(LibraryKey scope, LanguagePreviewEntry preview) => Identity.Hash(JsonSerializer.Serialize(new { Kind="Apply",scope,preview }));
    internal static string RestoreRequestFingerprint(LibraryKey scope, string applicationId, bool accepted) => Identity.Hash(JsonSerializer.Serialize(new { Kind="Restore",scope,applicationId,accepted }));

    public NameOperation? FindOperation(string id)
    {
        using var db=Open();
        return FindOperation(db,null,id);
    }
    public NameOperation GetOperation(string id) => FindOperation(id) ?? throw new KeyNotFoundException("Operation does not exist.");
    public IReadOnlyList<NameOperation> ListNameOperations(MediaKey key, int limit = 20)
    {
        if (limit is < 1 or > 100) throw new ArgumentException("Invalid operation list limit.");
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT payload_json FROM name_operations WHERE server_id=$s AND library_id=$l AND item_id=$i
            ORDER BY rowid DESC LIMIT $n;
            """, ("$s", key.ServerId), ("$l", key.LibraryId), ("$i", key.ItemId), ("$n", limit));
        using var reader = cmd.ExecuteReader();
        var result = new List<NameOperation>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<NameOperation>(reader.GetString(0))!);
        return result;
    }
    public IReadOnlyList<NameOperation> ListUnresolvedOperations()
    {
        using var db=Open();
        using var cmd=Command(db,null,"SELECT payload_json FROM name_operations WHERE state IN ('Prepared','Writing','Retryable') ORDER BY rowid;");
        using var reader=cmd.ExecuteReader();
        var records=new List<NameOperation>();
        while (reader.Read()) records.Add(JsonSerializer.Deserialize<NameOperation>(reader.GetString(0))!);
        return records;
    }
    public RestorePoint? GetRestorePoint(MediaKey key)
    {
        using var db=Open();
        return RestoreHead(db,null,key);
    }

    internal NameOperation PrepareApply(string id, LibraryKey scope, LanguagePreviewEntry preview, NameTargetSnapshot? current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (preview.Status is not (PreviewStatus.ReadyForReview or PreviewStatus.AlreadyMatches)) throw new OperationValidationException("PreviewNotReady");
        if (preview.Key.ServerId!=scope.ServerId || preview.Key.LibraryId!=scope.LibraryId) throw new OperationValidationException("LibraryScopeChanged");
        using var db=Open();
        using var tx=db.BeginTransaction(deferred:false);
        var fingerprint=ApplyRequestFingerprint(scope,preview);
        var existing=FindOperation(db,tx,id);
        if (existing is not null) return existing.RequestFingerprint==fingerprint ? existing : throw new IdempotencyConflictException();
        EnsureNotBusy(db,tx,preview.Key);
        EnsureGenreNotBusy(db,tx,preview.Key);
        var source=CurrentSource(db,tx,preview.Key);
        if (source is null || source.Id!=preview.SourceId) throw new OperationValidationException("SourceChanged");
        var selection=Selected(db,tx,source.Id,preview.Language);
        if (selection is null || selection.Candidate.Id!=preview.CandidateId || selection.Revision!=preview.SelectionRevision ||
            selection.Candidate.Revision!=preview.CandidateRevision || !selection.Candidate.Approved) throw new OperationValidationException("CandidateChanged");
        if (preview.ProposedName!=source.DisplayPrefix+selection.Candidate.Text) throw new OperationValidationException("PreviewValueChanged");
        var operation=new NameOperation(id,fingerprint,preview.Key,OperationKind.Apply,OperationState.Prepared,source.Id,
            selection.Candidate.Id,selection.Candidate.Revision,selection.Revision,preview.Language,
            current?.Movie.CurrentName ?? preview.ExpectedCurrentName,preview.ProposedName,source.OriginalTitle,source.DisplayPrefix,
            null,false,false,null,Now(),Now());
        var failure=NameOperationService.TargetFailure(operation,current);
        if (failure is not null) operation=operation with { State=OperationState.Blocked,ErrorCategory=failure };
        else if (current!.Movie.CurrentName==operation.PlannedValue) operation=operation with { State=OperationState.NoChange };
        else if (current.Movie.CurrentName!=preview.ExpectedCurrentName) operation=operation with { State=OperationState.Conflict,ErrorCategory="PreviewCurrentValueChanged" };
        InsertOperation(db,tx,operation);
        tx.Commit();
        return operation;
    }

    internal NameOperation PrepareRestore(string id, LibraryKey scope, string parentId, bool accepted, NameTargetSnapshot? current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var db=Open();
        using var tx=db.BeginTransaction(deferred:false);
        var parent=FindOperation(db,tx,parentId) ?? throw new KeyNotFoundException("Application operation not found.");
        if (parent.Key.ServerId!=scope.ServerId || parent.Key.LibraryId!=scope.LibraryId) throw new OperationValidationException("LibraryScopeChanged");
        var fingerprint=RestoreRequestFingerprint(scope,parentId,accepted);
        var existing=FindOperation(db,tx,id);
        if (existing is not null) return existing.RequestFingerprint==fingerprint ? existing : throw new IdempotencyConflictException();
        EnsureNotBusy(db,tx,parent.Key);
        EnsureGenreNotBusy(db,tx,parent.Key);
        var head=RestoreHead(db,tx,parent.Key);
        if (parent.Kind!=OperationKind.Apply || head?.Application.Id!=parent.Id) throw new OperationValidationException("RestorePointSupersededOrMissing");
        if (parent.ObservedOnly && !accepted) throw new OperationValidationException("UncertainAttributionNeedsReview");
        var operation=new NameOperation(id,fingerprint,parent.Key,OperationKind.Restore,OperationState.Prepared,parent.SourceId,
            null,null,null,parent.Language,parent.PlannedValue,parent.BeforeValue,parent.OriginalTitle,parent.DisplayPrefix,
            parent.Id,accepted,false,null,Now(),Now());
        var failure=NameOperationService.TargetFailure(operation,current);
        if (failure is not null) operation=operation with { State=OperationState.Blocked,ErrorCategory=failure };
        else if (head!.Consumed || current!.Movie.CurrentName==operation.PlannedValue)
            operation=operation with { State=OperationState.NoChange };
        else if (current.Movie.CurrentName!=operation.BeforeValue)
            operation=operation with { State=OperationState.Conflict,ErrorCategory="CurrentValueChangedAfterApply" };
        InsertOperation(db,tx,operation);
        if (operation.State==OperationState.NoChange) UpdateRestoreHead(db,tx,operation);
        tx.Commit();
        return operation;
    }

    internal string? ValidateOperation(string id)
    {
        using var db=Open();
        using var tx=db.BeginTransaction(deferred:false);
        return ValidateOperation(db,tx,FindOperation(db,tx,id)!);
    }
    private static string? ValidateOperation(SqliteConnection db, SqliteTransaction tx, NameOperation operation)
    {
        if (CurrentSource(db,tx,operation.Key)?.Id!=operation.SourceId) return "SourceChanged";
        if (operation.Kind==OperationKind.Restore)
        {
            var head=RestoreHead(db,tx,operation.Key);
            return head is null || head.Application.Id!=operation.ParentApplyId || head.Consumed ? "RestorePointSupersededOrConsumed" : null;
        }
        if (operation.OriginalDisplay)
        {
            var source = CurrentSource(db, tx, operation.Key)!;
            return operation.PlannedValue == source.DisplayPrefix + source.OriginalTitle ? null : "SourceChanged";
        }
        var selection=Selected(db,tx,operation.SourceId,operation.Language);
        return selection is null || selection.Candidate.Id!=operation.CandidateId || selection.Revision!=operation.SelectionRevision ||
            selection.Candidate.Revision!=operation.CandidateRevision || !selection.Candidate.Approved ? "CandidateChanged" : null;
    }

    internal NameOperation MarkWriting(string id)
    {
        using var db=Open();
        using var tx=db.BeginTransaction(deferred:false);
        var operation=FindOperation(db,tx,id)!;
        if (operation.State is not (OperationState.Prepared or OperationState.Retryable)) throw new OperationBusyException();
        var failure=ValidateOperation(db,tx,operation);
        if (failure is not null) throw new OperationValidationException(failure);
        operation=operation with { State=OperationState.Writing,ErrorCategory=null,UpdatedUtc=Now() };
        SaveOperation(db,tx,operation);
        tx.Commit();
        return operation;
    }

    internal NameOperation NoteOperationError(string id, string category)
    {
        using var db=Open(); using var tx=db.BeginTransaction(deferred:false);
        var operation=FindOperation(db,tx,id)!;
        operation=operation with { ErrorCategory=category,UpdatedUtc=Now() };
        SaveOperation(db,tx,operation); tx.Commit(); return operation;
    }
    internal NameOperation CompleteOperation(string id, OperationState state, bool observedOnly, string? error)
    {
        using var db=Open(); using var tx=db.BeginTransaction(deferred:false);
        var operation=FindOperation(db,tx,id)!;
        if (!NameOperationService.IsPending(operation.State)) return operation;
        operation=operation with { State=state,ObservedOnly=observedOnly,ErrorCategory=error,UpdatedUtc=Now() };
        SaveOperation(db,tx,operation);
        if (state is OperationState.Applied or OperationState.ObservedExpected) UpdateRestoreHead(db,tx,operation);
        tx.Commit(); return operation;
    }

    private static void UpdateRestoreHead(SqliteConnection db, SqliteTransaction tx, NameOperation operation)
    {
        if (operation.Kind==OperationKind.Apply)
            Execute(db,tx,"""
                INSERT INTO name_restore_heads VALUES($s,$l,$i,$id,0)
                ON CONFLICT(server_id,library_id,item_id) DO UPDATE SET apply_id=excluded.apply_id,consumed=0;
                """,("$s",operation.Key.ServerId),("$l",operation.Key.LibraryId),("$i",operation.Key.ItemId),("$id",operation.Id));
        else
            Execute(db,tx,"UPDATE name_restore_heads SET consumed=1 WHERE server_id=$s AND library_id=$l AND item_id=$i AND apply_id=$parent;",
                ("$s",operation.Key.ServerId),("$l",operation.Key.LibraryId),("$i",operation.Key.ItemId),("$parent",operation.ParentApplyId));
    }
    private static RestorePoint? RestoreHead(SqliteConnection db, SqliteTransaction? tx, MediaKey key)
    {
        using var cmd=Command(db,tx,"""
            SELECT o.payload_json,h.consumed FROM name_restore_heads h JOIN name_operations o ON o.id=h.apply_id
            WHERE h.server_id=$s AND h.library_id=$l AND h.item_id=$i;
            """,("$s",key.ServerId),("$l",key.LibraryId),("$i",key.ItemId));
        using var reader=cmd.ExecuteReader();
        return reader.Read() ? new(JsonSerializer.Deserialize<NameOperation>(reader.GetString(0))!,reader.GetBoolean(1)) : null;
    }
    private static NameOperation? FindOperation(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        var json=Scalar(db,tx,"SELECT payload_json FROM name_operations WHERE id=$id;",("$id",id)) as string;
        return json is null ? null : JsonSerializer.Deserialize<NameOperation>(json)!;
    }
    private static void InsertOperation(SqliteConnection db, SqliteTransaction tx, NameOperation operation) =>
        Execute(db,tx,"INSERT INTO name_operations VALUES($id,$s,$l,$i,$state,$json);",("$id",operation.Id),
            ("$s",operation.Key.ServerId),("$l",operation.Key.LibraryId),("$i",operation.Key.ItemId),
            ("$state",operation.State.ToString()),("$json",JsonSerializer.Serialize(operation)));
    private static void SaveOperation(SqliteConnection db, SqliteTransaction tx, NameOperation operation) =>
        Execute(db,tx,"UPDATE name_operations SET state=$state,payload_json=$json WHERE id=$id;",("$id",operation.Id),
            ("$state",operation.State.ToString()),("$json",JsonSerializer.Serialize(operation)));
}
