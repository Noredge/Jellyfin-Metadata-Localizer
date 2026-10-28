using System.Text.Json;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Unified;

public sealed record HostMedia(MediaIdentity Key, string MediaPath, string EvidenceJson = "{}");
public sealed record ObservationMetrics(double ElapsedMs, Dictionary<string, double> Phases, Dictionary<string, int> Counts);
public interface IUnifiedInventory
{
    Task<HostMedia[]> ListAsync(string[] libraryIds, CancellationToken token);
}
public interface IUnifiedObservationHints
{
    // An optimization hint only. Every send and host write still reads trusted sources again.
    ValueTask<string?> ObservationFingerprintAsync(HostMedia item, CancellationToken token);
}

/// <summary>Reads and admits changes only. It never sends, selects a model result, or writes host metadata.</summary>
public sealed class SourceObserver(WorkStore work, IUnifiedHost host, IUnifiedInventory inventory)
{
    private readonly SemaphoreSlim observing = new(1, 1);
    private readonly ContentStore content = new(work.Database);
    private readonly SettingsStore settings = new(work.Database);
    private readonly Dictionary<string, (string Fingerprint, long SettingsRevision, DateTimeOffset Verified)> observed = new(StringComparer.Ordinal);
    private ObservationMetrics? performance;
    public ObservationMetrics? Performance => Volatile.Read(ref performance);
    public async Task ObserveAllAsync(CancellationToken token = default)
    {
        await observing.WaitAsync(token);
        using var metrics = new Localizer.Core.OperationTimings(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timing = Localizer.Core.OperationTimings.Measure("observe_all");
            var start = settings.Read(); HostMedia[] items;
            using (Localizer.Core.OperationTimings.Measure("inventory_list")) items = await inventory.ListAsync(start.ManagedLibraries, token);
            var seen = new HashSet<string>(StringComparer.Ordinal); var group = Id();
            foreach (var item in items) { token.ThrowIfCancellationRequested(); seen.Add(await ObserveCore(item, start, group, false, token)); }
            var known = new List<(string Id, string Library)>();
            using (var db = work.Database.OpenRead())
            using (var cmd = Command(db, null, "SELECT id,library_id FROM media_items WHERE present=1"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) known.Add((r.GetString(0), r.GetString(1)));
            foreach (var item in known.Where(x => start.ManagedLibraries.Contains(x.Library, StringComparer.Ordinal) && !seen.Contains(x.Id))) { content.MarkRemoved(item.Id); observed.Remove(item.Id); }
            if (settings.Read().Revision == start.Revision) settings.EstablishBaseline();
        }
        finally { Volatile.Write(ref performance, new(elapsed.Elapsed.TotalMilliseconds, metrics.Snapshot(), metrics.Counts())); observing.Release(); }
    }
    public async Task ObserveMediaAsync(HostMedia item, CancellationToken token = default, string? observationId = null)
    {
        await observing.WaitAsync(token);
        try { await ObserveCore(item, settings.Read(), observationId ?? Id(), true, token); }
        finally { observing.Release(); }
    }
    private async Task<string> ObserveCore(HostMedia item, UnifiedSettings settingsAtStart, string group, bool force, CancellationToken token)
    {
        using var sourceRead = (host as IUnifiedReadScope)?.BeginReadScope(item.Key, settingsAtStart);
        var id = Hash(JsonSerializer.Serialize(new[] { item.Key.ServerId, item.Key.LibraryId, item.Key.ItemId }));
        string? oldPath; bool imported; bool firstBaseline; bool removed;
        using (var db = work.Database.OpenRead())
        {
            using var cmd = Command(db, null, """
                SELECT m.media_path,m.present,l.imported_baseline,l.baseline_established
                FROM (SELECT $library AS library_id) requested LEFT JOIN managed_libraries l ON l.library_id=requested.library_id
                LEFT JOIN media_items m ON m.id=$id
                """, ("$id", id), ("$library", item.Key.LibraryId));
            using var r = cmd.ExecuteReader(); r.Read();
            oldPath = r.IsDBNull(0) ? null : r.GetString(0); removed = !r.IsDBNull(1) && r.GetInt64(1) == 0;
            imported = !r.IsDBNull(2) && r.GetInt64(2) == 1; firstBaseline = r.IsDBNull(3) || r.GetInt64(3) == 0;
        }
        if (oldPath is null) content.RegisterMedia(item.Key, item.MediaPath, item.EvidenceJson);
        else if (!string.Equals(oldPath, item.MediaPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            foreach (var f in new[] { "Name", "Overview", "Genres" }) content.ObserveUnavailable(id, f, "source_binding_changed"); return id;
        }
        var fingerprint = inventory is IUnifiedObservationHints hints ? await hints.ObservationFingerprintAsync(item, token) : null;
        if (!force && !removed && fingerprint is not null && observed.TryGetValue(id, out var previous)
            && previous.Fingerprint == fingerprint && previous.SettingsRevision == settingsAtStart.Revision
            && work.UtcNow >= previous.Verified && work.UtcNow - previous.Verified < TimeSpan.FromMinutes(15)) return id;
        observed.Remove(id);
        var complete = true; var returning = false;
        foreach (var field in new[] { "Name", "Overview", "Genres" })
        {
            token.ThrowIfCancellationRequested(); var old = content.ReadField(id, field);
            string? previousDisplayHash; string? importedDisplay = null;
            using (var db = work.Database.OpenRead())
            {
                previousDisplayHash = Scalar<string>(db, null, "SELECT observed_display_hash FROM media_fields WHERE media_id=$id AND field=$f", ("$id", id), ("$f", field));
                if (previousDisplayHash is null && imported && firstBaseline)
                    importedDisplay = Scalar<string>(db, null, "SELECT expected_value FROM display_intents WHERE media_id=$id AND field=$f", ("$id", id), ("$f", field));
            }
            HostFieldObservation actual;
            try { actual = await host.ReadAsync(item.Key, field, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { content.ObserveUnavailable(id, field, "source_unavailable"); complete = false; continue; }
            if (!actual.Present) { content.MarkRemoved(id); return id; }
            if (actual.Key != item.Key || !string.Equals(actual.MediaPath, item.MediaPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            { content.ObserveUnavailable(id, field, "source_binding_changed"); complete = false; continue; }
            if (removed) { returning = content.RestorePresence(id, item.MediaPath); removed = false; }
            if (actual.Original is null || actual.SourceError is not null) { content.ObserveUnavailable(id, field, actual.SourceError ?? "source_unavailable"); complete = false; continue; }
            var current = content.Observe(id, field, actual.Original, actual.EvidenceJson, actual.Prefix, actual.Display);
            var changed = returning || old is null || old.SourceRevision != current.SourceRevision || old.Availability != current.Availability;
            var atOriginal = DisplayExecutor.Equal(field, actual.Display, UnifiedExecutor.OriginalValue(current));
            var displayChanged = previousDisplayHash is not null ? previousDisplayHash != Hash(actual.Display)
                : importedDisplay is not null && !DisplayExecutor.Equal(field, importedDisplay, actual.Display);
            var owned = atOriginal;
            bool hasTranslation = false, keep = false;
            using (var db = work.Database.OpenRead())
            {
                using (var cmd = Command(db, null, "SELECT mode,desired_value,expected_value,keep_original FROM display_intents WHERE media_id=$id AND field=$f", ("$id", id), ("$f", field)))
                using (var r = cmd.ExecuteReader()) if (r.Read())
                {
                    keep = r.GetBoolean(3);
                    if (r.GetString(0) != "external" && !r.IsDBNull(1) && DisplayExecutor.Equal(field, actual.Display, r.GetString(1))) owned = true;
                }
                // A replacement intent can be durable before the host call. Its predecessor's verified
                // display still belongs to us during this gap, even though desired_value already changed.
                var verified = Scalar<string>(db, null, """
                    SELECT w.target_value FROM display_writes w JOIN display_intents i ON i.id=w.intent_id
                    WHERE i.media_id=$m AND i.field=$f AND w.state='verified' ORDER BY w.rowid DESC LIMIT 1
                    """, ("$m", id), ("$f", field));
                if (verified is not null && DisplayExecutor.Equal(field, actual.Display, verified)) owned = true;
                if (!owned && previousDisplayHash == Hash(actual.Display))
                {
                    // The native read can have captured a verified predecessor just before our
                    // writer completed a replacement. Do not classify this stale snapshot as an edit.
                    var predecessor = Scalar<string>(db, null, """
                        SELECT w.target_value FROM display_writes w JOIN display_intents i ON i.id=w.intent_id
                        WHERE i.media_id=$m AND i.field=$f AND w.state='verified' AND w.target_hash=$h ORDER BY w.rowid DESC LIMIT 1
                        """, ("$m", id), ("$f", field), ("$h", previousDisplayHash));
                    if (predecessor is not null && DisplayExecutor.Equal(field, actual.Display, predecessor)) owned = true;
                }
                hasTranslation = Scalar<long>(db, null, "SELECT COUNT(*) FROM translation_selections WHERE source_id=$s AND language=$l", ("$s", current.SourceId), ("$l", settingsAtStart.Language)) > 0;
            }
            if (!owned && old is not null && DisplayExecutor.Equal(field, actual.Display, UnifiedExecutor.OriginalValue(old))) owned = true;
            if (!owned)
            {
                // Existing history is retained; control advances so an in-flight model result cannot take over.
                content.ProtectExternal(current, actual.Display); continue;
            }
            if (atOriginal) current = content.AllowOriginalReset(current, actual.Display);
            if (current.Availability == "empty") continue;
            if (firstBaseline && !imported) continue;
            if (changed || displayChanged && atOriginal && hasTranslation && !keep)
                work.EnqueueAutomatic(oldPath is null || returning ? "added" : "nfo", new(current, actual.Display), deferUntilBaseline: true, observationId: group);
        }
        if (complete && fingerprint is not null)
        {
            if (observed.Count >= 8192) observed.Clear();
            observed[id] = (fingerprint, settingsAtStart.Revision, work.UtcNow);
        }
        return id;
    }
}
