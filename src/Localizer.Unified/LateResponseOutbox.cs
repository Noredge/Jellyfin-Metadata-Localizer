using System.Text.Json;

namespace Localizer.Unified;

/// <summary>Validated late results survive a database outage without another provider send.</summary>
internal sealed class LateResponseOutbox(string dataRoot)
{
    internal sealed record Entry(int Format, string RequestId, GenerationReply Reply);
    private readonly string root = Path.Combine(dataRoot, "late-responses");
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static void Regular(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("LateResponseReparsePoint");
    }
    private string FilePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("InvalidLateRequestId");
        return Path.Combine(root, id + ".json");
    }
    internal bool HasPending => Directory.Exists(root) && PendingPath() is not null;
    private string? PendingPath()
    {
        Regular(root);
        return Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).FirstOrDefault();
    }
    internal void Store(string id, GenerationReply reply)
    {
        var path = FilePath(id);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Entry(1, id, reply));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("LateResponseTooLarge");
        Directory.CreateDirectory(root); Regular(root);
        if (File.Exists(path))
        {
            Regular(path);
            if (new FileInfo(path).Length > MaximumBytes || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("LateResponseConflict");
            return;
        }
        var temporary = Path.Combine(root, id + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal Entry? ReadNext()
    {
        if (!Directory.Exists(root)) return null;
        var path = PendingPath(); if (path is null) return null;
        Regular(path);
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("LateResponseTooLarge");
        var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("InvalidLateResponse");
        if (entry.Format != 1 || entry.Reply is null || FilePath(entry.RequestId) != path) throw new InvalidDataException("InvalidLateResponse");
        return entry;
    }
    internal void Remove(string id) { Regular(root); var path = FilePath(id); if (File.Exists(path)) { Regular(path); File.Delete(path); } }
}
