namespace Localizer.Core;

public static class MetadataText
{
    public static void ValidateOverview(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 24000 || text.Contains('\0')) throw new ArgumentException("InvalidOverviewText");
    }
}
