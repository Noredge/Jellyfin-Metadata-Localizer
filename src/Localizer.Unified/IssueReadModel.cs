using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

public sealed partial class ReadModel
{
    // Overview counts and issue pages use exactly the same actionable entries.
    private static readonly string CurrentIssues = $"""
        WITH term_failures AS (
          SELECT w.* FROM work_items w JOIN genre_terms t ON t.id=w.term_id
          WHERE w.target_kind='term' AND w.state IN('failed','paused') AND w.issue_kind IS NOT NULL
            AND w.source_id=t.source_id AND w.control_revision=t.control_revision
            AND w.target_epoch=(SELECT target_epoch FROM settings)
            AND w.language=(SELECT target_language FROM settings)
            AND {WorkStore.TermDemandSql.Replace("$automatic", "(SELECT auto_enabled AND baseline_established FROM settings)", StringComparison.Ordinal)}
            AND NOT EXISTS(SELECT 1 FROM work_items later WHERE later.term_id=w.term_id
              AND later.target_epoch=w.target_epoch AND later.control_revision=w.control_revision AND later.rowid>w.rowid)
        ), media_issues AS (
          SELECT w.media_id,w.field,w.id AS work_id,w.issue_kind,w.error_code,NULL AS term_id FROM work_items w
            JOIN media_fields f ON f.media_id=w.media_id AND f.field=w.field
          WHERE w.target_kind='media' AND w.state IN('failed','paused') AND w.issue_kind IS NOT NULL
            AND w.source_id=f.source_id AND w.source_revision=f.source_revision AND w.control_revision=f.control_revision AND w.render_revision=f.render_revision
            AND w.target_epoch=(SELECT target_epoch FROM settings)
            AND NOT EXISTS(SELECT 1 FROM work_items later WHERE later.media_id=w.media_id AND later.field=w.field
              AND later.source_id=w.source_id AND later.source_revision=w.source_revision AND later.control_revision=w.control_revision
              AND later.render_revision=w.render_revision AND later.target_epoch=w.target_epoch AND later.rowid>w.rowid)
          UNION ALL SELECT f.media_id,f.field,NULL,'source',f.observation_error,NULL FROM media_fields f WHERE f.availability='unreadable'
          UNION ALL SELECT d.media_id,d.field,NULL,'display','external_display_preserved',NULL FROM display_intents d WHERE d.mode='external' AND d.state='protected'
          UNION ALL SELECT p.media_id,p.field,w.id,w.issue_kind,w.error_code,w.term_id FROM term_failures w
            JOIN work_dependencies dep ON dep.child_id=w.id JOIN work_items p ON p.id=dep.parent_id
            JOIN media_fields f ON f.media_id=p.media_id AND f.field=p.field
            JOIN media_items m ON m.id=p.media_id JOIN managed_libraries l ON l.library_id=m.library_id JOIN settings cfg ON cfg.singleton=1
          WHERE {WorkStore.CurrentParentDemandSql.Replace("$automatic", "(cfg.auto_enabled AND cfg.baseline_established)", StringComparison.Ordinal)}
        ), scoped AS (
          SELECT DISTINCT i.*,m.item_id,'media' AS target_kind,NULL AS original FROM media_issues i
            JOIN media_items m ON m.id=i.media_id JOIN managed_libraries l ON l.library_id=m.library_id JOIN settings s ON s.singleton=1
          WHERE m.present=1 AND ((i.field='Name' AND s.title_enabled=1) OR (i.field='Overview' AND s.overview_enabled=1) OR (i.field='Genres' AND s.genres_enabled=1))
        ), current_issues AS (
          SELECT * FROM scoped
          UNION ALL SELECT NULL,'Term',w.id,w.issue_kind,w.error_code,w.term_id,NULL,'term',t.original
            FROM term_failures w JOIN genre_terms t ON t.id=w.term_id
          WHERE NOT EXISTS(SELECT 1 FROM scoped i WHERE i.work_id=w.id)
        )
        """;

    public MoviePage Issues(string kind, string errorCode, int offset, int limit)
    {
        Page(offset, limit); using var db = database.OpenRead(); using var tx = db.BeginTransaction(deferred: true);
        var query = CurrentIssues + " SELECT * FROM current_issues WHERE issue_kind=$kind AND ifnull(error_code,'')=$code";
        var args = new (string, object?)[] { ("$kind", kind), ("$code", errorCode), ("$offset", offset), ("$limit", limit) };
        var total = Scalar<long>(db, tx, "SELECT COUNT(*) FROM (" + query + ")", args);
        using var cmd = Command(db, tx, query + " ORDER BY target_kind,media_id,field,work_id LIMIT $limit OFFSET $offset", args);
        using var r = cmd.ExecuteReader(); var rows = new List<Dictionary<string, object?>>();
        while (r.Read()) { var row = new Dictionary<string, object?>(); for (var i=0;i<r.FieldCount;i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i); rows.Add(row); }
        return new(total, [.. rows], offset, limit);
    }
}
