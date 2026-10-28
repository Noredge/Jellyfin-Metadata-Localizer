using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Localizer.Translation;
using Localizer.Unified;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Plugin;

public sealed record UnifiedSettingsChange(long Revision, string Language, FieldOptions Fields, string[] ManagedLibraries, bool AutoEnabled);
public sealed record UnifiedConnectionChange(long Revision, string Endpoint, string Model, int TimeoutMs, int RequestsPerMinute, string ParametersJson, string? ApiKey = null, long? SettingsRevision = null);
public sealed record UnifiedModelCatalogRequest(string Endpoint, string? ApiKey = null)
{
    public override string ToString() => "UnifiedModelCatalogRequest { credentials redacted }";
}
public sealed record UnifiedPreviewRequest(long SettingsRevision, string[]? MediaIds = null, string[]? Fields = null, bool Force = false);
public sealed record UnifiedPreviewTarget(string MediaId, string Field, string SourceId, long SourceRevision, long ControlRevision, long RenderRevision, string ExpectedDisplayHash);
public sealed record UnifiedPreview(string Id, long SettingsRevision, UnifiedPreviewTarget[] Targets, bool Force);
public sealed record UnifiedSubmitRequest(string PreviewId, string CommandId);
public sealed record UnifiedFieldChange(FieldState Expected, long TargetEpoch, string Language, string Action, string ExpectedDisplay,
    string? Text = null, string? TranslationId = null, string? CommandId = null);
public sealed record UnifiedRetryRequest(string WorkId, bool AcceptUnknown = false, string? ExpectedVersion = null);
public sealed record UnifiedTermChange(string Original, string Language, long ControlRevision, string Action, string? Text, string CommandId);
public sealed record UnifiedPersonChange(string Id, long Revision, string Original, string? Chinese, string? English, string[] Aliases, bool Delete = false);
public sealed record UnifiedBindingChange(string Observation, string Original, string Prefix);
public sealed record UnifiedTermSubmission(long SettingsRevision, string CommandId, string[] Originals, bool Force = false);

