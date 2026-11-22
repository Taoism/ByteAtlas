using ByteAtlas.Model;

namespace ByteAtlas.Analysis;

/// <summary>Space that files of a folder share on disk (copy-on-write clones, hard links).</summary>
public sealed record SharedResult(long Shared, long Files, int Sets, bool Supported);

/// <summary>
/// Measures how much of a folder's allocated space is shared between its files: clones (ReFS / Dev Drive,
/// btrfs, XFS) and hard links point at the same blocks, but every copy counts its full size in the tree. Opens
/// every file of 64 KB and more once, so it runs on request, not with every scan.
/// </summary>
public static class SharedSpace
{
    public const long MinSize = 64 * 1024;

    public static SharedResult Measure(DirNode folder, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        var files = folder.AllFiles().Where(f => f.Size >= MinSize).ToList();
        if (files.Count == 0)
            return new SharedResult(0, 0, 0, true);
        // drives that cannot clone: only hard links share, and the scan already knows them by link count
        if (OperatingSystem.IsWindows() && !Native.FileClone.CanClone(files[0].FullPath))
            return new SharedResult(0, files.Count, 0, false);

        // key: where the data starts (clones and hard links alike), else the file id (hard links where blocks
        // are not shown as shared, e.g. Linux without clones)
        var byBlock = new Dictionary<(bool, ulong, ulong), List<FileItem>>();
        long done = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if ((++done & 255) == 0)
                progress?.Report((done, files.Count));
            if (Native.FileClone.FirstBlock(f.FullPath) is { } block)
                Add(byBlock, (true, block.Item1, block.Item2), f);
            else if (FileIdentity.Of(f.FullPath) is { } id)
                Add(byBlock, (false, id.Item1, id.Item2), f);
        }
        long shared = 0;
        var sets = 0;
        foreach (var list in byBlock.Values.Where(l => l.Count > 1))
        {
            // all but the largest copy use no blocks of their own (a clone changed later is partly its own again;
            // that part is counted as shared too, so this is the most that is shared)
            shared += list.Sum(f => f.Allocated) - list.Max(f => f.Allocated);
            sets++;
        }
        progress?.Report((files.Count, files.Count));
        return new SharedResult(shared, files.Count, sets, true);
    }

    private static void Add(Dictionary<(bool, ulong, ulong), List<FileItem>> map, (bool, ulong, ulong) key, FileItem f)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(f);
    }
}
