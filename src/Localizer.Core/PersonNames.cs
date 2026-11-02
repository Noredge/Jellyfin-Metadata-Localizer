using System.Text.Json;

namespace Localizer.Core;

public sealed class PersonAliasConflictException() : ArgumentException("PersonAliasConflict");

/// <summary>Literal name matching with Latin word boundaries; CJK names may adjoin CJK text.</summary>
public static class PersonNameMatching
{
    private const string Word = @"A-Za-z\p{Nd}\p{M}_\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u024F\u1E00-\u1EFF\u2C60-\u2C7F\uA720-\uA7FF\uAB30-\uAB6F";
    private static bool WordCharacter(char c) => char.IsDigit(c) || c == '_' || char.IsAsciiLetter(c)
        || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark
        || c is >= '\u00C0' and <= '\u00D6' or >= '\u00D8' and <= '\u00F6' or >= '\u00F8' and <= '\u024F'
            or >= '\u1E00' and <= '\u1EFF' or >= '\u2C60' and <= '\u2C7F' or >= '\uA720' and <= '\uA7FF' or >= '\uAB30' and <= '\uAB6F';
    public static string Pattern(string name) => (WordCharacter(name[0]) ? "(?<![" + Word + "])" : "")
        + System.Text.RegularExpressions.Regex.Escape(name) + (WordCharacter(name[^1]) ? "(?![" + Word + "])" : "");
    public static bool Contains(string text, string name)
    {
        if (name.Length == 0) return false;
        var start = 0;
        while (start <= text.Length - name.Length)
        {
            var at = text.IndexOf(name, start, StringComparison.Ordinal); if (at < 0) return false;
            if ((!WordCharacter(name[0]) || at == 0 || !WordCharacter(text[at - 1]))
                && (!WordCharacter(name[^1]) || at + name.Length == text.Length || !WordCharacter(text[at + name.Length]))) return true;
            start = at + 1;
        }
        return false;
    }
}

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
