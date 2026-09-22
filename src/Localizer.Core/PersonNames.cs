using System.Text.Json;

namespace Localizer.Core;

public sealed record PersonNameEntry(string Id, string OriginalName, string[] Aliases, string? ChineseName = null, string? EnglishName = null)
{
    public string Display(TargetLanguage language) => (language == TargetLanguage.SimplifiedChinese ? ChineseName : EnglishName) is { Length: > 0 } value ? value : OriginalName;
}
public sealed record PersonNameSnapshot(long Revision, PersonNameEntry[] Entries)
{
    public static PersonNameSnapshot Empty => new(0, []);
    public PersonNameEntry? Resolve(string name) => Entries.SingleOrDefault(x => x.OriginalName == name || x.Aliases.Contains(name, StringComparer.Ordinal));
    public void Validate()
    {
        if (Revision < 0 || Entries is null || Entries.Length > 100000 || Entries.Any(x => x is null)
            || Entries.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Entries.Length) throw new ArgumentException("InvalidPersonNames");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (!Guid.TryParseExact(entry.Id, "N", out _) || entry.Aliases is null || entry.Aliases.Length > 50) throw new ArgumentException("InvalidPersonName");
            ValidateName(entry.OriginalName);
            foreach (var name in entry.Aliases.Prepend(entry.OriginalName))
            { ValidateName(name); if (!seen.Add(name)) throw new ArgumentException("PersonAliasConflict"); }
            if (entry.ChineseName is not null) ValidateName(entry.ChineseName);
            if (entry.EnglishName is not null) ValidateName(entry.EnglishName);
        }
    }
    public static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Trim() != value || value.Any(char.IsControl)
            || value.Contains("__JML_", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("InvalidPersonName");
    }
}

/// <summary>Exact original names and explicitly assigned aliases only. No fuzzy identity merging.</summary>
public sealed class PersonNameStore(string file)
{
    private readonly string path = Path.GetFullPath(file);
    public PersonNameSnapshot Read()
    {
        if (!File.Exists(path)) return PersonNameSnapshot.Empty;
        var snapshot = JsonSerializer.Deserialize<PersonNameSnapshot>(File.ReadAllText(path)) ?? throw new InvalidOperationException("InvalidPersonNameStore");
        snapshot.Validate(); return snapshot;
    }
    public PersonNameSnapshot Save(long expectedRevision, PersonNameEntry entry)
    {
        using var lease = Lease(); var current = Read();
        if (current.Revision != expectedRevision) throw new RevisionConflictException();
        var existing = current.Entries.SingleOrDefault(x => x.Id == entry.Id);
        if (existing is not null && existing.OriginalName != entry.OriginalName) throw new ArgumentException("OriginalNameIsStable");
        var next = new PersonNameSnapshot(checked(current.Revision + 1), [.. current.Entries.Where(x => x.Id != entry.Id), entry]);
        next.Validate(); Write(next); return next;
    }
    public PersonNameSnapshot Discover(IEnumerable<string> names)
    {
        var values = names.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var name in values) PersonNameSnapshot.ValidateName(name);
        using var lease = Lease(); var current = Read();
        var known = current.Entries.SelectMany(x => x.Aliases.Prepend(x.OriginalName)).ToHashSet(StringComparer.Ordinal);
        var added = values.Where(x => !known.Contains(x)).Order(StringComparer.Ordinal).Select(x => new PersonNameEntry(Guid.NewGuid().ToString("N"), x, [])).ToArray();
        if (added.Length == 0) return current;
        var next = new PersonNameSnapshot(checked(current.Revision + 1), [.. current.Entries, .. added]);
        next.Validate(); Write(next); return next;
    }
    private FileStream Lease()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new OperationBusyException(); }
    }
    private void Write(PersonNameSnapshot value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
