using System.Xml;
using System.Xml.Linq;

namespace Localizer.Core;

public sealed record NfoOverviewRead(string? Path, string? Text, string? Hash, string? Error);
public static class NfoOverview
{
    // Exact sidecars take priority. Folder fallback accepts only host-confirmed parts.
    public static NfoOverviewRead Read(string mediaPath, IEnumerable<string>? confirmedAdditionalParts = null, NfoReadSession? session = null)
    {
        using var measurement = OperationTimings.Measure("nfo_read");
        NfoOverviewRead Error(string error) => new(null, null, null, error);
        try
        {
            if (!Path.IsPathFullyQualified(mediaPath) || !File.Exists(mediaPath)) return Error("overview_media_missing");
            var path = Path.ChangeExtension(mediaPath, ".nfo");
            var folderNfo = Path.Combine(Path.GetDirectoryName(mediaPath)!, "movie.nfo");
            if (!File.Exists(path))
            {
                if (!File.Exists(folderNfo)) return Error("overview_nfo_missing");
                if (!FolderNfoOwnership.IsUnambiguous(mediaPath, confirmedAdditionalParts, session)) return Error("overview_nfo_ambiguous");
                path = folderNfo;
            }
            var doc = (session ?? new NfoReadSession()).Document(path);
            if (doc is null) return Error("overview_nfo_too_large");
            if (doc.Root?.Name != "movie") return Error("overview_nfo_invalid");
            var plots = doc.Root.Elements("plot").ToArray();
            if (plots.Length == 0 || plots.Length == 1 && string.IsNullOrWhiteSpace(plots[0].Value)) return Error("overview_empty");
            if (plots.Length != 1 || plots[0].HasElements) return Error("overview_nfo_invalid");
            var text = plots[0].Value;
            if (text.Length > 24000) return Error("overview_too_long");
            return new(path, text, Identity.Hash(text), null);
        }
        catch (XmlException) { return Error("overview_nfo_invalid"); }
        catch (IOException) { return Error("overview_nfo_unreadable"); }
        catch (UnauthorizedAccessException) { return Error("overview_nfo_unreadable"); }
        catch (ArgumentException) { return Error("overview_nfo_invalid_path"); }
    }
}
