using ByteAtlas.Scanning;

namespace ByteAtlas.Model;

/// <summary>
/// Several scans under one virtual root ("All scans") with summed totals. The scan roots are listed as its
/// folders but keep no parent, so their paths stay as they are.
/// </summary>
public static class ScanGroup
{
    public const string Name = "All scans";

    public static ScanSession Build(IReadOnlyList<ScanSession> scans)
    {
        var g = ScanSession.FromTree(Name, new DirNode(Name, null) { Pending = false });
        g.IsGroup = true;
        Refresh(g, scans);
        return g;
    }

    /// <summary>Takes the current scans and totals into the group (keeps the group object, so selection stays).</summary>
    public static void Refresh(ScanSession g, IReadOnlyList<ScanSession> scans)
    {
        var root = g.Root;
        long size = 0, alloc = 0, files = 0, folders = 0, total = 0, free = 0, mod = 0;
        var depth = 0;
        var volumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        root.DirList.Clear();
        foreach (var s in scans)
        {
            var r = s.Root;
            root.DirList.Add(r);
            size += r.Size;
            alloc += r.Allocated;
            files += r.FileCount;
            folders += r.FolderCount + 1;
            mod = Math.Max(mod, r.ModifiedTicks);
            depth = Math.Max(depth, r.MaxDepthBelow + 1);
            // each drive's space counted once
            var vol = Path.GetPathRoot(r.FullPath) ?? r.FullPath;
            if (s.VolumeTotal > 0 && volumes.Add(vol))
            {
                total += s.VolumeTotal;
                free += s.VolumeFree;
            }
        }
        root._size = size;
        root._allocated = alloc;
        root._fileCount = files;
        root._folderCount = folders;
        root.ModifiedTicks = mod;
        root.MaxDepthBelow = depth;
        root.Sort(bySize: true, recursive: false);
        g.VolumeTotal = total;
        g.VolumeFree = free;
        g.MethodUsed = $"{scans.Count} scans";
        g.ScanDate = scans.Count == 0 ? DateTime.Now : scans.Min(s => s.ScanDate);
        g.Members = [.. scans];
        g.State = scans.Any(s => s.State is ScanState.Scanning or ScanState.Paused) ? ScanState.Scanning : ScanState.Done;
    }
}
