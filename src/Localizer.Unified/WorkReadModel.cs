using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

public sealed partial class ReadModel
{
    private static string WorkSql(bool exact, bool batch, bool fullText)
    {
        // These fragments are fixed SQL. User values are always parameters.
        var predicate = exact ? "w.id=$work" : batch ? "w.id IN(SELECT work_id FROM batch_items WHERE batch_id=$b)" : "1=1";
        if (exact && batch) predicate += " AND EXISTS(SELECT 1 FROM batch_items bi WHERE bi.batch_id=$b AND bi.work_id=w.id)";
        var ordering = exact ? "" : "ORDER BY CASE w.state WHEN 'running' THEN 0 WHEN 'queued' THEN 1 WHEN 'waiting' THEN 2 WHEN 'paused' THEN 3 ELSE 4 END,w.created_utc DESC,w.id";
        var original = fullText ? "s.content" : "substr(s.content,1,180)";
        var result = fullText ? "t.text" : "substr(t.text,1,180)";
        return $"""
            SELECT w.id,w.media_id,w.field,w.state,w.step,w.wait_reason,w.issue_kind,w.error_code,w.completion_reason,w.attempt_count,
              w.request_ms,w.save_ms,(w.save_ms>0) AS save_timing_measured,w.display_ms,w.created_utc,w.updated_utc,{original} AS original,m.item_id,
              {(fullText ? "0" : "length(s.content)>180")} AS original_truncated,
              (SELECT {result} FROM translation_versions t WHERE t.id=w.translation_id) AS result_text,
              {(fullText ? "0" : "(SELECT length(t.text)>180 FROM translation_versions t WHERE t.id=w.translation_id)")} AS result_truncated,
              coalesce((SELECT r.send_started_utc FROM request_attempts r WHERE r.work_id=w.id AND r.state='sending' ORDER BY sequence DESC LIMIT 1),w.updated_utc) AS current_stage_started_utc,
              (SELECT CASE WHEN d.state='applied' THEN d.desired_value ELSE d.expected_value END FROM display_intents d WHERE d.media_id=w.media_id AND d.field='Name') AS movie_name
            FROM work_items w JOIN source_versions s ON s.id=w.source_id LEFT JOIN media_items m ON m.id=w.media_id
            WHERE {predicate} {ordering} LIMIT $limit OFFSET $offset
            """;
    }
    public Dictionary<string, object?>[] Work(string? batchId, int offset, int limit, string? workId = null)
    {
        Page(offset, limit); using var db = database.OpenRead();
        return Rows(db, WorkSql(workId is not null, batchId is not null, false),
            ("$b", batchId), ("$offset", offset), ("$limit", limit), ("$work", workId));
    }
    public Dictionary<string, object?>? WorkDetail(string workId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workId); using var db = database.OpenRead();
        return Rows(db, WorkSql(true, false, true), ("$work", workId), ("$limit", 1), ("$offset", 0)).SingleOrDefault();
    }
}
