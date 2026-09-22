using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Localizer.Core;
using Localizer.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Mvc;

namespace Localizer.Plugin;

public sealed record PersonNameSaveRequest(long Revision, string? Id, [Required] string OriginalName,
    [Required] string[] Aliases, string? ChineseName = null, string? EnglishName = null);

public sealed partial class AdminController
{
    private PersonNameStore PersonNames() => new(Path.Combine(Root, "person-names.json"));
    private PersonNameSnapshot CurrentPersonNames() => PersonNames().Read();
    private PersonNameSnapshot DiscoverPersonNames(Guid folder)
    {
        if (!FolderExists(folder)) throw new KeyNotFoundException();
        var movies = library.GetItemList(new InternalItemsQuery { ParentId = folder, Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie], Limit = WorkbenchMaximum + 1 });
        if (movies.Count > WorkbenchMaximum) throw new OperationValidationException("library_limit_exceeded");
        return PersonNames().Discover(movies.OfType<Movie>().Where(x => Belongs(x, folder))
            .SelectMany(movie => library.GetPeople(movie).Select(x => x.Name)));
    }
    private static GenerationSpec PrepareConfiguredTitle(SourceSnapshot source, TargetLanguage language,
        IEnumerable<string> names, TitleRules rules, TranslationServiceProfile service, PersonNameSnapshot people)
    {
        var spec = service.Apply(TitlePipeline.Prepare(source, language, names, rules, people));
        var context = JsonNode.Parse(spec.ContextJson)!.AsObject(); context["ServiceId"] = service.Id;
        return spec with { ContextJson = context.ToJsonString() };
    }

    [HttpGet("people")]
    public ActionResult ListPersonNames([FromQuery] string? q = null, [FromQuery] int offset = 0, [FromQuery] int limit = 30) => Guard(() =>
    {
        if (offset < 0 || limit is < 1 or > 100 || q?.Length > 200) throw new ArgumentException();
        var snapshot = CurrentPersonNames();
        var filtered = snapshot.Entries.Where(x => string.IsNullOrWhiteSpace(q)
            || x.Aliases.Append(x.OriginalName).Append(x.ChineseName ?? "").Append(x.EnglishName ?? "")
                .Any(n => n.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.OriginalName, StringComparer.Ordinal).ToArray();
        return new { snapshot.Revision, Total = filtered.Length, Items = filtered.Skip(offset).Take(limit).ToArray() };
    });
    [HttpPost("people")]
    public ActionResult SavePersonName([FromBody] PersonNameSaveRequest request) => CampaignGuard(() =>
    {
        var id = string.IsNullOrEmpty(request.Id) ? Guid.NewGuid().ToString("N") : request.Id;
        var current = CurrentPersonNames();
        if (!string.IsNullOrEmpty(request.Id) && !current.Entries.Any(x => x.Id == id)) throw new KeyNotFoundException();
        var item = new PersonNameEntry(id, request.OriginalName, request.Aliases,
            string.IsNullOrWhiteSpace(request.ChineseName) ? null : request.ChineseName.Trim(),
            string.IsNullOrWhiteSpace(request.EnglishName) ? null : request.EnglishName.Trim());
        PersonNameSnapshot next;
        try { next = PersonNames().Save(request.Revision, item); }
        catch (ArgumentException error) when (error.Message == "PersonAliasConflict") { throw new OperationValidationException("person_alias_conflict"); }
        catch (RevisionConflictException) { throw new OperationValidationException("people_revision_conflict"); }
        return new { next.Revision, Item = item };
    });
    [HttpPost("libraries/{folder:guid}/people/discover")]
    public ActionResult DiscoverPeople(Guid folder) => Guard(() =>
    {
        var previous = CurrentPersonNames(); var next = DiscoverPersonNames(folder);
        return new { next.Revision, Added = next.Entries.Length - previous.Entries.Length, Total = next.Entries.Length };
    });
}
