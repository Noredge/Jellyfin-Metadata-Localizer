using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Localizer.Core;

public sealed record NfoGenreRead(string? Path, string? Fingerprint, string[] Values, string? Error, string? LegacyFingerprint = null);
public static class NfoGenres
{
    public static NfoGenreRead Read(string mediaPath, IEnumerable<string>? confirmedAdditionalParts = null, NfoReadSession? session = null)
    {
        using var measurement = OperationTimings.Measure("nfo_read");
        NfoGenreRead Error(string category) => new(null, null, [], category);
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(mediaPath) || !File.Exists(mediaPath)) return Error("genre_nfo_media_missing");
            var directory = System.IO.Path.GetDirectoryName(mediaPath)!;
            var exact = System.IO.Path.ChangeExtension(mediaPath, ".nfo");
            var folder = System.IO.Path.Combine(directory, "movie.nfo");
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var candidates = new[] { exact, folder }.Distinct(comparer).Where(File.Exists).ToArray();
            if (candidates.Length == 0) return Error("genre_nfo_missing");
            if (candidates.Length > 1) return Error("genre_nfo_ambiguous");
            var path = candidates[0];
            if (!comparer.Equals(path, exact))
            {
                // Only the host's established multipart relationship can expand ownership.
                // Never infer ownership from a similar basename or CD suffix here.
                if (!FolderNfoOwnership.IsUnambiguous(mediaPath, confirmedAdditionalParts, session)) return Error("genre_nfo_ambiguous");
            }
            session ??= new NfoReadSession();
            var document = session.Document(path);
            if (document is null) return Error("genre_nfo_too_large");
            if (document.Root?.Name != "movie") return Error("genre_nfo_invalid");
            var nodes = document.Root.Elements("genre").ToArray();
            if (nodes.Length == 0) return Error("genre_nfo_empty");
            if (nodes.Length > 200 || nodes.Any(x => x.HasElements || string.IsNullOrWhiteSpace(x.Value)
                || x.Value.Length > 500 || x.Value.IndexOfAny(['\r', '\n']) >= 0)) return Error("genre_nfo_invalid");
            var values = nodes.Select(x => x.Value).ToArray();
            return new(path, Identity.Hash(GenreValues.Canonical(values)), values, null, session.Digest(path));
        }
        catch (XmlException) { return Error("genre_nfo_invalid"); }
        catch (IOException) { return Error("genre_nfo_unreadable"); }
        catch (UnauthorizedAccessException) { return Error("genre_nfo_unreadable"); }
        catch (ArgumentException) { return Error("genre_nfo_invalid_path"); }
    }

    public static bool DisplayCanBeUpdated(string[] current, string[] nfo, string[]? original, string[]? lastWritten) =>
        GenreValues.Equal(current, nfo) || original is not null && GenreValues.Equal(current, original)
        || lastWritten is not null && GenreValues.Equal(current, lastWritten);
}
