using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas.Analysis;

public sealed record FolderChange(string Path, long OldSize, long NewSize)
{
    public long Change => NewSize - OldSize;
}

public sealed class ComparisonResult
{
    public string ComparedWith { get; init; } = "";
    public DateTime ComparedDate { get; init; }
    public long OldSize { get; init; }
    public long NewSize { get; init; }
    public long Change => NewSize - OldSize;
    public List<FolderChange> Added { get; } = [];
    public List<FolderChange> Removed { get; } = [];
    public List<FolderChange> Changed { get; } = [];
}

/// <summary>Compares the current scan with an older one (index file, snapshot or another path).</summary>
public static class Comparison
{
    public static ComparisonResult Compare(ScanSession current, ScanSession old, string description)
    {
        var result = new ComparisonResult
        {
            ComparedWith = description,
            ComparedDate = old.ScanDate,
            OldSize = old.Root.Size,
            NewSize = current.Root.Size,
        };
        Walk(current.Root, old.Root, current.Root.FullPath, result);
        current.ComparedWith = description;
        result.Changed.Sort((a, b) => Math.Abs(b.Change).CompareTo(Math.Abs(a.Change)));
        result.Added.Sort((a, b) => b.NewSize.CompareTo(a.NewSize));
        result.Removed.Sort((a, b) => b.OldSize.CompareTo(a.OldSize));
        return result;
    }

    private static void Walk(DirNode now, DirNode? then, string path, ComparisonResult result)
    {
        if (then == null)
        {
            now.CompareSize = -1;
            now.CompareFiles = 0;
            result.Added.Add(new FolderChange(path, 0, now.Size));
            foreach (var c in now.Dirs)
                MarkNew(c);
            return;
        }
        now.CompareSize = then.Size;
        now.CompareFiles = then.FileCount;
        var oldByName = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in then.Dirs)
            oldByName.TryAdd(d.Name, d);
        foreach (var c in now.Dirs)
        {
            var cp = Path.Combine(path, c.Name);
            oldByName.Remove(c.Name, out var oc);
            Walk(c, oc, cp, result);
            if (oc != null && c.Size != oc.Size && c.Dirs.Length == 0)
                result.Changed.Add(new FolderChange(cp, oc.Size, c.Size));
        }
        foreach (var gone in oldByName.Values)
            result.Removed.Add(new FolderChange(Path.Combine(path, gone.Name), gone.Size, 0));
        if (now.Dirs.Length > 0 && now.Size != then.Size && now.OwnFilesSize != then.OwnFilesSize)
            result.Changed.Add(new FolderChange(path + Path.DirectorySeparatorChar + "[Files]", then.OwnFilesSize, now.OwnFilesSize));
    }

    private static void MarkNew(DirNode d)
    {
        d.CompareSize = -1;
        d.CompareFiles = 0;
        foreach (var c in d.Dirs)
            MarkNew(c);
    }

    public static void Clear(ScanSession s)
    {
        s.ComparedWith = null;
        foreach (var d in s.Root.AllFolders())
        {
            d.CompareSize = null;
            d.CompareFiles = null;
        }
    }
}
