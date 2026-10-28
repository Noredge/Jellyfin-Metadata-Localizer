using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

public sealed record FieldOptions(bool Title, bool Overview, bool Genres)
{
    public bool Includes(string field) => field switch { "Name" => Title, "Overview" => Overview, "Genres" => Genres, _ => false };
}
public sealed record UnifiedSettings(long Revision, string Language, long TargetEpoch, FieldOptions Fields,
    bool AutoEnabled, string[] ManagedLibraries, string? ConnectionId, bool BaselineEstablished);
public sealed record ConnectionVersion(string Id, long Revision, string Endpoint, string Model, int TimeoutMs,
    int RequestsPerMinute, string? CredentialReference, string ParametersJson);

public sealed class SettingsStore(UnifiedDatabase database)
{
    public UnifiedSettings Read() { using var db = database.OpenRead(); return Read(db, null); }
    internal static UnifiedSettings Read(SqliteConnection db, SqliteTransaction? tx)
    {
        UnifiedSettings value;
        using (var cmd = Command(db, tx, "SELECT revision,target_language,target_epoch,title_enabled,overview_enabled,genres_enabled,auto_enabled,connection_id,baseline_established FROM settings WHERE singleton=1"))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidOperationException("UnifiedSettingsRequired");
            value = new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), new(r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5)),
                r.GetBoolean(6), [], r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8));
        }
        using var libraries = Command(db, tx, "SELECT library_id FROM managed_libraries ORDER BY library_id");
        using var reader = libraries.ExecuteReader(); var ids = new List<string>(); while (reader.Read()) ids.Add(reader.GetString(0));
        return value with { ManagedLibraries = [.. ids] };
    }
    public UnifiedSettings Save(long expectedRevision, string language, FieldOptions fields, string[] libraries, bool automatic)
    {
        ContentStore.ValidateLanguage(language);
        if (!(fields.Title || fields.Overview || fields.Genres) || libraries.Any(x => !Guid.TryParse(x, out _))
            || libraries.Select(x => Guid.Parse(x).ToString("N")).Distinct(StringComparer.Ordinal).Count() != libraries.Length) throw new ArgumentException("InvalidSettings");
        libraries = libraries.Select(x => Guid.Parse(x).ToString("N")).ToArray();
        return database.Write((db, tx) =>
        {
            var old = Read(db, tx); if (old.Revision != expectedRevision) throw new InvalidOperationException("RevisionConflict");
            var previousScopes = new Dictionary<string, (bool Established, string? Utc, bool Imported)>(StringComparer.Ordinal);
            using (var cmd = Command(db, tx, "SELECT library_id,baseline_established,baseline_utc,imported_baseline FROM managed_libraries"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) previousScopes[r.GetString(0)] = (r.GetBoolean(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3));
            var scopeChanged = !old.ManagedLibraries.Order(StringComparer.Ordinal).SequenceEqual(libraries.Order(StringComparer.Ordinal), StringComparer.Ordinal);
            Execute(db, tx, """
                UPDATE settings SET revision=revision+1,target_language=$l,target_epoch=target_epoch+$changed,
                  title_enabled=$t,overview_enabled=$o,genres_enabled=$g,auto_enabled=$a WHERE singleton=1
                """, ("$l", language), ("$changed", old.Language == language ? 0 : 1), ("$t", fields.Title),
                ("$o", fields.Overview), ("$g", fields.Genres), ("$a", automatic));
            if (old.Language != language)
                Execute(db, tx, "INSERT INTO runtime_state VALUES('language_pending',$e) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$e", (old.TargetEpoch + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Execute(db, tx, "DELETE FROM managed_libraries");
            foreach (var id in libraries)
            {
                previousScopes.TryGetValue(id, out var baseline);
                Execute(db, tx, "INSERT INTO managed_libraries VALUES($id,$b,$n,$i)", ("$id", id), ("$b", baseline.Established), ("$n", baseline.Utc), ("$i", baseline.Imported));
            }
            if (scopeChanged) Execute(db, tx, "UPDATE settings SET baseline_established=0 WHERE singleton=1");
            if (!automatic)
            {
                Execute(db, tx, "UPDATE work_items SET autonomous_demand=0 WHERE autonomous_demand=1 AND state IN('queued','running','waiting','paused')");
                Execute(db, tx, """
                    UPDATE work_items SET state='expired',completion_reason='automatic_demand_revoked',lease_owner=NULL,lease_until_utc=NULL,updated_utc=$n
                    WHERE target_kind='media' AND state IN('queued','running','waiting','paused') AND autonomous_demand=0
                    AND NOT EXISTS(SELECT 1 FROM batch_items bi JOIN batches b ON b.id=bi.batch_id
                      WHERE bi.work_id=work_items.id AND bi.active=1 AND b.cancelled=0 AND b.kind NOT IN('added','nfo'))
                    """, ("$n", Now()));
            }
            // Expiration is durable. Removing and later re-adding a scope never revives its old execution authority.
            Execute(db, tx, """
                UPDATE work_items SET state='expired',completion_reason='target_or_scope_changed',lease_owner=NULL,lease_until_utc=NULL,updated_utc=$n
                WHERE state IN('queued','running','waiting','paused') AND (
                  target_epoch<>(SELECT target_epoch FROM settings) OR language<>(SELECT target_language FROM settings) OR
                  (target_kind='media' AND (NOT EXISTS(SELECT 1 FROM media_items m JOIN managed_libraries l ON l.library_id=m.library_id
                    WHERE m.id=work_items.media_id AND m.present=1) OR
                    (field='Name' AND (SELECT title_enabled FROM settings)=0) OR
                    (field='Overview' AND (SELECT overview_enabled FROM settings)=0) OR
                    (field='Genres' AND (SELECT genres_enabled FROM settings)=0))))
                """, ("$n", Now()));
            Execute(db, tx, """
                UPDATE display_intents SET state='expired',error_code='target_or_scope_changed',updated_utc=$n
                WHERE target_epoch<>(SELECT target_epoch FROM settings) OR
                  NOT EXISTS(SELECT 1 FROM media_items m JOIN managed_libraries l ON l.library_id=m.library_id WHERE m.id=display_intents.media_id AND m.present=1) OR
                  (field='Name' AND (SELECT title_enabled FROM settings)=0) OR
                  (field='Overview' AND (SELECT overview_enabled FROM settings)=0) OR
                  (field='Genres' AND (SELECT genres_enabled FROM settings)=0)
                """, ("$n", Now()));
            return Read(db, tx);
        });
    }
    public ConnectionVersion? Connection(string? id = null)
    {
        using var db = database.OpenRead(); return Connection(db, null, id ?? Scalar<string>(db, null, "SELECT connection_id FROM settings WHERE singleton=1"));
    }
    internal static ConnectionVersion? Connection(SqliteConnection db, SqliteTransaction? tx, string? id)
    {
        if (id is null) return null;
        using var cmd = Command(db, tx, "SELECT id,revision,endpoint,model,timeout_ms,requests_per_minute,credential_reference,parameters_json FROM connection_versions WHERE id=$id", ("$id", id));
        using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7)) : null;
    }
    public ConnectionVersion SaveConnection(long expectedRevision, string endpoint, string model, int timeoutMs, int requestsPerMinute,
        string? credentialReference, string parametersJson, long? expectedSettingsRevision = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(model) || model.Any(char.IsWhiteSpace) || timeoutMs is < 1000 or > 600000 || requestsPerMinute is < 1 or > 10000
            || credentialReference is not null && !Guid.TryParseExact(credentialReference, "N", out _)) throw new ArgumentException("InvalidConnection");
        ConnectionParameters.Validate(parametersJson);
        return database.Write((db, tx) =>
        {
            if (expectedSettingsRevision is not null && Scalar<long>(db, tx, "SELECT revision FROM settings") != expectedSettingsRevision.Value)
                throw new InvalidOperationException("RevisionConflict");
            var currentId = Scalar<string>(db, tx, "SELECT connection_id FROM settings WHERE singleton=1");
            var old = Connection(db, tx, currentId);
            if ((old?.Revision ?? 0) != expectedRevision) throw new InvalidOperationException("RevisionConflict");
            // The caller saves a new DPAPI envelope before passing its reference. It cannot inherit one across endpoints.
            if (old?.Endpoint != endpoint && credentialReference is not null && old?.CredentialReference == credentialReference)
                throw new InvalidOperationException("CredentialEndpointMismatch");
            var id = Id(); var revision = Scalar<long>(db, tx, "SELECT COALESCE(MAX(revision),0)+1 FROM connection_versions");
            Execute(db, tx, "INSERT INTO connection_versions VALUES($id,$r,$e,$m,$t,$rpm,$c,$p,$n)", ("$id", id), ("$r", revision), ("$e", endpoint),
                ("$m", model), ("$t", timeoutMs), ("$rpm", requestsPerMinute), ("$c", credentialReference), ("$p", parametersJson), ("$n", Now()));
            Execute(db, tx, "UPDATE settings SET connection_id=$id,revision=revision+1 WHERE singleton=1", ("$id", id));
            return Connection(db, tx, id)!;
        });
    }
    public void EstablishBaseline()
    {
        using (var db = database.Open())
            if (Scalar<long>(db, null, "SELECT COUNT(*) FROM settings WHERE baseline_established=0") == 0
                && Scalar<long>(db, null, "SELECT COUNT(*) FROM managed_libraries WHERE baseline_established=0") == 0) return;
        database.Write((db, tx) =>
        {
            Execute(db, tx, "UPDATE settings SET baseline_established=1,baseline_utc=COALESCE(baseline_utc,$n) WHERE baseline_established=0", ("$n", Now()));
            Execute(db, tx, "UPDATE managed_libraries SET baseline_established=1,baseline_utc=COALESCE(baseline_utc,$n) WHERE baseline_established=0", ("$n", Now()));
            return true;
        });
    }
}
