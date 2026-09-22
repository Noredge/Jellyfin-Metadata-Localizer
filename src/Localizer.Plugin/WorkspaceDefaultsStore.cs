using System.Text.Json;
using Localizer.Core;

namespace Localizer.Plugin;

public sealed record WorkspaceDefaults(long Revision, string Languages, string Display);

public sealed class WorkspaceDefaultsStore(string root, Guid folder)
{
    private static readonly object Gate = new();
    private string FilePath => Path.Combine(root, "workspace-defaults", folder.ToString("N") + ".json");
    public WorkspaceDefaults Read()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return new(0, "both", "current");
            var value = JsonSerializer.Deserialize<WorkspaceDefaults>(File.ReadAllText(FilePath))
                ?? throw new IOException("InvalidWorkspaceDefaults");
            Validate(value); return value;
        }
    }
    public WorkspaceDefaults Save(WorkspaceDefaults value)
    {
        Validate(value);
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var lease = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = Read();
            if (current.Revision != value.Revision) throw new RevisionConflictException();
            var next = value with { Revision = checked(current.Revision + 1) };
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, next); stream.Flush(true); }
                File.Move(temporary, FilePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return next;
        }
    }
    private static void Validate(WorkspaceDefaults value)
    {
        if (value is null || value.Revision < 0 || value.Languages is not ("both" or "zh-Hans" or "en")
            || value.Display is not ("current" or "store" or "zh-Hans" or "en")
            || value.Display is not ("store" or "current") && value.Languages != "both" && value.Display != value.Languages)
            throw new ArgumentException("InvalidWorkspaceDefaults");
    }
}
