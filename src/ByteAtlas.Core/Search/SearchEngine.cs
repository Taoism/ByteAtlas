using System.Security.AccessControl;
using System.Security.Cryptography;
using ByteAtlas.Analysis;
using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas.Search;

/// <summary>One hit of the file search.</summary>
public sealed class SearchResult
{
    public required Item Item { get; init; }
    public string FullPath { get; init; } = "";
    public string Name => Item.Name;
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
    public long Size => Item.Size;
    public long Allocated => Item.Allocated;
    public string Owner { get; init; } = "";

    /// <summary>Duplicate group number (1-based), 0 for normal searches.</summary>
    public int Group { get; set; }
    public string? Hash { get; set; }

    /// <summary>Extra text (permission search: the access found).</summary>
    public string Detail { get; set; } = "";
    public bool Checked { get; set; }
}

public sealed class SearchProgress
{
    public string Phase { get; set; } = "";
    public long Done { get; set; }
    public long Total { get; set; }
}

public static class SearchEngine
{
    /// <summary>Searches already scanned trees (fast, no disk access).</summary>
    public static List<SearchResult> Search(IEnumerable<ScanSession> scans, SearchCriteria c, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        foreach (var s in scans)
            SearchTree(s, s.Root, c, results, ct);
        if (c.Template == SearchTemplate.Duplicates)
            return results;
        if (c.SortBySizeDesc)
            results.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (c.MaxResults > 0 && results.Count > c.MaxResults)
            results.RemoveRange(c.MaxResults, results.Count - c.MaxResults);
        return results;
    }

    /// <summary>Searches below one folder of a scan.</summary>
    public static List<SearchResult> Search(ScanSession scan, DirNode branch, SearchCriteria c, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        SearchTree(scan, branch, c, results, ct);
        if (c.SortBySizeDesc)
            results.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (c.MaxResults > 0 && results.Count > c.MaxResults)
            results.RemoveRange(c.MaxResults, results.Count - c.MaxResults);
        return results;
    }

    /// <summary>Scans the given paths (drives, folders, shares) and searches them.</summary>
    public static async Task<List<SearchResult>> SearchPathsAsync(IEnumerable<string> paths, SearchCriteria c, ScanOptions options, IProgress<SearchProgress>? progress, CancellationToken ct)
    {
        var scans = new List<ScanSession>();
        foreach (var p in paths)
        {
            progress?.Report(new SearchProgress { Phase = "Scanning " + p });
            var o = options.Clone();
            o.CollectOwners = !string.IsNullOrEmpty(c.Owner);
            var s = new ScanSession(p, o);
            await Scanner.ScanAsync(s, ct);
            scans.Add(s);
        }
        progress?.Report(new SearchProgress { Phase = "Searching" });
        return Search(scans, c, ct);
    }

    private static void SearchTree(ScanSession s, DirNode root, SearchCriteria c, List<SearchResult> results, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(c.PermissionAccount))
        {
            if (OperatingSystem.IsWindows())
                PermissionSearch(root, c, results, ct);
            return;
        }
        var owner = string.IsNullOrEmpty(c.Owner) ? null : c.Owner;
        var stack = new Stack<(DirNode Node, string Path)>();
        stack.Push((root, root.FullPath));
        var n = 0;
        while (stack.Count > 0)
        {
            var (d, path) = stack.Pop();
            if ((++n & 1023) == 0)
                ct.ThrowIfCancellationRequested();
            foreach (var sub in d.Dirs)
            {
                if (c.Exclude.Any(r => r.Enabled && r.Folders && r.Matches(sub.Name)))
                    continue;
                var subPath = Path.Combine(path, sub.Name);
                if (c.IncludeFolders && MatchesFolder(sub, subPath, c))
                    results.Add(new SearchResult { Item = sub, FullPath = subPath });
                stack.Push((sub, subPath));
            }
            foreach (var f in d.Files)
            {
                if (!Matches(f, path, c, s, owner, out var full, out var ownerName))
                    continue;
                results.Add(new SearchResult { Item = f, FullPath = full, Owner = ownerName });
                if (c.MaxResults > 0 && !c.SortBySizeDesc && results.Count >= c.MaxResults && c.Template != SearchTemplate.Duplicates)
                    return;
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void PermissionSearch(DirNode root, SearchCriteria c, List<SearchResult> results, CancellationToken ct)
    {
        var sids = Security.Permissions.SidsOf(c.PermissionAccount!.Trim(), out _);
        if (sids.Count == 0)
            throw new ArgumentException($"The account \"{c.PermissionAccount}\" is not known.");
        var need = c.PermissionLevel switch
        {
            AccessLevel.Read => FileSystemRights.Read,
            AccessLevel.Write => FileSystemRights.Write,
            AccessLevel.Modify => FileSystemRights.Modify,
            AccessLevel.FullControl => FileSystemRights.FullControl,
            _ => (FileSystemRights)0,
        };
        need &= ~FileSystemRights.Synchronize;
        var items = new List<Item>();
        foreach (var d in root.AllFolders())
        {
            if (c.Exclude.Any(r => r.Enabled && r.Folders && r.Matches(d.Name)))
                continue;
            items.Add(d);
            if (!c.PermissionFoldersOnly)
                items.AddRange(d.Files);
        }
        var found = new System.Collections.Concurrent.ConcurrentBag<SearchResult>();
        Parallel.ForEach(items, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 8 }, item =>
        {
            var path = item.FullPath;
            var p = Security.Permissions.Read(path);
            if (p.Error != null)
                return;
            var entries = p.Entries.Where(e => e.Sid != null && sids.Contains(e.Sid) && (!c.PermissionExplicitOnly || !e.Inherited)).ToList();
            if (entries.Count == 0 || entries.TrueForAll(e => !e.Allow))
                return;
            var rights = Security.Permissions.EffectiveFor(p, sids);
            if (need != 0 && (rights & need) != need)
                return;
            var who = string.Join(", ", entries.Where(e => e.Allow).Select(e => Security.Permissions.ShortName(e.Identity)).Distinct());
            found.Add(new SearchResult
            {
                Item = item,
                FullPath = path,
                Owner = p.Owner,
                Detail = $"{Security.Permissions.Describe(rights)}: {who}{(entries.Exists(e => !e.Inherited) ? " (explicit)" : " (inherited)")}",
            });
        });
        results.AddRange(found.OrderBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase));
    }

