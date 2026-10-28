namespace Localizer.Unified;

/// <summary>Public v2 data never reuses an older installation's directory.</summary>
public static class DataLayout
{
    public const string DirectoryName = "metadata-localizer-v2";
    public static string Root(string serverDataPath)
    {
        if (!Path.IsPathFullyQualified(serverDataPath)) throw new ArgumentException("Absolute server data path required.");
        return Path.Combine(serverDataPath, DirectoryName);
    }
    public static bool HasOlderData(string serverDataPath) => Directory.Exists(Path.Combine(serverDataPath, "metadata-localizer"));
}
