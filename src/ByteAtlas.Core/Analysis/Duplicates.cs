using System.Collections.Concurrent;
using ByteAtlas.Model;
using ByteAtlas.Search;

namespace ByteAtlas.Analysis;

/// <summary>How files are compared by the duplicate tabs.</summary>
public enum DuplicateMatch
{
    /// <summary>Same size and the same contents (checksum).</summary>
    Contents,

    /// <summary>Same name, size and contents.</summary>
    NameAndContents,

    /// <summary>Same name and size, contents not read (fast, no disk access).</summary>
    NameAndSize,
}

public sealed class DuplicateOptions
{
    public DuplicateMatch Match { get; set; } = DuplicateMatch.Contents;
    public HashKind Hash { get; set; } = HashKind.Md5;

    /// <summary>Files (or folders) smaller than this are ignored.</summary>
    public long MinSize { get; set; } = 1;

    /// <summary>Folders: also report folders whose contents are all found in another folder.</summary>
    public bool Subsets { get; set; } = true;

    /// <summary>False for scans whose files cannot be read (remote sources, index files): names and sizes only.</summary>
    public bool CanRead { get; set; } = true;

    public bool ReadsContents => CanRead && Match != DuplicateMatch.NameAndSize;
    public bool SameName => Match != DuplicateMatch.Contents || !CanRead;
}

/// <summary>Files with the same contents.</summary>
public sealed class DuplicateFileGroup
{
    public int Number { get; internal set; }
    public long Size { get; init; }
    public string Hash { get; init; } = "";
    public required List<FileItem> Files { get; init; }

    /// <summary>Files that already are hard links of each other share one copy on disk.</summary>
    public int DistinctCopies { get; internal set; }

    /// <summary>Space freed when only one copy is kept.</summary>
    public long Wasted => Size * Math.Max(0, DistinctCopies - 1);
}

/// <summary>Folders with the same contents, or folders whose files all are in a bigger one.</summary>
public sealed class DuplicateFolderGroup
{
    public int Number { get; internal set; }

    /// <summary>Identical: all have the same contents. Subset: Folders[0] holds everything of the others.</summary>
    public bool IsSubset { get; init; }
    public required List<DirNode> Folders { get; init; }

    public long Wasted => IsSubset ? Folders.Skip(1).Sum(f => f.Size) : Folders[0].Size * (Folders.Count - 1);
}

public sealed class DuplicateProgress
{
    public string Phase { get; init; } = "";
    public long Done { get; init; }
    public long Total { get; init; }
}

public static class Duplicates
{
    // ---------------- files ----------------

    public static List<DuplicateFileGroup> FindFiles(DirNode root, DuplicateOptions o, IProgress<DuplicateProgress>? progress = null, CancellationToken ct = default)
    {
        var hashes = new ConcurrentDictionary<FileItem, string>();
        return FindFiles(root.AllFiles(), o, hashes, progress, ct);
    }

