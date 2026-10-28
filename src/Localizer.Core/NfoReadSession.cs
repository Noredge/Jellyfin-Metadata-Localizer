using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Localizer.Core;

/// <summary>One media read operation only. Never retained across sends or host writes.</summary>
public sealed class NfoReadSession
{
    private readonly Dictionary<string, byte[]?> bytes = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, XDocument> documents = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> directories = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public string[] Files(string directory)
    {
        if (!directories.TryGetValue(directory, out var files))
        { using var timing = OperationTimings.Measure("directory_read"); directories[directory] = files = Directory.EnumerateFiles(directory).ToArray(); }
        return files;
    }
    public byte[]? Bytes(string path)
    {
        if (bytes.TryGetValue(path, out var value)) return value;
        using var timing = OperationTimings.Measure("nfo_file_read");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        const int maximum = 4 * 1024 * 1024;
        if (file.Length > maximum) return bytes[path] = null;
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = file.Read(buffer)) > 0)
        { if (memory.Length + count > maximum) return bytes[path] = null; memory.Write(buffer, 0, count); }
        return bytes[path] = memory.ToArray();
    }
    public XDocument? Document(string path)
    {
        if (documents.TryGetValue(path, out var doc)) return doc;
        var value = Bytes(path); if (value is null) return null;
        using var timing = OperationTimings.Measure("nfo_parse");
        using var memory = new MemoryStream(value, writable: false);
        using var reader = XmlReader.Create(memory, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        return documents[path] = XDocument.Load(reader);
    }
    public string? Digest(string path) => Bytes(path) is { } value ? Convert.ToHexString(SHA256.HashData(value)) : null;
}
