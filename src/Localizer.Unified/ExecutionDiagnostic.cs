using Microsoft.Data.Sqlite;

namespace Localizer.Unified;

// Deliberately excludes exception messages, URLs, SQL parameters and request contents.
public sealed record ExecutionDiagnostic(string Stage, string? WorkId, string ErrorType, int ErrorCode, int? SqliteExtendedCode)
{
    public static ExecutionDiagnostic Capture(string stage, string? workId, Exception error) => new(
        stage, workId, error.GetType().Name, error is SqliteException sqlite ? sqlite.SqliteErrorCode : error.HResult,
        error is SqliteException extended ? extended.SqliteExtendedErrorCode : null);
}
