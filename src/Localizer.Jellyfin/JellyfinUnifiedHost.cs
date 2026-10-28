using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Jellyfin.Data.Enums;
using Localizer.Core;
using Localizer.Unified;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using static Localizer.Unified.UnifiedDatabase;

namespace Localizer.Jellyfin;

public sealed record UnifiedTitleBindingView(string Observation, string MediaPath, string ObservedOriginalTitle, string Display, string? Original, string Prefix);

/// <summary>Native read/write boundary. Only the plugin database and verified NFO sources supply originals.</summary>
public sealed class JellyfinUnifiedHost(ILibraryManager library, IApplicationHost host, UnifiedDatabase database)
    : IUnifiedHost, IUnifiedInventory, IUnifiedObservationHints, IUnifiedReadScope
{
    private readonly SettingsStore settings = new(database);
    private readonly ContentStore content = new(database);
    private readonly AsyncLocal<MediaReadScope?> currentRead = new();
    private sealed class MediaReadScope(JellyfinUnifiedHost owner, MediaIdentity key, MediaReadScope? previous) : IDisposable
    {
        public MediaIdentity Key { get; } = key;
        public NfoReadSession Nfo { get; } = new();
        public UnifiedSettings? Settings { get; set; }
        public string? ResolvedPath { get; set; }
        public (Movie Movie, bool SaversDisabled)? Resolved { get; set; }
        public string[]? People { get; set; }
        public bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; owner.currentRead.Value = previous; }
    }
    public IDisposable BeginReadScope(MediaIdentity key, UnifiedSettings? settings = null)
    { var scope = new MediaReadScope(this, key, currentRead.Value) { Settings = settings }; currentRead.Value = scope; return scope; }
    private MediaReadScope? Scope(MediaIdentity key) => currentRead.Value is { Disposed: false } scope && scope.Key == key ? scope : null;
    private (Movie Movie, bool SaversDisabled)? Resolve(MediaIdentity key, HashSet<Guid> scopes, string? path, MediaReadScope? scope)
    {
        if (scope is not null && scope.ResolvedPath == path && scope.Resolved is not null) return scope.Resolved;
        using var timing = OperationTimings.Measure("host_resolve");
        var resolved = JellyfinMovieAccess.Resolve(library, host, scopes, new(key.ServerId, key.LibraryId, key.ItemId), path);
        if (scope is not null) { scope.ResolvedPath = path; scope.Resolved = resolved; }
        return resolved;
    }
    private string[] People(Movie movie, MediaReadScope? scope)
    {
        if (scope?.People is { } names) return names;
        using var timing = OperationTimings.Measure("host_people");
        names = library.GetPeople(movie).Select(x => x.Name).ToArray();
        if (scope is not null) scope.People = names; return names;
    }
    public IReadOnlyDictionary<string, string> MovieNames(IEnumerable<string> itemIds)
    {
        using var timing = OperationTimings.Measure("movie_names");
        var ids = itemIds.Select(Guid.Parse).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<string, string>();
        if (ids.Length > 100) throw new ArgumentException("PageLimitExceeded");
        return library.GetItemList(new InternalItemsQuery { ItemIds = ids, IncludeItemTypes = [BaseItemKind.Movie] })
            .OfType<Movie>().ToDictionary(x => x.Id.ToString("N"), x => x.Name, StringComparer.Ordinal);
    }
    public async Task<UnifiedTitleBindingView> TitleBindingAsync(MediaIdentity key, CancellationToken token)
    {
        var actual = await ReadAsync(key, "Name", token);
        if (!actual.Present) throw new InvalidOperationException("SourceBindingUnavailable");
        var scopes = settings.Read().ManagedLibraries.Select(Guid.Parse).ToHashSet();
        var resolved = JellyfinMovieAccess.Resolve(library, host, scopes, new(key.ServerId, key.LibraryId, key.ItemId), actual.MediaPath)
            ?? throw new InvalidOperationException("SourceBindingUnavailable");
        var observed = resolved.Movie.OriginalTitle ?? "";
        var mid = Hash(JsonSerializer.Serialize(new[] { key.ServerId, key.LibraryId, key.ItemId }));
        var state = content.ReadField(mid, "Name");
        return new(Hash(JsonSerializer.Serialize(new { key, actual.MediaPath, observed, actual.Display, state?.SourceRevision, state?.ControlRevision, state?.RenderRevision })),
            actual.MediaPath, observed, actual.Display, actual.Original, actual.Prefix);
    }
    public async Task ConfirmTitleAsync(MediaIdentity key, string observation, string original, string prefix, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(original) || original.Length > 8000 || prefix.Length > 500 || original.Contains('\0') || prefix.Contains('\0')) throw new ArgumentException("InvalidOriginal");
        await JellyfinMovieAccess.Writer.WaitAsync(token);
        try
        {
            var current = await TitleBindingAsync(key, token);
            if (current.Observation != observation) throw new InvalidOperationException("RevisionConflict");
            var mid = Hash(JsonSerializer.Serialize(new[] { key.ServerId, key.LibraryId, key.ItemId }));
            database.Write((db, tx) =>
            {
                var prior = Scalar<string>(db, tx, "SELECT path_evidence FROM media_items WHERE id=$m", ("$m", mid));
                var evidence = JsonNode.Parse(prior ?? "{}")!.AsObject();
                evidence["native_title"] = JsonSerializer.SerializeToNode(new { path = current.MediaPath, observed_original = current.ObservedOriginalTitle, original, prefix });
                Execute(db, tx, "UPDATE media_items SET path_evidence=$e WHERE id=$m", ("$m", mid), ("$e", evidence.ToJsonString()));
                Execute(db, tx, "UPDATE media_fields SET control_revision=control_revision+1,availability='unreadable',observation_error='binding_observation_pending' WHERE media_id=$m AND field='Name'", ("$m", mid));
                Execute(db, tx, "UPDATE work_items SET state='expired',completion_reason='source_binding_changed' WHERE media_id=$m AND field='Name' AND state IN('queued','running','waiting','paused')", ("$m", mid));
                return true;
            });
        }
        finally { JellyfinMovieAccess.Writer.Release(); }
    }
    public HostMedia? FindMedia(Guid itemId, bool includeUnmanaged = false)
    {
        if (library.GetItemList(new InternalItemsQuery { ItemIds = [itemId], IncludeItemTypes = [BaseItemKind.Movie] }).SingleOrDefault() is not Movie movie
            || !Path.IsPathFullyQualified(movie.Path)) return null;
        var collections = library.GetCollectionFolders(movie).Select(x => x.Id).Distinct().ToArray();
        if (collections.Length != 1 || !includeUnmanaged && !settings.Read().ManagedLibraries.Contains(collections[0].ToString("N"), StringComparer.Ordinal)) return null;
        return new(new(host.SystemId, collections[0].ToString("N"), movie.Id.ToString("N")), movie.Path);
    }
    public Task<HostMedia[]> ListAsync(string[] libraryIds, CancellationToken token)
    {
        var result = new List<HostMedia>();
        foreach (var id in libraryIds)
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParse(id, out var folderId)) throw new ArgumentException("InvalidLibrary");
            foreach (var movie in library.GetItemList(new InternalItemsQuery { ParentId = folderId, Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie] }).OfType<Movie>())
            {
                token.ThrowIfCancellationRequested();
                var collections = library.GetCollectionFolders(movie).Select(x => x.Id).Distinct().ToArray();
                if (collections.Length != 1 || collections[0] != folderId || !Path.IsPathFullyQualified(movie.Path)) continue;
                result.Add(new(new(host.SystemId, folderId.ToString("N"), movie.Id.ToString("N")), movie.Path));
            }
        }
        return Task.FromResult(result.ToArray());
    }
    public Task<HostFieldObservation> ReadAsync(MediaIdentity key, string field, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); return Task.FromResult(Read(key, field).Observation);
    }
    public ValueTask<string?> ObservationFingerprintAsync(HostMedia item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var scope = Scope(item.Key); var nfoRead = scope?.Nfo ?? new NfoReadSession();
            var resolved = Resolve(item.Key, new HashSet<Guid> { Guid.Parse(item.Key.LibraryId) }, item.MediaPath, scope);
            if (resolved is null) return ValueTask.FromResult<string?>(null);
            var (movie, savers) = resolved.Value;
            var directory = Path.GetDirectoryName(item.MediaPath)!;
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var files = nfoRead.Files(directory).Select(Path.GetFileName).Order(comparer).ToArray();
            var nfos = new List<object>();
            foreach (var path in new[] { Path.ChangeExtension(item.MediaPath, ".nfo"), Path.Combine(directory, "movie.nfo") }.Distinct(comparer))
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(path)) { nfos.Add(new { path, missing = true }); continue; }
                var digest = nfoRead.Digest(path);
                if (digest is null) return ValueTask.FromResult<string?>(null);
                nfos.Add(new { path, digest });
            }
            // Hash content rather than trusting mtime/length. Same-size NFO edits are visible too.
            return ValueTask.FromResult<string?>(Hash(JsonSerializer.Serialize(new
            {
                item.Key, item.MediaPath, mediaExists = File.Exists(item.MediaPath), movie.Name, movie.OriginalTitle, movie.Overview, movie.Genres,
                movie.IsLocked, movie.LockedFields, savers, files, nfos,
                parts = movie.GetAdditionalParts().Select(x => x.Path).Order(comparer).ToArray(),
                people = People(movie, scope).Order(StringComparer.Ordinal).ToArray()
            })));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return ValueTask.FromResult<string?>(null); }
    }
    private (Movie? Movie, HostFieldObservation Observation) Read(MediaIdentity key, string field, bool fresh = false)
    {
        if (field is not ("Name" or "Overview" or "Genres")) throw new ArgumentException("InvalidField");
        string? path; string evidence;
        var scope = fresh ? null : Scope(key);
        using (var db = database.OpenRead())
        using (var cmd = Command(db, null, "SELECT media_path,path_evidence FROM media_items WHERE server_id=$s AND library_id=$l AND item_id=$i",
            ("$s", key.ServerId), ("$l", key.LibraryId), ("$i", key.ItemId)))
        using (var r = cmd.ExecuteReader())
        { if (!r.Read()) return (null, new(key, "", field, null, "", Present: false, Writable: false)); path = r.GetString(0); evidence = r.GetString(1); }
        var snapshot = scope?.Settings ?? settings.Read(); if (scope is not null) scope.Settings = snapshot;
        var scopes = snapshot.ManagedLibraries.Select(Guid.Parse).ToHashSet();
        if (Guid.TryParse(key.LibraryId, out var browsingLibrary)) scopes.Add(browsingLibrary);
        var resolved = Resolve(key, scopes, path, scope);
        if (resolved is null) return (null, new(key, path, field, null, "", Present: false, Writable: false, SourceError: "source_binding_changed"));
        var (movie, savers) = resolved.Value;
        var metadataField = field == "Name" ? MetadataField.Name : field == "Overview" ? MetadataField.Overview : MetadataField.Genres;
        var displayError = movie.IsLocked || movie.LockedFields.Contains(metadataField) ? "field_locked" : !savers ? "metadata_savers_not_disabled" : null;
        var display = field == "Name" ? movie.Name : field == "Overview" ? movie.Overview ?? "" : JsonSerializer.Serialize(movie.Genres);
        string? original = null, error = null; var prefix = ""; object proof = new { media_path = movie.Path };
        var mid = Hash(JsonSerializer.Serialize(new[] { key.ServerId, key.LibraryId, key.ItemId }));
        var current = content.ReadField(mid, field);
        using var binding = JsonDocument.Parse(string.IsNullOrEmpty(evidence) ? "{}" : evidence);
        if (binding.RootElement.TryGetProperty("path_conflict", out var conflict) && conflict.ValueKind == JsonValueKind.True) error = "source_binding_changed";
        else if (field == "Name")
        {
            var observed = movie.OriginalTitle ?? "";
            string? paired = null;
            if (current is not null)
            {
                using var db = database.OpenRead();
                var sourceEvidence = Scalar<string>(db, null, "SELECT evidence_json FROM source_versions WHERE id=$id", ("$id", current.SourceId));
                if (sourceEvidence is not null)
                {
                    using var source = JsonDocument.Parse(sourceEvidence);
                    if (source.RootElement.TryGetProperty("observed_original", out var value)) paired = value.GetString();
                }
            }
            if (paired is null && binding.RootElement.TryGetProperty("bindings", out var titleBinding))
            {
                if (!SamePath(titleBinding.GetProperty("Path").GetString()!, movie.Path)) error = "source_binding_changed";
                paired = titleBinding.GetProperty("ObservedOriginalTitle").GetString();
            }
            var trustedPrior = current is { Content.Length: > 0 } && paired is not null;
            original = trustedPrior && paired == observed ? current!.Content : observed;
            prefix = trustedPrior ? current!.Prefix : "";
            var explicitBinding = binding.RootElement.TryGetProperty("native_title", out var native)
                && SamePath(native.GetProperty("path").GetString()!, movie.Path) && native.GetProperty("observed_original").GetString() == observed;
            if (explicitBinding) { original = native.GetProperty("original").GetString()!; prefix = native.GetProperty("prefix").GetString()!; trustedPrior = true; }
            // A reset to a trusted original can also supply a changed catalog prefix.
            var reset = original.Length > 0 && movie.Name.EndsWith(original, StringComparison.Ordinal)
                && movie.Name.Length - original.Length <= 500;
            if (reset && !explicitBinding) prefix = movie.Name[..^original.Length];
            if (original.Length is 0 or > 8000 || !trustedPrior && !reset) error ??= "original_title_unavailable";
            proof = new { media_path = movie.Path, observed_original = observed, original_binding = "native-original-title" };
        }
        else if (field == "Overview")
        {
            var read = NfoOverview.Read(movie.Path, movie.GetAdditionalParts().Select(x => x.Path), scope?.Nfo); original = read.Error == "overview_empty" ? "" : read.Text;
            error = read.Error == "overview_empty" ? null : read.Error;
            proof = new { media_path = movie.Path, nfo_path = read.Path, content_hash = read.Hash };
        }
        else
        {
            if (binding.RootElement.TryGetProperty("genre-bindings", out var genreBinding)
                && (!genreBinding.TryGetProperty("NfoPath", out var nfo) || nfo.ValueKind == JsonValueKind.Null))
            {
                if (current is null || !SamePath(genreBinding.GetProperty("Path").GetString()!, movie.Path)) error = "genre_source_binding_changed";
                else original = current.RenderJson;
                proof = new { media_path = movie.Path, original_binding = "confirmed-plugin-source" };
            }
            else
            {
                var read = NfoGenres.Read(movie.Path, movie.GetAdditionalParts().Select(x => x.Path), scope?.Nfo);
                error = read.Error == "genre_nfo_empty" ? null : read.Error;
                original = error is null ? JsonSerializer.Serialize(read.Values) : null;
                proof = new { media_path = movie.Path, nfo_path = read.Path, content_hash = read.Fingerprint };
            }
        }
        var names = People(movie, scope).Where(x => !string.IsNullOrWhiteSpace(x)
            && original?.Contains(x, StringComparison.Ordinal) == true).Distinct(StringComparer.Ordinal).ToArray();
        return (movie, new(key, movie.Path, field, error is null ? original : null, display, prefix, JsonSerializer.Serialize(proof),
            Writable: displayError is null, SourceError: error, DisplayError: displayError, PersonNames: names));
    }
    public async Task<HostWriteOutcome> WriteAsync(HostWriteCommand command, CancellationToken token)
    {
        await JellyfinMovieAccess.Writer.WaitAsync(token);
        try
        {
            var (movie, actual) = Read(command.Key, command.Field, fresh: true);
            if (!settings.Read().ManagedLibraries.Contains(command.Key.LibraryId, StringComparer.Ordinal)) return HostWriteOutcome.Blocked;
            if (movie is null || !actual.Present || !actual.Writable || actual.SourceError is not null) return HostWriteOutcome.Blocked;
            if (!SamePath(command.MediaPath, actual.MediaPath) || actual.Prefix != command.Prefix
                || !SameSource(command.Field, actual.Original!, command.Original)) return HostWriteOutcome.Conflict;
            if (SameValue(command.Field, actual.Display, command.TargetDisplay)) return HostWriteOutcome.AlreadyMatches;
            if (!SameValue(command.Field, actual.Display, command.ExpectedDisplay)) return HostWriteOutcome.Conflict;
            string[]? genres = command.Field == "Genres" ? JsonSerializer.Deserialize<string[]>(command.TargetDisplay) : null;
            if (command.Field == "Name") ArgumentException.ThrowIfNullOrWhiteSpace(command.TargetDisplay);
            token.ThrowIfCancellationRequested();
            if (!command.AuthorityStillValid()) return HostWriteOutcome.Conflict;
            // Shared lock covers this plugin only. Jellyfin refreshes and other plugins have no atomic CAS.
            if (command.Field == "Name") movie.Name = command.TargetDisplay;
            else if (command.Field == "Overview") movie.Overview = command.TargetDisplay;
            else movie.Genres = genres ?? throw new ArgumentException("InvalidGenres");
            await library.UpdateItemAsync(movie, movie.GetParent(), ItemUpdateType.MetadataEdit, token);
            return HostWriteOutcome.Written;
        }
        finally { JellyfinMovieAccess.Writer.Release(); }
    }
    private static bool SamePath(string a, string b) => Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b)
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool SameSource(string field, string a, string b) => field == "Genres"
        ? JsonSerializer.Deserialize<string[]>(a)!.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).SequenceEqual(JsonSerializer.Deserialize<string[]>(b)!.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)
        : SameValue(field, a, b);
    private static bool SameValue(string field, string a, string b) => field == "Genres"
        ? JsonSerializer.Deserialize<string[]>(a)!.ToHashSet(StringComparer.Ordinal).SetEquals(JsonSerializer.Deserialize<string[]>(b)!)
        : field == "Overview" ? a.Replace("\r\n", "\n").Replace('\r', '\n') == b.Replace("\r\n", "\n").Replace('\r', '\n') : a == b;
}
