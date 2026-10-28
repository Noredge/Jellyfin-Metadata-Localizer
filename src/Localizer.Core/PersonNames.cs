using System.Text.Json;

namespace Localizer.Core;

public sealed record PersonNameEntry(string Id, string OriginalName, string[] Aliases, string? ChineseName = null, string? EnglishName = null)
{
    public string Display(TargetLanguage language) => (language == TargetLanguage.SimplifiedChinese ? ChineseName : EnglishName) is { Length: > 0 } value ? value : OriginalName;
}
public sealed record PersonNameSnapshot(long Revision, PersonNameEntry[] Entries)
{
    public static PersonNameSnapshot Empty => new(0, []);
    public PersonNameEntry? Resolve(string name) => Entries.SingleOrDefault(x => x.OriginalName == name || x.Aliases.Contains(name, StringComparer.Ordinal));
    public void Validate()
    {
        if (Revision < 0 || Entries is null || Entries.Length > 100000 || Entries.Any(x => x is null)
            || Entries.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Entries.Length) throw new ArgumentException("InvalidPersonNames");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (!Guid.TryParseExact(entry.Id, "N", out _) || entry.Aliases is null || entry.Aliases.Length > 50) throw new ArgumentException("InvalidPersonName");
            ValidateName(entry.OriginalName);
            foreach (var name in entry.Aliases.Prepend(entry.OriginalName))
            { ValidateName(name); if (!seen.Add(name)) throw new ArgumentException("PersonAliasConflict"); }
            if (entry.ChineseName is not null) ValidateName(entry.ChineseName);
            if (entry.EnglishName is not null) ValidateName(entry.EnglishName);
        }
    }
    public static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Trim() != value || value.Any(char.IsControl)
            || value.Contains("__JML_", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("InvalidPersonName");
    }
}
