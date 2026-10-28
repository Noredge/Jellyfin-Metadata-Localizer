using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

public sealed record FillCandidate(FieldState Field, MediaIdentity Key);
public sealed record FillPlan(FillCandidate[] Candidates, long Skipped);

/// <summary>Read the observed inventory first; only missing candidates need host/NFO reads.</summary>
public sealed class FillPlanner(UnifiedDatabase database)
{
    // Used again inside submission's write transaction, so a selected translation or active
    // request arriving after preview cannot cause duplicate generation or display work.
    internal const string Eligible = """
        f.availability='available'
        AND NOT EXISTS(SELECT 1 FROM translation_selections s WHERE s.source_id=f.source_id AND s.language=$language)
        AND NOT EXISTS(SELECT 1 FROM display_intents d WHERE d.media_id=f.media_id AND d.field=f.field
          AND (d.keep_original=1 OR d.mode='external' AND d.state='protected'))
        AND NOT EXISTS(SELECT 1 FROM work_items w WHERE w.media_id=f.media_id AND w.field=f.field
          AND w.source_id=f.source_id AND w.source_revision=f.source_revision AND w.control_revision=f.control_revision
          AND w.render_revision=f.render_revision AND w.language=$language AND w.target_epoch=$epoch
          AND w.state IN('queued','running','waiting','paused'))
        """;
    internal static bool CanFill(SqliteConnection db, SqliteTransaction tx, FieldState field, UnifiedSettings settings) =>
        Scalar<long>(db, tx, "SELECT COUNT(*) FROM media_fields f WHERE f.media_id=$media AND f.field=$field AND " + Eligible,
            ("$media", field.MediaId), ("$field", field.Field), ("$language", settings.Language), ("$epoch", settings.TargetEpoch)) == 1;

    public FillPlan Read(string[]? mediaIds = null, string[]? fields = null)
    {
        using var timing = Localizer.Core.OperationTimings.Measure("fill_plan");
        using var db = database.OpenRead(); using var tx = db.BeginTransaction(deferred: true); var settings = SettingsStore.Read(db, tx);
        var scope = "m.present=1 AND ((f.field='Name' AND $title=1) OR (f.field='Overview' AND $overview=1) OR (f.field='Genres' AND $genres=1))";
        var args = new List<(string, object?)> { ("$language", settings.Language), ("$epoch", settings.TargetEpoch),
            ("$title", settings.Fields.Title), ("$overview", settings.Fields.Overview), ("$genres", settings.Fields.Genres) };
        if (mediaIds is not null) { scope += " AND f.media_id IN(SELECT value FROM json_each($media))"; args.Add(("$media", System.Text.Json.JsonSerializer.Serialize(mediaIds))); }
        if (fields is not null) { scope += " AND f.field IN(SELECT value FROM json_each($fields))"; args.Add(("$fields", System.Text.Json.JsonSerializer.Serialize(fields))); }
        const string from = " FROM media_fields f JOIN media_items m ON m.id=f.media_id JOIN managed_libraries l ON l.library_id=m.library_id JOIN source_versions s ON s.id=f.source_id WHERE ";
        var total = Scalar<long>(db, tx, "SELECT COUNT(*)" + from + scope, args.ToArray());
        using var cmd = Command(db, tx, """
            SELECT f.media_id,f.field,f.source_id,f.source_revision,f.control_revision,f.render_revision,f.display_prefix,f.availability,s.content,f.render_json,
              m.server_id,m.library_id,m.item_id
            """ + from + scope + " AND " + Eligible + " ORDER BY m.id,f.field", args.ToArray());
        using var r = cmd.ExecuteReader(); var candidates = new List<FillCandidate>();
        while (r.Read())
        {
            candidates.Add(new(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9)),
                new(r.GetString(10),r.GetString(11),r.GetString(12))));
        }
        return new([.. candidates], total - candidates.Count);
    }
}
