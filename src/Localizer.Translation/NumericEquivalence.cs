using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Localizer.Translation;

/// <summary>Conservative writing equivalence; does not infer omitted quantities or arithmetic.</summary>
public static class NumericEquivalence
{
    private static string Signature(string text) => string.Join("|", Regex.Matches(text, @"\d+(?:\.\d+)?%?")
        .Select(x => x.Value).Order(StringComparer.Ordinal));
    public static bool SameNumbers(string source, string translated)
    {
        source = source.Normalize(NormalizationForm.FormKC); translated = translated.Normalize(NormalizationForm.FormKC);
        // Preserve existing acceptance when literal numbers match; do not introduce
        // warnings from incidental kanji in names, idioms or ordinary words.
        if (Signature(source) == Signature(translated)) return true;
        return Signature(Expand(source)) == Signature(Expand(translated));
    }
    private static string Expand(string text)
    {
        text = Regex.Replace(text, "シックス[・\\s]?ナイン", "69");
        text = Regex.Replace(text, @"\bsixty[ -]nine\b", "69", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(text, "[零〇一二三四五六七八九十百千万两]+(?=\\s*(?:点|人|名|日|天|夜|次|回|枚|本|部|个|個|岁|歳|年|月|分|秒|时|時|時間|小时|対|对|對|折|%|$))",
            match => ConvertNumeral(match.Value));
    }
    private static string ConvertNumeral(string value)
    {
        const string digits = "零一二三四五六七八九";
        static int Digit(char c) => c is '〇' ? 0 : c is '两' ? 2 : digits.IndexOf(c);
        if (!value.Any(c => c is '十' or '百' or '千' or '万'))
            return string.Concat(value.Select(c => Digit(c).ToString(CultureInfo.InvariantCulture)));
        try
        {
            long total = 0, section = 0, number = 0;
            foreach (var c in value)
            {
                var digit = Digit(c); if (digit >= 0) { number = digit; continue; }
                var unit = c switch { '十' => 10L, '百' => 100L, '千' => 1000L, '万' => 10000L, _ => 0L };
                if (unit == 10000) { total = checked(total + checked((section + number) * unit)); section = number = 0; }
                else { section = checked(section + checked((number == 0 ? 1 : number) * unit)); number = 0; }
            }
            return checked(total + section + number).ToString(CultureInfo.InvariantCulture);
        }
        catch (OverflowException) { return value; }
    }
}