[ApiController, Route("MetadataLocalizer/unified"), Authorize(Policy = Policies.RequiresElevation), RequestSizeLimit(131072)]
public sealed class UnifiedController(UnifiedRuntime runtime) : ControllerBase
{
    private SettingsStore Settings => new(runtime.Database);
    private ContentStore Data => new(runtime.Database);
    private ReadModel Read => new(runtime.Database);
    private async Task<ActionResult> Guard(Func<Task<object>> action, bool require = true)
    {
        using var timings = new Localizer.Core.OperationTimings();
        try { if (require) runtime.RequireReady(); return Ok(await action()); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return StatusCode(499); }
        catch (OperationCanceledException) { return StatusCode(504, new { Error = "request_timeout" }); }
        catch (HttpRequestException) { return StatusCode(502, new { Error = "model_catalog_unavailable" }); }
        catch (KeyNotFoundException) { return NotFound(new { Error = "item_unavailable" }); }
        catch (ConnectionParameterException error) { return BadRequest(new { Error = "invalid_connection_parameter", error.Parameter }); }
        catch (ArgumentException) { return BadRequest(new { Error = "invalid_request" }); }
        catch (JsonException) { return BadRequest(new { Error = "invalid_request" }); }
        catch (InvalidOperationException error) when (error.Message is "RevisionConflict" or "SubmissionConflict" or "PreviewExpired" or "UnknownOutcomeConsentRequired" or "RetryIntentExpired")
        { return Conflict(new { Error = error.Message }); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { return StatusCode(503, new { Error = runtime.Ready ? "operation_unavailable" : runtime.Health.Error ?? "UnifiedDatabaseRequired" }); }
        finally
        {
            // Durations only, no identities, paths, SQL or payloads. Nested phases are inclusive.
            var counts = timings.Counts();
            Response.Headers["Server-Timing"] = string.Join(",", timings.Snapshot().Select(x => x.Key + ";dur=" + x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";desc=\"" + counts[x.Key] + " calls\""));
        }
    }
    private Task<ActionResult> Guard(Func<object> action, bool require = true) => Guard(() => Task.FromResult(action()), require);
    [HttpGet("state")]
    public Task<ActionResult> State() => Guard(() => new { runtime.Ready, runtime.Health, runtime.LegacyDataPresent, Settings = runtime.Ready ? Settings.Read() : null, Libraries = runtime.Libraries(),
        ExecutionIdleCeilingMs = runtime.ExecutionIdle.Delay.TotalMilliseconds, ExecutionEmptyChecks = runtime.ExecutionIdle.EmptyChecks,
        ObservationPerformance = runtime.Ready ? runtime.Observer.Performance : null }, false);
    [HttpPost("initialize")]
    public Task<ActionResult> Initialize() => Guard(() => { runtime.Open(create: true); runtime.Notify(); return new { runtime.Ready }; }, false);
    [HttpGet("overview")]
    public Task<ActionResult> Overview(bool includeWorkHistory = false) => Guard(() => Read.Overview(includeWorkHistory));
    [HttpGet("issues")]
    public Task<ActionResult> Issues(string kind, string errorCode = "", int offset = 0, int limit = 30) => Guard(() =>
    {
        var page = Read.Issues(kind, errorCode, offset, limit);
        var names = runtime.Host.MovieNames(page.Items.Select(x => x["item_id"]).OfType<string>());
        foreach (var item in page.Items) item["name"] = item["item_id"] is string id ? names.GetValueOrDefault(id, id) : item["original"];
        return page;
    });
    [HttpPost("settings")]
    public Task<ActionResult> SaveSettings(UnifiedSettingsChange change) => Guard(() =>
    {
        runtime.ValidateLibraries(change.ManagedLibraries);
        // Validate credentials before enabling automatic HTTP. Endpoint changes require a separately saved envelope.
        if (change.AutoEnabled)
        {
            var connection = Settings.Connection() ?? throw new InvalidOperationException("connection_required");
            _ = new ServerCredentialStore(runtime.Root).Load(connection.Endpoint, connection.CredentialReference ?? "");
        }
        var saved = Settings.Save(change.Revision, change.Language, change.Fields, change.ManagedLibraries, change.AutoEnabled);
        runtime.Notify(); return saved;
    });
    [HttpPost("settings/preview")]
    public Task<ActionResult> PreviewSettings(UnifiedSettingsChange change) => Guard(async () =>
    {
        runtime.ValidateLibraries(change.ManagedLibraries);
        var current = Settings.Read(); if (current.Revision != change.Revision) throw new InvalidOperationException("RevisionConflict");
        if (change.Language is not ("zh-Hans" or "en") || !(change.Fields.Title || change.Fields.Overview || change.Fields.Genres)) throw new ArgumentException("InvalidSettings");
        var libraries = change.ManagedLibraries.Select(x => Guid.Parse(x).ToString("N")).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var item in await runtime.Host.ListAsync(libraries, HttpContext.RequestAborted)) await runtime.Observer.ObserveMediaAsync(item, HttpContext.RequestAborted);
        var proposed = current with { ManagedLibraries = libraries, Language = change.Language, Fields = change.Fields };
        var seeds = await runtime.SeedsAsync(null, null, HttpContext.RequestAborted, proposed);
        var kept = 0; var cached = 0;
        using var db = runtime.Database.Open();
        foreach (var seed in seeds)
        {
            if (Scalar<long>(db, null, "SELECT keep_original FROM display_intents WHERE media_id=$m AND field=$f", ("$m", seed.Field.MediaId), ("$f", seed.Field.Field)) == 1) kept++;
            else if (Data.Selected(seed.Field.SourceId, change.Language) is not null) cached++;
        }
        return new { Count = seeds.Length, Cached = cached, KeptOriginal = kept, Missing = seeds.Length - kept - cached, Language = change.Language };
    });
    [HttpGet("connection")]
    public Task<ActionResult> Connection() => Guard(() => ConnectionView());
    [HttpGet("connection/models")]
    public Task<ActionResult> Models() => Guard(async () => new { Models = await runtime.ListModelsAsync(HttpContext.RequestAborted), TranslationRequests = 0 });
    [HttpPost("connection/models")]
    public Task<ActionResult> DraftModels(UnifiedModelCatalogRequest change) => Guard(async () => new
    { Models = await runtime.ListModelsAsync(change.Endpoint, change.ApiKey, HttpContext.RequestAborted), TranslationRequests = 0 });
    [HttpPost("connection/test")]
    public Task<ActionResult> TestConnection(UnifiedTermSubmission change) => Guard(() =>
    {
        // A fixed diagnostic word uses the same durable term work and request accounting. No film is changed.
        return runtime.Work.SubmitTerms(change.CommandId, "connection_test", change.SettingsRevision, ["接続確認"], force: true);
    });
    [HttpPost("terms/generate")]
    public Task<ActionResult> GenerateTerms(UnifiedTermSubmission change) => Guard(() => runtime.Work.SubmitTerms(change.CommandId,
        "dictionary", change.SettingsRevision, change.Originals, change.Force));
    private object ConnectionView()
    {
        var c = Settings.Connection();
        return c is null ? new { Configured = false, Revision = 0L } : (object)new { Configured = true, c.Revision, c.Endpoint, c.Model,
            c.TimeoutMs, c.RequestsPerMinute, c.ParametersJson, HasCredential = c.CredentialReference is not null };
    }
    [HttpPost("connection")]
    public Task<ActionResult> SaveConnection(UnifiedConnectionChange change) => Guard(() =>
    {
        ConnectionParameters.Validate(change.ParametersJson);
        var endpoint = PublicCloudEndpoint.Canonicalize(change.Endpoint); var old = Settings.Connection();
        if (string.IsNullOrWhiteSpace(change.Model) || change.Model.Length > 256 || change.Model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new ArgumentException("InvalidModel");
        if ((old?.Revision ?? 0) != change.Revision) throw new InvalidOperationException("RevisionConflict");
        var reference = old?.Endpoint == endpoint ? old.CredentialReference : null;
        if (!string.IsNullOrEmpty(change.ApiKey)) reference = new ServerCredentialStore(runtime.Root).Save(endpoint, change.ApiKey);
        _ = Settings.SaveConnection(change.Revision, endpoint, change.Model, change.TimeoutMs, change.RequestsPerMinute, reference, change.ParametersJson, change.SettingsRevision);
        return ConnectionView();
    });
    [HttpGet("movies")]
    public Task<ActionResult> Movies(string search = "", string? libraryId = null, string filter = "all", int offset = 0, int limit = 30)
        => Guard(async () =>
        {
            var selected = libraryId is null ? null : Guid.Parse(libraryId).ToString("N");
            if (selected is not null) await runtime.ObserveBrowseLibraryAsync(selected, HttpContext.RequestAborted);
            var page = Read.Movies(search, selected, filter, offset, limit);
            var names = runtime.Host.MovieNames(page.Items.Select(x => (string)x["item_id"]!));
            foreach (var item in page.Items) if (names.TryGetValue((string)item["item_id"]!, out var name)) item["name"] = name;
            return page;
        });
    private MediaIdentity Identity(string id)
    {
        using var db = runtime.Database.OpenRead(); using var cmd = Command(db, null, "SELECT server_id,library_id,item_id FROM media_items WHERE id=$id", ("$id", id));
        using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetString(2)) : throw new KeyNotFoundException();
    }
    [HttpGet("movies/{id}")]
    public Task<ActionResult> Movie(string id) => Guard(async () =>
    {
        var key = Identity(id); var fields = new List<object>(); var details = Data.ReadDetails(id);
        using var sourceRead = runtime.Host.BeginReadScope(key, details.Settings);
        foreach (var field in new[] { "Name", "Overview", "Genres" })
        {
            var observation = await runtime.Host.ReadAsync(key, field, HttpContext.RequestAborted);
            var current = details.Fields.SingleOrDefault(x => x.State.Field == field);
            if (current is null) continue;
            fields.Add(new { current.State, Observation = observation, current.Selection, current.Intent });
        }
        return new { Id = id, Key = key, Fields = fields, details.Settings };
    });
    [HttpGet("history")]
    public Task<ActionResult> History(string mediaId, string field, string language, string? sourceId = null, int offset = 0, int limit = 20) => Guard(() =>
    {
        var current = Data.ReadField(mediaId, field) ?? throw new KeyNotFoundException();
        using var db = runtime.Database.Open();
        var sources = ReadModel.Rows(db, "SELECT id,content,created_utc FROM source_versions WHERE owner_id=$id AND kind=$f ORDER BY created_utc DESC,id DESC", ("$id", mediaId), ("$f", field));
        var selectedSource = sourceId ?? current.SourceId;
        if (!sources.Any(x => (string)x["id"]! == selectedSource)) throw new KeyNotFoundException();
        return new { CurrentSourceId = current.SourceId, Sources = sources, Versions = Data.History(selectedSource, language, offset, limit), Offset = offset, Limit = limit };
    });
    [HttpGet("movies/{id}/binding")]
    public Task<ActionResult> Binding(string id) => Guard(async () => await runtime.Host.TitleBindingAsync(Identity(id), HttpContext.RequestAborted));
    [HttpPost("movies/{id}/binding")]
    public Task<ActionResult> ConfirmBinding(string id, UnifiedBindingChange change) => Guard(async () =>
    {
        var identity = Identity(id);
        await runtime.Host.ConfirmTitleAsync(identity, change.Observation, change.Original, change.Prefix, HttpContext.RequestAborted);
        if (runtime.Host.FindMedia(Guid.Parse(identity.ItemId)) is { } item) await runtime.Observer.ObserveMediaAsync(item, HttpContext.RequestAborted);
        runtime.Notify(); return new { Saved = true };
    });
    [HttpPost("field")]
    public Task<ActionResult> Field(UnifiedFieldChange change) => Guard(() =>
    {
        var settings = Settings.Read();
        if (settings.TargetEpoch != change.TargetEpoch || settings.Language != change.Language) throw new InvalidOperationException("RevisionConflict");
        var f = change.Expected;
        var identity = Identity(f.MediaId);
        if (change.Action != "save" && (!settings.ManagedLibraries.Contains(identity.LibraryId, StringComparer.Ordinal) || !settings.Fields.Includes(f.Field)))
            throw new InvalidOperationException("OutsideManagedScope");
        switch (change.Action)
        {
            case "save": case "show":
                _ = Data.SaveManual(f, change.Language, change.Text ?? "", change.Action == "show", change.ExpectedDisplay, change.CommandId ?? throw new ArgumentException("CommandRequired")); break;
            case "choose": Data.Choose(f, change.Language, change.TranslationId ?? "", change.ExpectedDisplay); break;
            case "keep": Data.KeepOriginal(f, change.ExpectedDisplay); break;
            case "restore": Data.RestoreOriginal(f, change.ExpectedDisplay); break;
            case "follow":
                Data.FollowGlobal(f); var current = Data.ReadField(f.MediaId, f.Field)!;
                runtime.Work.Submit(change.CommandId ?? Guid.NewGuid().ToString("N"), "manual", settings.Revision, [new(current, change.ExpectedDisplay)]); break;
            default: throw new ArgumentException("InvalidAction");
        }
        runtime.Work.AdmitDisplayIntents(); runtime.Notify(); return new { Saved = true };
    });
    [HttpPost("preview")]
    public Task<ActionResult> Preview(UnifiedPreviewRequest change) => Guard(async () =>
    {
        if (runtime.ScanRunning) throw new InvalidOperationException("library_scan_running");
        var settings = Settings.Read(); if (settings.Revision != change.SettingsRevision) throw new InvalidOperationException("RevisionConflict");
        if (change.Fields?.Any(f => f is not ("Name" or "Overview" or "Genres")) == true) throw new ArgumentException("InvalidField");
        WorkSeed[] seeds; long skipped = 0; var changed = 0;
        if (change.Force)
        {
            await runtime.Observer.ObserveAllAsync(HttpContext.RequestAborted);
            seeds = await runtime.SeedsAsync(change.MediaIds, change.Fields, HttpContext.RequestAborted);
        }
        else (seeds, skipped, changed) = await runtime.MissingSeedsAsync(change.MediaIds, change.Fields, HttpContext.RequestAborted);
        var preview = new UnifiedPreview(Id(), settings.Revision, seeds.Select(x => new UnifiedPreviewTarget(x.Field.MediaId, x.Field.Field,
            x.Field.SourceId, x.Field.SourceRevision, x.Field.ControlRevision, x.Field.RenderRevision, Hash(x.ExpectedDisplay))).ToArray(), change.Force);
        var json = JsonSerializer.Serialize(preview);
        runtime.Database.Write((db, tx) =>
        {
            Execute(db, tx, "DELETE FROM previews WHERE expires_utc<$n", ("$n", Now()));
            return Execute(db, tx, "INSERT INTO previews VALUES($id,$hash,$expires,$json)", ("$id", preview.Id), ("$hash", Hash(json)), ("$expires", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")), ("$json", json));
        }, wakeExecutor: false);
        var cached = seeds.Count(s => !change.Force && Data.Selected(s.Field.SourceId, settings.Language) is not null);
        return new { preview.Id, Count = seeds.Length, Cached = cached, Missing = seeds.Length - cached, Skipped = skipped, SourceChanged = changed, Forced = change.Force, ExpiresInSeconds = 300 };
    });
    [HttpPost("submit")]
    public Task<ActionResult> Submit(UnifiedSubmitRequest request) => Guard(async () =>
    {
        string json;
        using (var db = runtime.Database.Open())
            json = Scalar<string>(db, null, "SELECT summary_json FROM previews WHERE id=$id AND expires_utc>$n", ("$id", request.PreviewId), ("$n", Now())) ?? throw new InvalidOperationException("PreviewExpired");
        var preview = JsonSerializer.Deserialize<UnifiedPreview>(json)!;
        var kind = preview.Force ? "retranslate" : "fill";
        if (runtime.Work.TryReplay(request.CommandId, kind, preview.SettingsRevision, preview.Targets.Select(x => (x.MediaId, x.Field)).ToArray(), preview.Force) is { } replay) return replay;
        var seeds = new List<WorkSeed>();
        foreach (var target in preview.Targets)
        {
            var current = Data.ReadField(target.MediaId, target.Field) ?? throw new KeyNotFoundException();
            if (current.SourceId != target.SourceId || current.SourceRevision != target.SourceRevision || current.ControlRevision != target.ControlRevision || current.RenderRevision != target.RenderRevision)
                throw new InvalidOperationException("RevisionConflict");
            var actual = await runtime.Host.ReadAsync(Identity(target.MediaId), target.Field, HttpContext.RequestAborted);
            if (!actual.Present || actual.SourceError is not null || Hash(actual.Display) != target.ExpectedDisplayHash) throw new InvalidOperationException("RevisionConflict");
            seeds.Add(new(current, actual.Display));
        }
        var receipt = runtime.Work.Submit(request.CommandId, kind, preview.SettingsRevision, [.. seeds], preview.Force);
        runtime.Notify(); return receipt;
    });
    [HttpGet("work")]
    public Task<ActionResult> Work(string? batchId = null, int offset = 0, int limit = 30, string? workId = null) => Guard(() =>
    {
        var rows = Read.Work(batchId, offset, limit, workId);
        var names = runtime.Host.MovieNames(rows.Select(x => x["item_id"] as string).OfType<string>());
        foreach (var row in rows) if (row["item_id"] is string id && names.TryGetValue(id, out var name)) row["movie_name"] = name;
        return rows;
    });
    [HttpGet("work/{workId}")]
    public Task<ActionResult> WorkDetail(string workId) => Guard(() => Read.WorkDetail(workId) ?? throw new KeyNotFoundException());
    [HttpGet("attempts")]
    public Task<ActionResult> Attempts(string workId, int offset = 0, int limit = 20) => Guard(() => Read.Attempts(workId, offset, limit));
    [HttpPost("cancel/{id}")]
    public Task<ActionResult> Cancel(string id) => Guard(() => { runtime.Work.CancelBatch(id); return new { Cancelled = true }; });
    [HttpPost("retry")]
    public Task<ActionResult> Retry(UnifiedRetryRequest request) => Guard(() => { var id = runtime.Work.Retry(request.WorkId, request.AcceptUnknown, request.ExpectedVersion); return new { Retried = true, WorkId = id, Reused = id != request.WorkId }; });
    [HttpGet("terms")]
    public Task<ActionResult> Terms(string search = "", int offset = 0, int limit = 30) => Guard(() => new Localizer.Unified.ConfigurationStore(runtime.Database).Terms(search, offset, limit));
    [HttpPost("terms")]
    public Task<ActionResult> SaveTerm(UnifiedTermChange change) => Guard(() =>
    {
        var store = new Localizer.Unified.ConfigurationStore(runtime.Database);
        if (change.Action == "builtin") store.RestoreBuiltin(change.Original, change.Language, change.ControlRevision);
        else if (change.Action == "save") _ = store.SaveTerm(change.Original, change.Language, change.Text ?? "", change.ControlRevision, change.CommandId);
        else throw new ArgumentException("InvalidAction");
        return new { Saved = true };
    });
    [HttpGet("people")]
    public Task<ActionResult> People(string search = "", int offset = 0, int limit = 30) => Guard(() => new Localizer.Unified.ConfigurationStore(runtime.Database).People(search, offset, limit));
    [HttpPost("people")]
    public Task<ActionResult> Person(UnifiedPersonChange change) => Guard(() =>
    {
        var store = new Localizer.Unified.ConfigurationStore(runtime.Database);
        if (change.Delete) store.DeletePerson(change.Id, change.Revision);
        else _ = store.SavePerson(change.Id, change.Revision, change.Original, change.Chinese, change.English, change.Aliases);
        return new { Saved = true };
    });
}
