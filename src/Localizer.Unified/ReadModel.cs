using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

public sealed record MoviePage(long Total, Dictionary<string, object?>[] Items, int Offset, int Limit);

/// <summary>SQL summaries and bounded details; no network, filesystem journals or secondary read database.</summary>
public sealed partial class ReadModel(UnifiedDatabase database)
{
    private const string EnabledField = """
        ((f.field='Name' AND (SELECT title_enabled FROM settings)=1)
          OR (f.field='Overview' AND (SELECT overview_enabled FROM settings)=1)
          OR (f.field='Genres' AND (SELECT genres_enabled FROM settings)=1))
        """;
    public static Dictionary<string, object?>[] Rows(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(db, null, sql, parameters); using var r = cmd.ExecuteReader(); var result = new List<Dictionary<string, object?>>();
        while (r.Read()) { var row = new Dictionary<string, object?>(); for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i); result.Add(row); }
        return [.. result];
    }
    public object Overview(bool includeWorkHistory = true)
    {
        using var timing = Localizer.Core.OperationTimings.Measure("overview_read");
        using var db = database.OpenRead();
        var fields = Rows(db, """
            SELECT f.field,COUNT(*) AS total,
              SUM(f.availability='empty') AS empty,
              SUM(f.availability='unreadable') AS source_problem,
              SUM(f.availability='available' AND ifnull(d.keep_original,0)=1) AS kept_original,
              SUM(f.availability='available' AND ifnull(d.keep_original,0)=0) AS denominator,
              SUM(f.availability='available' AND ifnull(d.keep_original,0)=0 AND s.translation_id IS NOT NULL) AS translated,
              SUM(f.availability='available' AND ifnull(d.keep_original,0)=0 AND s.translation_id IS NULL) AS missing,
              SUM(f.availability='available' AND ifnull(d.keep_original,0)=0 AND s.translation_id IS NOT NULL
                AND (d.id IS NULL OR NOT(d.mode='translated' AND d.source_id=f.source_id AND d.source_revision=f.source_revision AND d.render_revision=f.render_revision
                  AND d.language=(SELECT target_language FROM settings) AND (d.translation_id=s.translation_id OR EXISTS(
                    SELECT 1 FROM translation_versions displayed JOIN translation_versions selected ON selected.id=s.translation_id
                    WHERE displayed.id=d.translation_id AND (displayed.text_hash=selected.text_hash OR f.field='Genres'
                      AND NOT EXISTS(SELECT value FROM json_each(displayed.text) EXCEPT SELECT value FROM json_each(selected.text))
                      AND NOT EXISTS(SELECT value FROM json_each(selected.text) EXCEPT SELECT value FROM json_each(displayed.text)))))
                  AND (d.state='applied' OR d.state='expired' AND EXISTS(SELECT 1 FROM display_writes verified WHERE verified.intent_id=d.id AND verified.state='verified' AND verified.target_hash=f.observed_display_hash))))) AS pending_display
            FROM media_fields f JOIN media_items m ON m.id=f.media_id JOIN managed_libraries l ON l.library_id=m.library_id
            LEFT JOIN display_intents d ON d.media_id=f.media_id AND d.field=f.field
            LEFT JOIN translation_selections s ON s.source_id=f.source_id AND s.language=(SELECT target_language FROM settings)
            WHERE m.present=1 AND
            """ + EnabledField + " GROUP BY f.field");
        var work = includeWorkHistory ? Rows(db, "SELECT state,COUNT(*) AS count FROM work_items GROUP BY state") : null;
        var issues = Rows(db, CurrentIssues + " SELECT issue_kind,error_code,COUNT(*) AS count FROM current_issues GROUP BY issue_kind,error_code ORDER BY count DESC");
        var batches = Rows(db, """
            WITH recent AS (SELECT * FROM batches ORDER BY created_utc DESC,id DESC LIMIT 3)
            SELECT b.id,b.kind,b.language,b.cancelled,b.created_utc,COUNT(bi.work_id) AS total,
              SUM(w.state='completed') AS completed,SUM(w.state IN('failed','paused')) AS problem,
              SUM(w.state IN('queued','waiting','running')) AS pending
            FROM recent b LEFT JOIN batch_items bi ON bi.batch_id=b.id LEFT JOIN work_items w ON w.id=bi.work_id
            GROUP BY b.id ORDER BY b.created_utc DESC,b.id DESC LIMIT 3
            """);
        var active = Scalar<long>(db, null, "SELECT COUNT(*) FROM work_items WHERE state IN('queued','waiting','running')");
        return new { Fields = fields, Work = work, Issues = issues, Batches = batches, Active = active };
    }
    public MoviePage Movies(string search, string? libraryId, string filter, int offset, int limit)
    {
        using var timing = Localizer.Core.OperationTimings.Measure("movies_query");
        Page(offset, limit);
        if (filter is not ("all" or "missing" or "problem" or "manual" or "original")) throw new ArgumentException("InvalidFilter");
        var where = """
            m.present=1 AND ($library IS NULL OR m.library_id=$library)
            AND ($q='' OR instr(lower(m.media_path),lower($q))>0
              OR EXISTS(SELECT 1 FROM display_intents d WHERE d.media_id=m.id AND (instr(lower(ifnull(d.desired_value,'')),lower($q))>0 OR instr(lower(d.expected_value),lower($q))>0))
              OR EXISTS(SELECT 1 FROM source_versions s WHERE s.owner_id=m.id AND instr(lower(s.content),lower($q))>0)
              OR EXISTS(SELECT 1 FROM translation_versions t JOIN source_versions s ON s.id=t.source_id WHERE s.owner_id=m.id AND instr(lower(t.text),lower($q))>0))
            AND ($filter='all' OR $filter='missing' AND EXISTS(SELECT 1 FROM media_fields f LEFT JOIN display_intents d ON d.media_id=f.media_id AND d.field=f.field
              WHERE f.media_id=m.id AND
            """ + EnabledField + """
              AND EXISTS(SELECT 1 FROM managed_libraries l WHERE l.library_id=m.library_id)
              AND f.availability='available' AND ifnull(d.keep_original,0)=0 AND NOT EXISTS(SELECT 1 FROM translation_selections s WHERE s.source_id=f.source_id AND s.language=(SELECT target_language FROM settings)))
              OR $filter='problem' AND EXISTS(SELECT 1 FROM current_issues i WHERE i.target_kind='media' AND i.media_id=m.id)
              OR $filter='manual' AND EXISTS(SELECT 1 FROM translation_versions t JOIN source_versions s ON s.id=t.source_id WHERE s.owner_id=m.id AND t.origin='manual')
              OR $filter='original' AND EXISTS(SELECT 1 FROM display_intents d WHERE d.media_id=m.id AND d.keep_original=1))
            """;
        using var db = database.OpenRead(); var args = new (string, object?)[] { ("$q", search), ("$library", libraryId), ("$filter", filter), ("$offset", offset), ("$limit", limit) };
        var total = Scalar<long>(db, null, CurrentIssues + " SELECT COUNT(*) FROM media_items m WHERE " + where, args);
        var items = Rows(db, CurrentIssues + "\n" + """
            SELECT m.id,m.library_id,m.item_id,m.media_path,
              coalesce((SELECT CASE WHEN d.state='applied' THEN d.desired_value ELSE d.expected_value END FROM display_intents d WHERE d.media_id=m.id AND d.field='Name'),
                (SELECT s.content FROM media_fields f JOIN source_versions s ON s.id=f.source_id WHERE f.media_id=m.id AND f.field='Name'),m.item_id) AS name,
              (SELECT COUNT(*) FROM work_items w WHERE w.media_id=m.id AND w.state IN('queued','running','waiting','paused')) AS pending,
              (SELECT COUNT(DISTINCT i.field) FROM current_issues i WHERE i.media_id=m.id AND i.issue_kind='source' AND i.target_kind='media') AS source_problems
            FROM media_items m WHERE
            """ + " " + where + " ORDER BY name COLLATE NOCASE,m.id LIMIT $limit OFFSET $offset", args);
        return new(total, items, offset, limit);
    }
    public object Attempts(string workId, int offset, int limit)
    {
        Page(offset, limit); using var db = database.OpenRead();
        return Rows(db, """
            SELECT id,sequence,state,uncertainty_acknowledged,started_utc,send_started_utc,completed_utc,http_status,error_code,
              diagnostics_json,usage_json,elapsed_ms,translation_id,late_completed_utc,late_diagnostics_json,late_usage_json,late_translation_id
            FROM request_attempts WHERE work_id=$id ORDER BY sequence DESC LIMIT $limit OFFSET $offset
            """, ("$id", workId), ("$offset", offset), ("$limit", limit));
    }
    public static void Page(int offset, int limit) { if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit)); }
}
