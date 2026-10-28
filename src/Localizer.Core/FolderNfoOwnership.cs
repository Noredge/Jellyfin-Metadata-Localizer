namespace Localizer.Core;

internal static class FolderNfoOwnership
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".wmv", ".mov", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".flv", ".iso", ".strm", ".vob", ".mts", ".rm", ".rmvb", ".3gp", ".asf", ".mxf", ".ogv", ".divx" };

    internal static bool IsUnambiguous(string mediaPath, IEnumerable<string>? confirmedAdditionalParts, NfoReadSession? session = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var owned = new[] { mediaPath }.Concat(confirmedAdditionalParts ?? []).ToHashSet(comparer);
        var videos = (session?.Files(Path.GetDirectoryName(mediaPath)!) ?? Directory.EnumerateFiles(Path.GetDirectoryName(mediaPath)!).ToArray())
            .Where(x => VideoExtensions.Contains(Path.GetExtension(x))).ToArray();
        return videos.Length > 0 && videos.All(owned.Contains);
    }
}