    private static bool MatchesFolder(DirNode d, string full, SearchCriteria c)
    {
        if (c.MinPathLength is { } len)
            return full.Length >= len;
        if (c.Include.Count > 0 && !c.Include.Any(r => r.Enabled && r.Matches(d.Name)))
            return false;
        return c.Include.Count > 0;
    }

    private static bool Matches(FileItem f, string folder, SearchCriteria c, ScanSession s, string? owner, out string full, out string ownerName)
    {
        full = "";
        ownerName = "";
        if (c.MinSize is { } min && f.Size < min)
            return false;
        if (c.MaxSize is { } max && f.Size > max)
            return false;
        if ((f.Attributes & c.AttributesSet) != c.AttributesSet)
            return false;
        if ((f.Attributes & c.AttributesNotSet) != 0)
            return false;
        if (c.OlderThan != null || c.NewerThan != null)
        {
            var d = c.DateBasis switch
            {
                AgeBasis.Accessed => f.Accessed,
                AgeBasis.Created => f.Created,
                _ => f.Modified,
            };
            if (c.OlderThan is { } o && d >= o)
                return false;
            if (c.NewerThan is { } nw && d < nw)
                return false;
        }
        if (c.Include.Count > 0 && !c.Include.Any(r => r.Enabled && !r.Folders && r.Matches(f.Name)))
            return false;
        if (c.Exclude.Any(r => r.Enabled && !r.Folders && r.Matches(f.Name)))
            return false;
        ownerName = f.OwnerId >= 0 ? s.Owners.Name(f.OwnerId) : "";
        if (owner != null && !ownerName.Contains(owner, StringComparison.OrdinalIgnoreCase))
            return false;
        full = Path.Combine(folder, f.Name);
        if (c.MinPathLength is { } len && full.Length < len)
            return false;
        return true;
    }

    // ---------- duplicates ----------

    /// <summary>
    /// Finds duplicate files: same size (and name / date when asked), then the same content
    /// (a hash of the first and last 64 KB first, then of the whole file).
    /// </summary>
    public static List<SearchResult> FindDuplicates(IEnumerable<SearchResult> candidates, SearchCriteria c, IProgress<SearchProgress>? progress, CancellationToken ct)
    {
        var bySize = candidates
            .Where(r => r.Item is FileItem && r.Size > 0)
            .GroupBy(r => (r.Size, Name: c.DuplicatesSameName ? r.Name.ToLowerInvariant() : "", Date: c.DuplicatesSameDate ? r.Item.ModifiedTicks / TimeSpan.TicksPerSecond : 0))
            .Where(g => g.Count() > 1)
            .ToList();

        var output = new List<SearchResult>();
        var group = 0;
        var total = bySize.Sum(g => (long)g.Count());
        long done = 0;
        foreach (var g in bySize.OrderByDescending(g => g.Key.Size))
        {
            ct.ThrowIfCancellationRequested();
            var items = g.ToList();
            done += items.Count;
            progress?.Report(new SearchProgress { Phase = "Comparing contents", Done = done, Total = total });
            if (c.DuplicatesHash == HashKind.None)
            {
                group++;
                foreach (var r in items)
                {
                    r.Group = group;
                    output.Add(r);
                }
                continue;
            }
            // quick hash of head + tail first
            var quick = items.GroupBy(r => QuickHash(r.FullPath) ?? Guid.NewGuid().ToString()).Where(q => q.Count() > 1);
            foreach (var q in quick)
            {
                var full = q.GroupBy(r => FullHash(r.FullPath, c.DuplicatesHash, ct) ?? Guid.NewGuid().ToString()).Where(x => x.Count() > 1);
                foreach (var set in full)
                {
                    group++;
                    foreach (var r in set)
                    {
                        r.Group = group;
                        r.Hash = set.Key;
                        output.Add(r);
                    }
                }
            }
        }
        return output;
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
            {
                fs.Seek(-Part, SeekOrigin.End);
                n += fs.Read(buf, n, Part);
            }
            else if (fs.Length > Part)
                n += fs.Read(buf, n, Part);
            return Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, n)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? FullHash(string path, HashKind kind, CancellationToken ct = default)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            using HashAlgorithm h = kind == HashKind.Sha256 ? SHA256.Create() : MD5.Create();
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                h.TransformBlock(buf, 0, n, null, 0);
            }
            h.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(h.Hash!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
