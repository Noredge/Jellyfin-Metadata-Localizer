using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Localizer.Core;
using Localizer.Jellyfin;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record GenreBinding(long SourceId, string Path, string? NfoPath = null, string? NfoFingerprint = null);
public sealed record GenreConfirmRequest([Required] string Observation, [Required, MaxLength(200)] string[] OriginalValues);
public sealed record GenreOverrideRequest(long SourceId, [Required] string Language,
    [Required, StringLength(500)] string Original, long Revision, [StringLength(500)] string? Replacement,
    bool ConfirmSharedDictionary);
public sealed record GenreApplyRequest(Guid OperationId, Guid PreviewId, bool ConfirmApply, bool AcceptUnknownValues = false);
public sealed record SavedGenrePreview(Guid Id, DateTimeOffset CreatedUtc, GenrePreviewEntry Entry);

public sealed partial class AdminController
{
    private string GenreBindingPath(MediaKey key) => Path.Combine(Root, "genre-bindings", Hash(JsonSerializer.Serialize(key)) + ".json");
    private GenreBinding? ReadGenreBinding(MediaKey key) => System.IO.File.Exists(GenreBindingPath(key))
        ? JsonSerializer.Deserialize<GenreBinding>(System.IO.File.ReadAllText(GenreBindingPath(key))) : null;
    private bool GenreConfirmed(Movie movie, GenreSource? source, GenreBinding? binding) => source is not null
        && binding?.SourceId == source.Id && string.Equals(binding.Path, movie.Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private void WriteGenreBinding(MediaKey key, GenreBinding binding)
    {
        var path = GenreBindingPath(key); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, binding); stream.Flush(true); }
        System.IO.File.Move(path + ".tmp", path, true);
    }
    private static NfoGenreRead ReadNfoGenres(Movie movie) => NfoGenres.Read(movie.Path, movie.GetAdditionalParts().Select(x => x.Path));
    private bool NfoBindingCurrent(GenreBinding binding, MediaKey key)
    {
        if (binding.NfoPath is null) return true; // Existing explicitly confirmed sources remain supported.
        var nfo = ReadNfoGenres(Resolve(Guid.Parse(key.LibraryId), Guid.Parse(key.ItemId)));
        return nfo.Error is null && nfo.Path == binding.NfoPath && nfo.Fingerprint == binding.NfoFingerprint;
    }
    private string GenreObservation(Movie movie, GenreSource? source) => Hash(JsonSerializer.Serialize(new
        { movie.Id, movie.Path, Values = GenreValues.Canonical(movie.Genres), movie.IsLocked, movie.LockedFields, SourceId = source?.Id }));
    private GenreSource RequireGenreSource(Guid folder, Movie movie, CandidateStore store)
    {
        var key = Key(folder, movie.Id); var source = store.GetCurrentGenreSource(key);
        if (!GenreConfirmed(movie, source, ReadGenreBinding(key))) throw new SourceChangedException();
        return source!;
    }
    private JellyfinGenreTarget GenreTarget(Guid folder, CandidateStore store) => new(library, host,
        () => new HashSet<Guid> { folder }, key =>
        {
            var source = store.GetCurrentGenreSource(key); var binding = ReadGenreBinding(key);
            return source is null || binding?.SourceId != source.Id || !NfoBindingCurrent(binding, key) ? null : binding.Path;
        });
    private static object PublicGenreOperation(GenreOperation op) => new { op.Id, Kind = op.Kind.ToString(),
        State = op.State.ToString(), Language = op.Language.Tag(), op.BeforeValues, op.PlannedValues,
        op.ParentApplyId, op.ObservedOnly, op.ErrorCategory, op.CreatedUtc, op.UpdatedUtc };
    private static GenreOperation OwnedGenreOperation(CandidateStore store, Guid id, MediaKey key)
    {
        Nonempty(id); var operation = store.GetGenreOperation(id.ToString("N"));
        if (operation.Key != key) throw new KeyNotFoundException();
        return operation;
    }
    private object GenreView(Guid folder, Movie movie, CandidateStore store, TargetLanguage language)
    {
        var key = Key(folder, movie.Id); var source = store.GetCurrentGenreSource(key);
        var valid = GenreConfirmed(movie, source, ReadGenreBinding(key));
        var point = store.GetGenreRestorePoint(key);
        return new { CurrentValues = movie.Genres, Confirmed = valid, Observation = GenreObservation(movie, source),
            Source = source is null ? null : new { source.Id, source.Version, source.OriginalValues },
            SuggestedOriginalValues = valid ? source!.OriginalValues : movie.Genres,
            Language = language.Tag(), Dictionary = source is null ? [] : source.OriginalValues.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).Select(x => store.GetGenreDictionaryEntry(language, x)).ToArray(),
            Pending = store.ListUnresolvedGenreOperations().Where(x => x.Key == key).Select(PublicGenreOperation).ToArray(),
            Recent = store.ListGenreOperations(key).Select(PublicGenreOperation).ToArray(),
            RestorePoint = point is null ? null : new { Application = PublicGenreOperation(point.Application), point.Consumed } };
    }

    [HttpGet("libraries/{folder:guid}/items/{item:guid}/genres")]
    public ActionResult Genres(Guid folder, Guid item, [FromQuery] string language = "zh-Hans") => Guard(() =>
        GenreView(folder, Resolve(folder, item), Store(), Languages.Parse(language)));

    [HttpPost("libraries/{folder:guid}/items/{item:guid}/genres/confirm")]
    public ActionResult ConfirmGenres(Guid folder, Guid item, [FromBody] GenreConfirmRequest request) => Guard(() =>
    {
        if (request.OriginalValues.Any(x => x is null || x.Length > 500 || x.Contains('\n') || x.Contains('\r'))) throw new ArgumentException();
        var movie = Resolve(folder, item); var store = Store(); var key = Key(folder, item);
        if (GenreObservation(movie, store.GetCurrentGenreSource(key)) != request.Observation) throw new RevisionConflictException();
        if (store.ListUnresolvedGenreOperations().Any(x => x.Key == key)) throw new OperationBusyException();
        var source = store.ObserveGenreSource(key, request.OriginalValues);
        WriteGenreBinding(key, new(source.Id, movie.Path));
        return new { source.Id, source.Version, source.OriginalValues };
    });

    [HttpPost("libraries/{folder:guid}/items/{item:guid}/genres/dictionary")]
    public ActionResult GenreDictionaryOverride(Guid folder, Guid item, [FromBody] GenreOverrideRequest request) => Guard(() =>
    {
        if (!request.ConfirmSharedDictionary || request.Replacement?.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException();
        var store = Store(); var source = RequireGenreSource(folder, Resolve(folder, item), store);
        if (source.Id != request.SourceId || !source.OriginalValues.Contains(request.Original, StringComparer.Ordinal)) throw new SourceChangedException();
        var language = Languages.Parse(request.Language);
        store.SetGenreOverride(language, request.Original, request.Revision, request.Replacement);
        return store.GetGenreDictionaryEntry(language, request.Original);
    });

    private string GenrePreviewPath(Guid id) => Path.Combine(Root, "genre-previews", id.ToString("N") + ".json");
    private SavedGenrePreview ReadGenrePreview(Guid id, MediaKey key)
    {
        Nonempty(id); var path = GenrePreviewPath(id);
        if (!System.IO.File.Exists(path)) throw new KeyNotFoundException();
        var saved = JsonSerializer.Deserialize<SavedGenrePreview>(System.IO.File.ReadAllText(path))!;
        if (saved.Id != id || saved.Entry.Key != key) throw new KeyNotFoundException();
        return saved;
    }
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/genres/preview")]
    public Task<ActionResult> PreviewGenres(Guid folder, Guid item, [FromBody] NamePreviewRequest request) => GuardAsync(async () =>
    {
        Resolve(folder, item); var store = Store();
        var current = await GenreTarget(folder, store).ReadAsync(Key(folder, item), HttpContext.RequestAborted);
        if (current is null) throw new SourceChangedException();
        var preview = store.BuildGenrePreview(Scope(folder), Languages.Parse(request.Language), current);
        var saved = new SavedGenrePreview(Guid.NewGuid(), DateTimeOffset.UtcNow, preview);
        Directory.CreateDirectory(Path.GetDirectoryName(GenrePreviewPath(saved.Id))!);
        using (var stream = new FileStream(GenrePreviewPath(saved.Id), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, saved); stream.Flush(true); }
        return new { PreviewId = saved.Id, Status = preview.Status.ToString(), Language = preview.Language.Tag(),
            preview.ExpectedValues, preview.ProposedValues, preview.UnknownValues, current.MetadataSaversExplicitlyDisabled,
            CanApply = current.MetadataSaversExplicitlyDisabled && preview.Status == PreviewStatus.ReadyForReview };
    });
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/genres/apply")]
    public Task<ActionResult> ApplyGenres(Guid folder, Guid item, [FromBody] GenreApplyRequest request) => GuardAsync(async () =>
    {
        Resolve(folder, item); Nonempty(request.OperationId);
        if (!request.ConfirmApply) throw new ArgumentException();
        var store = Store(); var saved = ReadGenrePreview(request.PreviewId, Key(folder, item));
        if (saved.Entry.UnknownValues.Length > 0 && !request.AcceptUnknownValues) throw new ArgumentException();
        if (store.FindGenreOperation(request.OperationId.ToString("N")) is null
            && ReadGenreBinding(saved.Entry.Key) is { } binding && !NfoBindingCurrent(binding, saved.Entry.Key))
            throw new OperationValidationException("genre_nfo_changed_retry");
        if (store.FindGenreOperation(request.OperationId.ToString("N")) is null && saved.CreatedUtc < DateTimeOffset.UtcNow.AddHours(-24))
            throw new RevisionConflictException();
        return PublicGenreOperation(await new GenreOperationService(store, GenreTarget(folder, store))
            .ApplyAsync(request.OperationId.ToString("N"), Scope(folder), saved.Entry, HttpContext.RequestAborted));
    });
    [HttpGet("libraries/{folder:guid}/items/{item:guid}/genres/operations/{id:guid}")]
    public ActionResult GenreOperationStatus(Guid folder, Guid item, Guid id) => Guard(() =>
    {
        Resolve(folder, item); return PublicGenreOperation(OwnedGenreOperation(Store(), id, Key(folder, item)));
    });
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/genres/restore")]
    public Task<ActionResult> RestoreGenres(Guid folder, Guid item, [FromBody] NameRestoreRequest request) => GuardAsync(async () =>
    {
        Resolve(folder, item); Nonempty(request.OperationId);
        if (!request.ConfirmRestore) throw new ArgumentException();
        var store = Store(); var parent = OwnedGenreOperation(store, request.ApplicationId, Key(folder, item));
        return PublicGenreOperation(await new GenreOperationService(store, GenreTarget(folder, store))
            .RestoreAsync(request.OperationId.ToString("N"), Scope(folder), parent.Id, request.AcceptUncertainAttribution, HttpContext.RequestAborted));
    });
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/genres/operations/{id:guid}/reconcile")]
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/genres/operations/{id:guid}/resume")]
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/genres/operations/{id:guid}/cancel")]
    public Task<ActionResult> RecoverGenres(Guid folder, Guid item, Guid id, [FromBody] NameRecoveryRequest request) => GuardAsync(async () =>
    {
        Resolve(folder, item); var store = Store(); var op = OwnedGenreOperation(store, id, Key(folder, item));
        var service = new GenreOperationService(store, GenreTarget(folder, store));
        var action = HttpContext.Request.Path.Value!.Split('/').Last();
        var result = action switch
        {
            "reconcile" => await service.ReconcileAsync(op.Id, HttpContext.RequestAborted),
            "cancel" => await service.CancelPendingAsync(op.Id, HttpContext.RequestAborted),
            "resume" when request.ConfirmResume => await service.ResumeAsync(op.Id, Scope(folder), HttpContext.RequestAborted),
            _ => throw new ArgumentException()
        };
        return PublicGenreOperation(result);
    });
}
