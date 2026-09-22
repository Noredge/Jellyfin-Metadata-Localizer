using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Localizer.Core;

public sealed record NfoGenreRead(string? Path, string? Fingerprint, string[] Values, string? Error);
public static class NfoGenres
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".wmv", ".mov", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".flv", ".iso", ".strm", ".vob", ".mts", ".rm", ".rmvb" };
    public static NfoGenreRead Read(string mediaPath, IEnumerable<string>? confirmedAdditionalParts = null)
    {
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
                var owned = new[] { mediaPath }.Concat(confirmedAdditionalParts ?? []).ToHashSet(comparer);
                var videos = Directory.EnumerateFiles(directory).Where(x => VideoExtensions.Contains(System.IO.Path.GetExtension(x))).ToArray();
                if (videos.Length == 0 || videos.Any(x => !owned.Contains(x))) return Error("genre_nfo_ambiguous");
            }
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumBytes) return Error("genre_nfo_too_large");
            using var memory = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = file.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (memory.Length + count > MaximumBytes) return Error("genre_nfo_too_large");
                memory.Write(buffer, 0, count);
            }
            var bytes = memory.ToArray(); memory.Position = 0;
            using var reader = XmlReader.Create(memory, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != "movie") return Error("genre_nfo_invalid");
            var nodes = document.Root.Elements("genre").ToArray();
            if (nodes.Length == 0) return Error("genre_nfo_empty");
            if (nodes.Length > 200 || nodes.Any(x => x.HasElements || string.IsNullOrWhiteSpace(x.Value)
                || x.Value.Length > 500 || x.Value.IndexOfAny(['\r', '\n']) >= 0)) return Error("genre_nfo_invalid");
            return new(path, Convert.ToHexString(SHA256.HashData(bytes)), nodes.Select(x => x.Value).ToArray(), null);
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
