using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Localizer.Core;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record ConfirmRequest([Required] string Observation, [Required, StringLength(8000)] string OriginalTitle,
    [Required(AllowEmptyStrings = true), StringLength(500)] string DisplayPrefix);
public sealed record ManualRequest(long SourceId, [Required] string Language, Guid RequestId,
    [Required, StringLength(8000)] string Text);
public sealed record RevisionRequest(long Revision, [StringLength(8000)] string? Text = null);
public sealed record Binding(long SourceId, string Path, string ObservedOriginalTitle, string? ConfirmedDisplayName = null);

// Every route, including the partial write controller, requires server-side administrator authorization.
[ApiController, Route("MetadataLocalizer"), Authorize(Policy = Policies.RequiresElevation)]
[RequestSizeLimit(32768)]
public sealed partial class AdminController(ILibraryManager library, IApplicationHost host, IApplicationPaths paths,
    TranslationWorker worker, BatchWorker batchWorker) : ControllerBase
{
    private static readonly object Gate = new();
    private string Root => Path.Combine(paths.DataPath, "metadata-localizer");
    private CandidateStore Store() => new(Path.Combine(Root, "candidates.db"));
    private MediaKey Key(Guid folder, Guid item) => new(host.SystemId, folder.ToString("N"), item.ToString("N"));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string BindingPath(MediaKey key) => LiveSourceGuard.BindingPath(Root, key);
    private Binding? ReadBinding(MediaKey key)
    {
        var file = BindingPath(key);
        return System.IO.File.Exists(file) ? JsonSerializer.Deserialize<Binding>(System.IO.File.ReadAllText(file)) : null;
    }
    private void WriteBinding(MediaKey key, Binding value)
    {
        var file = BindingPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(true);
        }
        System.IO.File.Move(temp, file, true);
    }
    private bool FolderExists(Guid folder) => library.GetVirtualFolders().Any(x =>
        Guid.TryParse(x.ItemId, out var id) && id == folder && string.Equals(x.CollectionType?.ToString(), "movies", StringComparison.OrdinalIgnoreCase));
    private bool Belongs(Movie movie, Guid folder) => Path.IsPathFullyQualified(movie.Path)
        && library.GetCollectionFolders(movie).Select(x => x.Id).Distinct().ToArray() is var folders
        && folders.Length == 1 && folders[0] == folder;
    private Movie Resolve(Guid folder, Guid item)
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        if (library.GetItemList(new InternalItemsQuery { ItemIds = [item], IncludeItemTypes = [BaseItemKind.Movie] }).SingleOrDefault()
            is not Movie movie || !Belongs(movie, folder)) throw new KeyNotFoundException();
        return movie;
    }
    private bool Confirmed(Movie movie, SourceSnapshot? source, Binding? binding) => source is not null && binding is not null
        && binding.SourceId == source.Id && string.Equals(binding.Path, movie.Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && binding.ObservedOriginalTitle == (movie.OriginalTitle ?? "");
    private string Observation(Movie movie, SourceSnapshot? source) =>
        Observation(movie, source, source is null ? null : ReadBinding(source.Key));
    private static string Observation(Movie movie, SourceSnapshot? source, Binding? binding) => Hash(JsonSerializer.Serialize(new
        { movie.Id, movie.Name, movie.OriginalTitle, movie.Path, movie.IsLocked, movie.LockedFields, SourceId = source?.Id,
            Binding = binding }));
    private object Row(Guid folder, Movie movie, CandidateStore store)
    {
        var key = Key(folder, movie.Id);
        var source = store.GetCurrentSource(key);
        var valid = Confirmed(movie, source, ReadBinding(key));
        var original = movie.OriginalTitle ?? "";
        var prefix = original.Length > 0 && movie.Name.EndsWith(original, StringComparison.Ordinal)
            ? movie.Name[..^original.Length] : "";
        return new { movie.Id, movie.Name, OriginalTitle = original, movie.Genres, movie.IsLocked, movie.LockedFields,
            Observation = Observation(movie, source), Confirmed = valid, Source = source,
            SuggestedOriginal = valid ? source!.OriginalTitle : original,
            SuggestedPrefix = valid ? source!.DisplayPrefix : prefix };
    }
    private ActionResult Guard(Func<object> work)
    {
        lock (Gate)
        {
            try { return Ok(work()); }
            catch (KeyNotFoundException) { return NotFound(new { Error = "not_found_in_library" }); }
            catch (ArgumentException) { return BadRequest(new { Error = "invalid_input" }); }
            catch (InvalidOperationException) { return Conflict(new { Error = "changed_reload_required" }); }
        }
    }
    [HttpGet("libraries")]
    public ActionResult Libraries() => Guard(() => library.GetVirtualFolders()
        .Where(x => string.Equals(x.CollectionType?.ToString(), "movies", StringComparison.OrdinalIgnoreCase))
        .Select(x => new { Id = x.ItemId, x.Name, SaversDisabled = x.LibraryOptions?.MetadataSavers is { Length: 0 } }).ToArray());

    [HttpGet("libraries/{folder:guid}/scan")]
    public ActionResult Scan(Guid folder, [FromQuery] int start = 0) => Guard(() =>
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        if (start < 0) throw new ArgumentException();
        var raw = library.GetItemList(new InternalItemsQuery { ParentId = folder, Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie], StartIndex = start, Limit = 101 });
        var store = Store();
        return new { Items = raw.Take(100).OfType<Movie>().Where(x => Belongs(x, folder)).Select(x => Row(folder, x, store)).ToArray(),
            NextStart = raw.Count > 100 ? (int?)(start + 100) : null };
    });

    [HttpPost("libraries/{folder:guid}/items/{item:guid}/confirm")]
    public ActionResult Confirm(Guid folder, Guid item, [FromBody] ConfirmRequest request) => Guard(() =>
    {
        var movie = Resolve(folder, item);
        var store = Store();
        var key = Key(folder, item);
        // Even an unchanged source can replace its display binding. Never rebind an unsettled write.
        if (store.ReadWorkbench(Scope(folder), TargetLanguage.SimplifiedChinese).Pending.Contains(key))
            throw new OperationBusyException();
        var observedName = movie.Name;
        var observedPath = movie.Path;
        var observedOriginal = movie.OriginalTitle ?? "";
        if (Observation(movie, store.GetCurrentSource(key)) != request.Observation) throw new RevisionConflictException();
        var source = store.ObserveSource(key, request.OriginalTitle, request.DisplayPrefix);
        // Fail closed if interrupted between source persistence and the atomic binding replacement.
        WriteBinding(key, new(source.Id, observedPath, observedOriginal, observedName));
        return Row(folder, movie, store);
    });

    [HttpGet("libraries/{folder:guid}/items/{item:guid}/candidates")]
    public ActionResult Candidates(Guid folder, Guid item, [FromQuery] string language = "zh-Hans") => Guard(() =>
    {
        var movie = Resolve(folder, item);
        var store = Store();
        var source = store.GetCurrentSource(Key(folder, item));
        var target = Languages.Parse(language);
        var selected = source is null ? null : store.GetSelected(source.Id, target);
        return new { Item = Row(folder, movie, store),
            SelectedCandidateId = selected?.Candidate.Id, SelectionRevision = selected?.Revision,
            Candidates = source is null ? [] : store.ListCandidates(source.Id, target).Select(PublicCandidate).ToArray() };
    });
    private SourceSnapshot RequireSource(Guid folder, Movie movie, CandidateStore store)
    {
        var key = Key(folder, movie.Id);
        var source = store.GetCurrentSource(key);
        if (!Confirmed(movie, source, ReadBinding(key))) throw new SourceChangedException();
        return source!;
    }
    private static object PublicCandidate(Candidate candidate) => new { candidate.Id, candidate.SourceId,
        Language = candidate.Language.Tag(), InitialText = candidate.GeneratedText, candidate.Text,
        candidate.Revision, candidate.Approved, candidate.IsHumanEdited };

    [HttpPost("libraries/{folder:guid}/items/{item:guid}/candidates")]
    public ActionResult Manual(Guid folder, Guid item, [FromBody] ManualRequest request) => Guard(() =>
    {
        var store = Store();
        var source = RequireSource(folder, Resolve(folder, item), store);
        if (source.Id != request.SourceId) throw new SourceChangedException();
        if (request.RequestId == Guid.Empty) throw new ArgumentException();
        var spec = new GenerationSpec("manual://local", "human", "manual-v1", "none", "{}",
            JsonSerializer.Serialize(new { request.RequestId }), "Administrator entered this initial candidate; no model request.");
        var candidate = store.SaveGenerated(source.Id, Languages.Parse(request.Language), request.Text, spec);
        if (candidate.GeneratedText != request.Text) throw new RevisionConflictException();
        return PublicCandidate(candidate);
    });
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/candidates/{id}/edit")]
    public ActionResult Edit(Guid folder, Guid item, string id, [FromBody] RevisionRequest request) => Mutate(folder, item, id, request, false);
    [HttpPost("libraries/{folder:guid}/items/{item:guid}/candidates/{id}/approve")]
    public ActionResult Approve(Guid folder, Guid item, string id, [FromBody] RevisionRequest request) => Mutate(folder, item, id, request, true);
    private ActionResult Mutate(Guid folder, Guid item, string id, RevisionRequest request, bool approve) => Guard(() =>
    {
        var store = Store();
        var source = RequireSource(folder, Resolve(folder, item), store);
        var candidate = store.GetCandidate(id);
        if (candidate.SourceId != source.Id) throw new SourceChangedException();
        if (!approve && string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException();
        return PublicCandidate(approve ? store.Approve(id, request.Revision) : store.Edit(id, request.Revision, request.Text!));
    });
}
