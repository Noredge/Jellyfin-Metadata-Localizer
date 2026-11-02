using System.Text.RegularExpressions;

namespace Localizer.Core;

public sealed class PersonTemplateException(bool required = false) : ArgumentException(required ? "PersonTemplateRequired" : "PersonTemplateInvalid")
{ public string Code => required ? "person_template_required" : "person_template_invalid"; }

public sealed record PersonTextSlot(string Token, string Original, string Display, string? PersonId = null);
public sealed record PersonTextTemplate(int Format, string Text, PersonTextSlot[] Slots)
{
    public string Render(Func<PersonTextSlot, string> resolve)
    {
        if (Format != 1 || Text is null || Text.Length > 262144 || Text.Contains('\0') || Slots is null || Slots.Length > 8192
            || Slots.Any(x => x is null || string.IsNullOrWhiteSpace(x.Original) || x.Original.Length > 24000
                || string.IsNullOrWhiteSpace(x.Display) || x.Display.Length > 24000 || x.Original.Contains('\0') || x.Display.Contains('\0')
                || x.PersonId is not null && !Guid.TryParseExact(x.PersonId, "N", out _))) throw new PersonTemplateException();
        var values = new Dictionary<string, PersonTextSlot>(StringComparer.Ordinal);
        foreach (var slot in Slots)
            if (slot.Token is null || !Regex.IsMatch(slot.Token, "^__JML_PERSON_[A-Z]+__$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))
                || !values.TryAdd(slot.Token, slot)) throw new PersonTemplateException();
        var matches = Regex.Matches(Text, "__JML_PERSON_[A-Z]+__").Select(x => x.Value).ToArray();
        if (matches.Length != Slots.Length || matches.Distinct(StringComparer.Ordinal).Count() != matches.Length
            || matches.Any(x => !values.ContainsKey(x))) throw new PersonTemplateException();
        var result = Regex.Replace(Text, "__JML_PERSON_[A-Z]+__", x => resolve(values[x.Value]), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (result.Contains("__JML_", StringComparison.OrdinalIgnoreCase) || result.Contains('\0') || result.Length > 24000) throw new PersonTemplateException();
        return result;
    }
    public string Frozen() => Render(x => x.Display);
}
