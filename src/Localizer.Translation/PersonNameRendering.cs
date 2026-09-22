using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Localizer.Core;

namespace Localizer.Translation;

public static class PersonNameRendering
{
    // Old protected candidates can be recovered only if every frozen displayed name is still present.
    // This does not guess which free translation or spelling refers to an actor.
    public static string RecoverTemplate(string rendered, ProtectedTitle context)
    {
        var values = context.DisplayNames ?? context.Mapping;
        if (values.Count == 0) throw new ArgumentException("NoProtectedNames");
        var groups = values.GroupBy(x => x.Value, StringComparer.Ordinal).ToArray();
        foreach (var group in groups)
        {
            if (group.Select(x => context.PersonIds?.GetValueOrDefault(x.Key) is { Length: > 0 } id ? id : context.Mapping[x.Key])
                .Distinct(StringComparer.Ordinal).Count() != 1) throw new ArgumentException("AmbiguousDisplayedName");
        }
        var queues = groups.ToDictionary(x => x.Key, x => new Queue<string>(x.Select(y => y.Key)), StringComparer.Ordinal);
        var protectedText = TitlePipeline.Protect(rendered, values.Values.Distinct(StringComparer.Ordinal));
        var result = Regex.Replace(protectedText.MaskedSource, "__JML_PERSON_[A-Z]+__", match =>
        {
            var queue = queues[protectedText.Mapping[match.Value]];
            if (queue.Count == 0) throw new ArgumentException("NameCountMismatch");
            return queue.Dequeue();
        }, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (queues.Values.Any(x => x.Count != 0) || TitlePipeline.RenderNames(result, context, TargetLanguage.SimplifiedChinese) != rendered)
            throw new ArgumentException("NameCountMismatch");
        return result;
    }

    public static (string Text, GenerationSpec Spec) Refresh(SourceSnapshot source, Candidate candidate,
        PersonNameSnapshot people, string? savedTemplate, Guid planId)
    {
        people.Validate();
        if (candidate.SourceId != source.Id || candidate.IsHumanEdited || !candidate.Approved) throw new ArgumentException("CandidateNotEligible");
        var spec = JsonSerializer.Deserialize<GenerationSpec>(candidate.ProvenanceJson) ?? throw new ArgumentException("CandidateProvenanceMissing");
        var original = TitlePipeline.ValidateContext(new(source, candidate.Language, spec));
        var node = JsonNode.Parse(spec.ContextJson)!.AsObject();
        var template = node["NameTemplate"]?.GetValue<string>() ?? savedTemplate ?? RecoverTemplate(candidate.GeneratedText, original);
        if (TitlePipeline.RenderNames(template, original, candidate.Language) != candidate.GeneratedText) throw new ArgumentException("NameTemplateMismatch");
        var identities = original.Mapping.ToDictionary(x => x.Key, x => original.PersonIds?.GetValueOrDefault(x.Key) is { Length: > 0 } id
            ? id : people.Resolve(x.Value)?.Id ?? "");
        var display = original.Mapping.ToDictionary(x => x.Key, x => identities[x.Key].Length == 0 ? x.Value
            : people.Entries.SingleOrDefault(p => p.Id == identities[x.Key])?.Display(candidate.Language) ?? throw new ArgumentException("PersonIdentityMissing"));
        var next = original with { DisplayNames = display, PersonIds = identities, NameRevision = people.Revision };
        var text = TitlePipeline.RenderNames(template, next, candidate.Language);
        node["DisplayNames"] = JsonSerializer.SerializeToNode(display);
        node["PersonIds"] = JsonSerializer.SerializeToNode(identities);
        node["NameRevision"] = people.Revision;
        node["NameTemplate"] = template;
        node["NameRefreshOf"] = candidate.Id;
        node["NameRefreshPlanId"] = planId.ToString("N");
        node["Operation"] = "person-name-refresh-no-model-call";
        return (text, spec with { ContextJson = node.ToJsonString() });
    }
}
