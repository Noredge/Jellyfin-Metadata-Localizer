using Localizer.Core;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record NfoGenreImportRequest(bool ConfirmRead);
internal sealed record NfoGenrePlan(string State, string? Error, NfoGenreRead? Read = null);

public sealed partial class AdminController
{
    private NfoGenrePlan InspectNfoGenre(Guid folder, Movie movie, CandidateStore store)
    {
        var key = Key(folder, movie.Id); var source = store.GetCurrentGenreSource(key); var binding = ReadGenreBinding(key);
        if (source is not null && !GenreConfirmed(movie, source, binding)) return new("Blocked", "genre_nfo_binding_changed");
        // Manual source confirmations are deliberate overrides, not automatically replaced by NFO.
        if (source is not null && binding!.NfoPath is null) return new("Manual", null);
        if (store.ListUnresolvedGenreOperations().Any(x => x.Key == key)) return new("Blocked", "genre_nfo_pending");
        var nfo = ReadNfoGenres(movie);
        if (nfo.Error is not null) return new("Blocked", nfo.Error);
        var point = store.GetGenreRestorePoint(key);
        var lastWritten = point is null ? null : point.Consumed ? point.Application.BeforeValues : point.Application.PlannedValues;
        if (!NfoGenres.DisplayCanBeUpdated(movie.Genres, nfo.Values, source?.OriginalValues, lastWritten))
            return new("Blocked", "genre_nfo_display_conflict");
        if (source is null) return new("Ready", null, nfo);
        if (!GenreValues.Equal(source.OriginalValues, nfo.Values) || binding!.NfoFingerprint != nfo.Fingerprint || binding.NfoPath != nfo.Path)
            return new("Changed", null, nfo);
        return new("Current", null, nfo);
    }
    private NfoGenrePlan ImportNfoGenre(Guid folder, Movie movie, CandidateStore store)
    {
        var key = Key(folder, movie.Id);
        var observation = GenreObservation(movie, store.GetCurrentGenreSource(key));
        var plan = InspectNfoGenre(folder, movie, store);
        if (plan.State is not ("Ready" or "Changed")) return plan;
        var nfo = plan.Read!;
        // Re-read the media observation and bounded file just before committing plugin state.
        var current = Resolve(folder, movie.Id);
        var checkedPlan = InspectNfoGenre(folder, current, store);
        if (checkedPlan.State != plan.State || checkedPlan.Read?.Fingerprint != nfo.Fingerprint
            || checkedPlan.Read?.Path != nfo.Path
            || observation != GenreObservation(current, store.GetCurrentGenreSource(key)))
            return new("Blocked", "genre_nfo_changed_retry");
        try
        {
            var source = store.ObserveGenreSource(key, nfo.Values);
            WriteGenreBinding(key, new(source.Id, current.Path, nfo.Path, nfo.Fingerprint));
            return new("Imported", null, nfo);
        }
        catch (OperationBusyException) { return new("Blocked", "genre_nfo_pending"); }
    }
    private object NfoGenreSummary(Guid folder, bool save, int start, int limit)
    {
        var store = Store();
        var rows = LibraryMovies(folder).Select(movie =>
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();
            var plan = save ? ImportNfoGenre(folder, movie, store) : InspectNfoGenre(folder, movie, store);
            return new { ItemId = movie.Id.ToString("N"), movie.Name, plan.State, plan.Error };
        }).ToArray();
        var exceptions = rows.Where(x => x.State == "Blocked").ToArray();
        return new { Total = rows.Length, Ready = rows.Count(x => x.State == "Ready"), Changed = rows.Count(x => x.State == "Changed"),
            Current = rows.Count(x => x.State == "Current"), Manual = rows.Count(x => x.State == "Manual"),
            Imported = rows.Count(x => x.State == "Imported"), Blocked = exceptions.Length,
            Start = start, NextStart = start + limit < exceptions.Length ? (int?)(start + limit) : null,
            Items = exceptions.Skip(start).Take(limit).ToArray() };
    }
    [HttpGet("libraries/{folder:guid}/genres/nfo")]
    public ActionResult NfoGenrePreflight(Guid folder, [FromQuery] int start = 0, [FromQuery] int limit = 50) => Guard(() =>
    {
        if (start is < 0 or > 1000000 || limit is < 1 or > 50) throw new ArgumentException();
        return NfoGenreSummary(folder, false, start, limit);
    });
    [HttpPost("libraries/{folder:guid}/genres/nfo")]
    public ActionResult ImportNfoGenres(Guid folder, [FromBody] NfoGenreImportRequest request) => CampaignGuard(() =>
    {
        if (!request.ConfirmRead) throw new ArgumentException();
        RequireIdle(Campaigns());
        return NfoGenreSummary(folder, true, 0, 50);
    });
}
