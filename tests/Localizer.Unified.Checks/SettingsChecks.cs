using System.Text.Json;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class SettingsChecks
{
    internal static void Run(string root)
    {
        var database = new UnifiedDatabase(root, create: true); var store = new SettingsStore(database); var content = new ContentStore(database);
        var results = new List<object>();
        void Check(string name, bool valid)
        { results.Add(new { name, passed = valid }); Console.WriteLine((valid ? "PASS " : "FAIL ") + name); if (!valid) throw new InvalidOperationException(name); }
        void Reject(string name, Action action)
        { try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Check(name, true); return; } Check(name, false); }
        var library = Guid.NewGuid().ToString("N");
        var current = store.Save(1, "zh-Hans", new(true, true, true), [library], true);
        Check("new managed scope requires baseline before automatic discovery", !current.BaselineEstablished);
        store.EstablishBaseline(); Check("baseline persists in unified database", store.Read().BaselineEstablished);
        var media = content.RegisterMedia(new("server", library, "item"), "C:\\fixture\\movie.mp4", "fixture");
        var field = content.Observe(media, "Name", "source", "{}");
        using (var db = database.Open())
        {
            foreach (var id in new[] { "automatic", "shared" })
                Execute(db, null, """
                    INSERT INTO work_items(id,submission_key,target_kind,media_id,field,source_id,source_revision,control_revision,
                      language,target_epoch,generation_key,autonomous_demand,state,step,created_utc,updated_utc)
                    VALUES($id,$id,'media',$m,'Name',$s,1,0,'zh-Hans',0,$id,1,'queued','source',$n,$n)
                    """, ("$id", id), ("$m", media), ("$s", field.SourceId), ("$n", Now()));
            Execute(db, null, "INSERT INTO batches(id,submission_key,kind,language,target_epoch,created_utc) VALUES('manual','manual','fill','zh-Hans',0,$n)", ("$n", Now()));
            Execute(db, null, "INSERT INTO batch_items VALUES('manual','shared',1)");
        }
        current = store.Save(current.Revision, "zh-Hans", current.Fields, [library], false);
        using (var db = database.Open())
        {
            Check("disabling automatic processing expires its sole demand", Scalar<string>(db, null, "SELECT state FROM work_items WHERE id='automatic'") == "expired");
            Check("shared manual demand survives automatic cancellation", Scalar<string>(db, null, "SELECT state FROM work_items WHERE id='shared'") == "queued");
        }
        current = store.Save(current.Revision, "zh-Hans", current.Fields, [library], true);
        using (var db = database.Open()) Check("reenabling automatic processing cannot revive old authority", Scalar<string>(db, null, "SELECT state FROM work_items WHERE id='automatic'") == "expired");
        current = store.Save(current.Revision, "zh-Hans", current.Fields, [], false);
        current = store.Save(current.Revision, "zh-Hans", current.Fields, [library], true);
        using (var db = database.Open()) Check("removing and readding a scope cannot revive old work", Scalar<string>(db, null, "SELECT state FROM work_items WHERE id='shared'") == "expired");
        var epoch = current.TargetEpoch;
        current = store.Save(current.Revision, "en", current.Fields, [library], true);
        current = store.Save(current.Revision, "zh-Hans", current.Fields, [library], true);
        Check("language round trip monotonically advances target generation", current.TargetEpoch == epoch + 2);
        using (var db = database.Open()) Check("language switch leaves a durable admission marker in the settings transaction", Scalar<string>(db, null, "SELECT value FROM runtime_state WHERE key='language_pending'") == current.TargetEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var saved = content.SaveManual(field, "zh-Hans", "cached Chinese", true, "source", "settings-cache");
        var credential = Id();
        var first = store.SaveConnection(0, "https://example.invalid/v1", "model-a", 20000, 60, credential, "{\"MaxOutputTokens\":512}");
        var second = store.SaveConnection(first.Revision, first.Endpoint, "model-b", 30000, 30, credential, first.ParametersJson);
        Check("changing model preserves existing selected translation", content.Selected(field.SourceId, "zh-Hans")?.Id == saved.Id);
        Check("historical connection stays available to its request records", store.Connection(first.Id)?.Model == "model-a" && store.Connection()?.Id == second.Id);
        Reject("credential reference cannot be inherited across endpoints", () => store.SaveConnection(second.Revision, "https://another.invalid/v1", "model-b", 20000, 60, credential, "{}"));
        Reject("ordinary connection parameters reject plaintext credential fields", () => store.SaveConnection(second.Revision, second.Endpoint, "model-b", 20000, 60, credential, "{\"api_key\":\"synthetic-fixture\"}"));
        Reject("stale settings revision rejected", () => store.Save(1, "en", current.Fields, [library], false));
        Reject("same library cannot be selected twice using different Guid forms", () => store.Save(store.Read().Revision, "zh-Hans", current.Fields, [library, Guid.Parse(library).ToString("D")], true));
        var beforeGlobalEdit = store.Read(); store.Save(beforeGlobalEdit.Revision, beforeGlobalEdit.Language, beforeGlobalEdit.Fields, beforeGlobalEdit.ManagedLibraries, false);
        Reject("connection save cannot mask a concurrent global settings edit", () => store.SaveConnection(second.Revision, second.Endpoint, second.Model, second.TimeoutMs, second.RequestsPerMinute, credential, second.ParametersJson, beforeGlobalEdit.Revision));
        using (var db = database.Open()) Check("settings changes never create request records", Scalar<long>(db, null, "SELECT COUNT(*) FROM request_attempts") == 0);
        File.WriteAllText(Path.Combine(root, "settings-checks.json"), JsonSerializer.Serialize(new { status = "passed", checks = results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