    private static List<DuplicateFileGroup> FindFiles(IEnumerable<FileItem> files, DuplicateOptions o, ConcurrentDictionary<FileItem, string> hashes, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var min = Math.Max(1, o.MinSize);
        var bySize = files
            .Where(f => f.Size >= min)
            .GroupBy(f => (f.Size, Name: o.SameName ? f.Name.ToLowerInvariant() : ""))
            .Where(g => g.Skip(1).Any())
            .Select(g => g.ToList())
            .OrderByDescending(g => g[0].Size)
            .ToList();
        var groups = new List<DuplicateFileGroup>();
        if (!o.ReadsContents)
        {
            foreach (var g in bySize)
                groups.Add(new DuplicateFileGroup { Size = g[0].Size, Files = g });
            return Finish(groups, readIds: o.CanRead);
        }

        var total = bySize.Sum(g => (long)g.Count);
        long done = 0;
        var found = new ConcurrentBag<DuplicateFileGroup>();
        Parallel.ForEach(bySize, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 }, g =>
        {
            foreach (var set in SameContents(g, o.Hash, hashes, ct))
                found.Add(set);
            var d = Interlocked.Add(ref done, g.Count);
            progress?.Report(new DuplicateProgress { Phase = "Comparing contents", Done = d, Total = total });
        });
        groups.AddRange(found.OrderByDescending(x => x.Size * (x.Files.Count - 1)));
        return Finish(groups, readIds: true);
    }

    /// <summary>Splits same-size files by a quick checksum (start + end), then by a full one.</summary>
    private static IEnumerable<DuplicateFileGroup> SameContents(List<FileItem> sameSize, HashKind kind, ConcurrentDictionary<FileItem, string> hashes, CancellationToken ct)
    {
        var quick = new Dictionary<string, List<FileItem>>();
        foreach (var f in sameSize)
        {
            ct.ThrowIfCancellationRequested();
            if (QuickHash(f.FullPath) is not { } q)
                continue;
            if (!quick.TryGetValue(q, out var list))
                quick[q] = list = [];
            list.Add(f);
        }
        foreach (var q in quick.Values.Where(l => l.Count > 1))
        {
            var full = new Dictionary<string, List<FileItem>>();
            foreach (var f in q)
            {
                var h = ContentHash(f, kind, hashes, ct);
                if (h == null)
                    continue;
                if (!full.TryGetValue(h, out var list))
                    full[h] = list = [];
                list.Add(f);
            }
            foreach (var (h, list) in full)
                if (list.Count > 1)
                    yield return new DuplicateFileGroup { Size = list[0].Size, Hash = h, Files = list };
        }
    }

    private static string? ContentHash(FileItem f, HashKind kind, ConcurrentDictionary<FileItem, string> hashes, CancellationToken ct)
    {
        if (hashes.TryGetValue(f, out var h))
            return h;
        h = SearchEngine.FullHash(f.FullPath, kind == HashKind.None ? HashKind.Md5 : kind, ct);
        if (h != null)
            hashes[f] = h;
        return h;
    }

    private static string? QuickHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            const int Part = 64 * 1024;
            var buf = new byte[Part * 2];
            var n = fs.Read(buf, 0, Part);
            if (fs.Length > Part * 2)
                fs.Seek(-Part, SeekOrigin.End);
            n += fs.Read(buf, n, Part);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buf.AsSpan(0, n)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<DuplicateFileGroup> Finish(List<DuplicateFileGroup> groups, bool readIds)
    {
        var n = 0;
        foreach (var g in groups)
        {
            g.Number = ++n;
            g.DistinctCopies = readIds ? CountCopies(g.Files) : g.Files.Count;
        }
        // groups that are only hard links of one file free nothing
        groups.RemoveAll(g => g.DistinctCopies < 2);
        n = 0;
        foreach (var g in groups)
            g.Number = ++n;
        return groups;
    }

    /// <summary>Number of different files on disk (hard links of one file count once).</summary>
    private static int CountCopies(List<FileItem> files)
    {
        var ids = new HashSet<(ulong, ulong)>();
        var unknown = 0;
        foreach (var f in files)
        {
            if (FileIdentity.Of(f.FullPath) is { } id)
                ids.Add(id);
            else
                unknown++;
        }
        return ids.Count + unknown;
    }

    // ---------------- folders ----------------

    /// <summary>
    /// Folders with the same names, sizes (and contents, when read) of every file and sub folder; only the highest
    /// folders of a match are reported (their sub folders match too). With subsets on, also folders whose files are
    /// all found at the same places in a bigger folder.
    /// </summary>
    public static List<DuplicateFolderGroup> FindFolders(DirNode root, DuplicateOptions o, IProgress<DuplicateProgress>? progress = null, CancellationToken ct = default)
    {
        var folders = root.AllFolders().Where(d => !ReferenceEquals(d, root)).ToList();
        var min = Math.Max(1, o.MinSize);

        // 1. a signature of names + sizes, bottom-up
        progress?.Report(new DuplicateProgress { Phase = "Comparing folder structure" });
        var sig = new Dictionary<DirNode, UInt128>(folders.Count + 1);
        Signature(root, sig, null, ct);
        var candidates = folders
            .Where(d => d.Size >= min && d.FileCount > 0)
            .GroupBy(d => sig[d])
            .Where(g => g.Skip(1).Any())
            .Select(g => g.ToList())
            .ToList();

        // 2. contents: the same signature again, now with the checksum of every file
        var hashes = new ConcurrentDictionary<FileItem, string>();
        if (o.ReadsContents && candidates.Count > 0)
        {
            var files = candidates.SelectMany(g => g).Distinct().SelectMany(d => d.AllFiles()).Distinct().ToList();
            HashAll(files, o.Hash, hashes, progress, ct);
            var verified = new Dictionary<DirNode, UInt128>();
            var split = new List<List<DirNode>>();
            foreach (var g in candidates)
            {
                foreach (var d in g)
                    Signature(d, verified, hashes, ct);
                split.AddRange(g.GroupBy(d => verified[d]).Where(x => x.Skip(1).Any()).Select(x => x.ToList()));
            }
            candidates = split;
        }

        // 3. keep the highest folders of a match: drop a group when every folder's parent is in one other group
        var groupOf = new Dictionary<DirNode, int>();
        for (var i = 0; i < candidates.Count; i++)
            foreach (var d in candidates[i])
                groupOf[d] = i;
        var groups = new List<DuplicateFolderGroup>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var g = candidates[i];
            // folders inside each other (a\x and a\x\x with the same contents) cannot both be kept apart
            g = [.. g.Where(d => !g.Exists(o2 => !ReferenceEquals(o2, d) && IsBelow(d, o2)))];
            if (g.Count < 2)
                continue;
            var parents = g.Select(d => d.Parent != null && groupOf.TryGetValue(d.Parent, out var pg) ? pg : -1).Distinct().ToList();
            if (parents.Count == 1 && parents[0] >= 0)
                continue;
            groups.Add(new DuplicateFolderGroup { Folders = [.. g.OrderBy(d => d.Level).ThenBy(d => d.FullPath, StringComparer.OrdinalIgnoreCase)] });
        }

        if (o.Subsets)
        {
            var subsets = FindSubsets(folders, groupOf, o, min, hashes, progress, ct);
            // part\in = full\in is already said by "part is contained in full": drop identical groups
            // whose folders all sit directly in the folders of one subset group
            var subsetOf = new Dictionary<DirNode, int>();
            for (var i = 0; i < subsets.Count; i++)
                foreach (var d in subsets[i].Folders)
                    subsetOf.TryAdd(d, i);
            groups.RemoveAll(g =>
            {
                var owners = g.Folders.Select(d => d.Parent != null && subsetOf.TryGetValue(d.Parent, out var k) ? k : -1).Distinct().ToList();
                return owners.Count == 1 && owners[0] >= 0;
            });
            groups.AddRange(subsets);
        }

        groups = [.. groups.OrderByDescending(g => g.Wasted)];
        var n = 0;
        foreach (var g in groups)
            g.Number = ++n;
        return groups;
    }

    private static void HashAll(List<FileItem> files, HashKind kind, ConcurrentDictionary<FileItem, string> hashes, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        long done = 0;
        var total = files.Count;
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 }, f =>
        {
            ContentHash(f, kind, hashes, ct);
            var d = Interlocked.Increment(ref done);
            if ((d & 31) == 0 || d == total)
                progress?.Report(new DuplicateProgress { Phase = "Comparing contents", Done = d, Total = total });
        });
    }

    private static bool IsBelow(DirNode d, DirNode ancestor)
    {
        for (var p = d.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, ancestor))
                return true;
        return false;
    }

    /// <summary>Order-independent 128-bit signature of a folder's names, sizes (and checksums).</summary>
    private static UInt128 Signature(DirNode d, Dictionary<DirNode, UInt128> sig, ConcurrentDictionary<FileItem, string>? hashes, CancellationToken ct)
    {
        // iterative post-order: deep trees must not overflow the stack
        var stack = new Stack<(DirNode Node, bool Done)>();
        stack.Push((d, false));
        var n = 0;
        while (stack.Count > 0)
        {
            var (node, ready) = stack.Pop();
            if (!ready)
            {
                if ((++n & 4095) == 0)
                    ct.ThrowIfCancellationRequested();
                stack.Push((node, true));
                foreach (var c in node.Dirs)
                    stack.Push((c, false));
                continue;
            }
            var dirs = node.Dirs;
            var files = node.Files;
            UInt128 acc = (UInt128)(ulong)dirs.Length << 64 | (ulong)files.Length;
            foreach (var f in files)
            {
                var h = Mix(Hash128(f.Name.ToLowerInvariant()), (ulong)f.Size);
                if (hashes != null)
                    h = Mix(h ^ Hash128(hashes.TryGetValue(f, out var x) ? x : "?" + f.FullPath), 1);
                acc += h; // sum: order does not matter
            }
            foreach (var c in dirs)
                acc += Mix(Hash128("/" + c.Name.ToLowerInvariant()) ^ sig[c], 2);
            sig[node] = acc;
        }
        return sig[d];
    }

    private static UInt128 Hash128(string s)
    {
        // two FNV-1a 64 streams with different offsets, finalised by Mix
        ulong a = 0xCBF29CE484222325UL, b = 0x84222325CBF29CE4UL;
        foreach (var ch in s)
        {
            a = (a ^ ch) * 0x100000001B3UL;
            b = ((b ^ ch) * 0x100000001B3UL) ^ (b >> 29);
        }
        return Mix((UInt128)b << 64 | a, (ulong)s.Length);
    }

    private static UInt128 Mix(UInt128 h, ulong v)
    {
        // splitmix-style finaliser on both halves
        static ulong F(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        var lo = (ulong)h;
        var hi = (ulong)(h >> 64);
        return (UInt128)F(hi ^ F(v + 0x9E3779B97F4A7C15UL)) << 64 | F(lo ^ v);
    }

    /// <summary>Folders whose every file (same relative path and size) is also in a bigger folder elsewhere.</summary>
    private static List<DuplicateFolderGroup> FindSubsets(List<DirNode> folders, Dictionary<DirNode, int> identical, DuplicateOptions o, long min, ConcurrentDictionary<FileItem, string> hashes, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new DuplicateProgress { Phase = "Looking for folders contained in others" });
        // where does a file of this name and size live
        var byKey = new Dictionary<(string, long), List<FileItem>>();
        foreach (var d in folders)
            foreach (var f in d.Files)
            {
                if (f.Size <= 0)
                    continue;
                var k = (f.Name.ToLowerInvariant(), f.Size);
                if (!byKey.TryGetValue(k, out var list))
                    byKey[k] = list = [];
                list.Add(f);
            }

        var found = new Dictionary<DirNode, DirNode>(); // subset -> containing folder
        var n = 0;
        foreach (var a in folders.Where(d => d.Size >= min && d.FileCount > 0 && d.FileCount <= 50_000 && !identical.ContainsKey(d)).OrderByDescending(d => d.Size))
        {
            if ((++n & 255) == 0)
                ct.ThrowIfCancellationRequested();
            // the parent is already contained somewhere: its sub folders are, too
            if (a.Parent != null && found.ContainsKey(a.Parent))
                continue;
            var files = Relative(a);
            if (files.Count == 0)
                continue;
            var (probeRel, probe) = files.MaxBy(x => x.File.Size);
            if (!byKey.TryGetValue((probe.Name.ToLowerInvariant(), probe.Size), out var hits))
                continue;
            var depth = probeRel.Count(ch => ch == '/');
            foreach (var hit in hits)
            {
                if (ReferenceEquals(hit, probe))
                    continue;
                var b = Ancestor(hit, depth);
                if (b == null || ReferenceEquals(b, a) || IsBelow(a, b) || IsBelow(b, a) || b.Size <= a.Size)
                    continue;
                if (!RelativePathMatches(hit, b, probeRel) || !Contains(b, files, o, hashes, ct))
                    continue;
                found[a] = b;
                break;
            }
        }
        return [.. found.GroupBy(kv => kv.Value).Select(g => new DuplicateFolderGroup
        {
            IsSubset = true,
            Folders = [g.Key, .. g.Select(kv => kv.Key).OrderByDescending(d => d.Size)],
        })];
    }

    /// <summary>All files of a folder with their path below it ("sub/name").</summary>
    private static List<(string Rel, FileItem File)> Relative(DirNode root)
    {
        var list = new List<(string, FileItem)>();
        var stack = new Stack<(DirNode, string)>();
        stack.Push((root, ""));
        while (stack.Count > 0)
        {
            var (d, prefix) = stack.Pop();
            foreach (var f in d.Files)
                list.Add((prefix + f.Name, f));
            foreach (var c in d.Dirs)
                stack.Push((c, prefix + c.Name + "/"));
        }
        return list;
    }

    private static DirNode? Ancestor(FileItem f, int levels)
    {
        var d = f.Parent;
        for (var i = 0; i < levels && d != null; i++)
            d = d.Parent;
        return d;
    }

    private static bool RelativePathMatches(FileItem f, DirNode b, string rel)
    {
        var parts = rel.Split('/');
        var d = f.Parent;
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            if (d == null || !string.Equals(d.Name, parts[i], StringComparison.OrdinalIgnoreCase))
                return false;
            d = d.Parent;
        }
        return ReferenceEquals(d, b);
    }

    private static bool Contains(DirNode b, List<(string Rel, FileItem File)> files, DuplicateOptions o, ConcurrentDictionary<FileItem, string> hashes, CancellationToken ct)
    {
        foreach (var (rel, f) in files)
        {
            var parts = rel.Split('/');
            var d = b;
            for (var i = 0; i < parts.Length - 1 && d != null; i++)
                d = d.Dirs.FirstOrDefault(x => string.Equals(x.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            var other = d?.Files.FirstOrDefault(x => string.Equals(x.Name, parts[^1], StringComparison.OrdinalIgnoreCase));
            if (other == null || other.Size != f.Size)
                return false;
        }
        if (!o.ReadsContents)
            return true;
        // names and sizes match: now the contents
        foreach (var (rel, f) in files)
        {
            var parts = rel.Split('/');
            var d = b;
            for (var i = 0; i < parts.Length - 1; i++)
                d = d!.Dirs.First(x => string.Equals(x.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            var other = d!.Files.First(x => string.Equals(x.Name, parts[^1], StringComparison.OrdinalIgnoreCase));
            var h1 = ContentHash(f, o.Hash, hashes, ct);
            if (h1 == null || h1 != ContentHash(other, o.Hash, hashes, ct))
                return false;
        }
        return true;
    }
}

/// <summary>Identity of a file on disk (volume + file id / device + inode): hard links share it.</summary>
public static class FileIdentity
{
    public static (ulong, ulong)? Of(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var h = Native.Win32.CreateFile(Native.Win32.LongPath(path), 0, Native.Win32.FILE_SHARE_READ | Native.Win32.FILE_SHARE_WRITE | Native.Win32.FILE_SHARE_DELETE, IntPtr.Zero, Native.Win32.OPEN_EXISTING, Native.Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid || !Native.Win32.GetFileInformationByHandle(h, out var info))
                return null;
            return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }
        if (OperatingSystem.IsLinux() && Native.UnixStat.TryGet(path, out var i))
            return (i.Device, i.Inode);
        return null;
    }
}
