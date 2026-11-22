using ByteAtlas.Model;

namespace ByteAtlas.Scanning;

/// <summary>Runs a scan with the best method for the path and finishes the tree (totals, dates, sorting).</summary>
public static class Scanner
{
    public static Task ScanAsync(ScanSession s, CancellationToken ct) =>
        Task.Factory.StartNew(() => Scan(s, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static void Scan(ScanSession s, CancellationToken ct)
    {
        s.State = ScanState.Scanning;
        s.Started = DateTime.Now;
        s.ScanDate = s.Started;
        try
        {
            var path = s.Root.Name;
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Path not found: {path}");
            var vol = VolumeInfo.Get(path);
            s.VolumeTotal = vol.Total;
            s.VolumeFree = vol.Free;
            s.ClusterSize = vol.ClusterSize;
            s.FileSystem = vol.FileSystem;

            var useMft = false;
            if (OperatingSystem.IsWindows() && s.Options.Method != ScanMethod.FileSystem)
            {
                useMft = MftScanner.CanScan(path, out var why);
                if (!useMft && s.Options.Method == ScanMethod.Mft)
                    throw new InvalidOperationException("The master file table cannot be read: " + why);
                // following mount points needs the folder walk (the MFT knows one volume only)
                if (s.Options.FollowMountPoints && s.Options.Method == ScanMethod.Auto)
                    useMft = false;
            }

            // a folder below the drive root: the master file table costs the same as for the whole drive,
            // so try a quick folder walk first and only read the table when the folder turns out to be big
            if (useMft && s.Options.Method == ScanMethod.Auto && !IsVolumeRoot(path, vol))
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(QuickWalkBudget);
                // small folders: asking each file for its real space on disk (compact compression) is cheap
                var detect = s.Options.DetectCompactCompression;
                s.Options.DetectCompactCompression = true;
                try
                {
                    new FileSystemScanner(s).Run(s.Root, path, budget.Token);
                    useMft = false;
                    s.Timings = $"quick folder walk ({QuickWalkBudget.TotalSeconds:N0} s budget) was enough";
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Reset(s);
                }
                finally
                {
                    s.Options.DetectCompactCompression = detect;
                }
            }
            else if (useMft && OperatingSystem.IsWindows())
                useMft = true;
            else
                new FileSystemScanner(s).Run(s.Root, path, ct);

            if (useMft && OperatingSystem.IsWindows())
            {
                try
                {
                    new MftScanner(s).Run(ct);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && s.Options.Method == ScanMethod.Auto)
                {
                    s.AddError(path, "Master file table not readable, walked folders instead: " + e.Message);
                    Reset(s);
                    new FileSystemScanner(s).Run(s.Root, path, ct);
                }
            }

            Finish(s);
            s.State = ScanState.Done;
        }
        catch (OperationCanceledException)
        {
            Finish(s);
            s.State = ScanState.Cancelled;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            s.AddError(s.Path, e.Message);
            s.Root.Error = e.Message;
            Finish(s);
            s.State = ScanState.Failed;
        }
        finally
        {
            s.Finished = DateTime.Now;
            s.CurrentPath = "";
            s.Fraction = -1;
        }
    }

    /// <summary>How long a folder walk may take before a big folder is read from the master file table instead.</summary>
    public static TimeSpan QuickWalkBudget { get; set; } = TimeSpan.FromSeconds(2);

    private static bool IsVolumeRoot(string path, VolumeInfo vol) =>
        string.Equals(ScanSession.NormalizeRoot(path).TrimEnd('\\', '/'), vol.Root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Throws away a partial result before scanning again another way.</summary>
    private static void Reset(ScanSession s)
    {
        s.Root.ClearChildren();
        s.Root._size = s.Root._allocated = s.Root._fileCount = s.Root._folderCount = 0;
        s.Root.Error = null;
        s._filesDone = s._foldersDone = 0;
        s.MethodUsed = "";
    }

    private static void Finish(ScanSession s)
    {
        var w = System.Diagnostics.Stopwatch.StartNew();
        s.Root.Recalculate(s.Options.FolderDateFromFilesOnly);
        s.Root.Sort(bySize: true);
        foreach (var d in s.Root.AllFolders())
            d.Pending = false;
        s.Timings += (s.Timings.Length > 0 ? ", " : "") + $"totals + sort {w.Elapsed.TotalSeconds:N1} s";
    }

    /// <summary>
    /// Refreshes a whole scan: through the NTFS change journal when the scan was read from the master file table
    /// (only folders that changed are looked at again), else by scanning everything again.
    /// </summary>
    public static Task RefreshChangesAsync(ScanSession s, CancellationToken ct, bool keepVisible = false) =>
        Task.Factory.StartNew(() =>
        {
            if (TryJournalRefresh(s, ct))
                return;
            if (!keepVisible)
            {
                s.Root.ClearChildren();
                Scan(s, ct);
                return;
            }
            // scan into a second session and swap when done: the old tree stays on screen meanwhile
            var fresh = new ScanSession(s.Path, s.Options) { Title = s.Title };
            s.Progress = fresh;
            s.State = ScanState.Scanning;
            try
            {
                Scan(fresh, ct);
            }
            finally
            {
                s.Progress = null;
            }
            if (fresh.State == ScanState.Done)
                s.TakeOver(fresh);
            else
                s.State = ScanState.Done;
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>The change-journal refresh; false when it cannot be used (then scan normally).</summary>
    public static bool TryJournalRefresh(ScanSession s, CancellationToken ct, int maxFolders = 20_000)
    {
        if (!OperatingSystem.IsWindows() || s.Usn is not { } from || s.IndexFile != null && s.Usn == null)
            return false;
        var root = Path.GetPathRoot(s.Root.FullPath);
        if (string.IsNullOrEmpty(root))
            return false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var prev = s.State;
        s.State = ScanState.Scanning;
        s.Started = DateTime.Now;
        try
        {
            s.CurrentPath = "Reading the change journal";
            var changes = Native.UsnJournal.ChangedFolders(root, from, maxFolders, ct);
            if (changes == null)
            {
                s.State = prev;
                return false;
            }
            var map = new Dictionary<long, DirNode>();
            foreach (var d in s.Root.AllFolders())
                if (d.Frn > 0)
                    map.TryAdd(d.Frn, d);
            var targets = changes.Value.Folders.Where(map.ContainsKey).Select(f => map[f]).OrderBy(d => d.Level).ToList();
            var done = 0;
            foreach (var t in targets)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsAttached(s.Root, t))
                    continue; // removed by the refresh of a folder above
                s.CurrentPath = t.FullPath;
                Shallow(s, t, ct);
                done++;
            }
            s.Usn = changes.Value.Now;
            s.Root.Recalculate(s.Options.FolderDateFromFilesOnly);
            s.Root.Sort(bySize: true);
            s.ScanDate = DateTime.Now;
            s.Timings = $"change journal: {changes.Value.Folders.Count} changed folders, {done} looked at again, {watch.Elapsed.TotalSeconds:N1} s";
            s.State = ScanState.Done;
            return true;
        }
        catch (OperationCanceledException)
        {
            s.State = ScanState.Cancelled;
            return true;
        }
        finally
        {
            s.Finished = DateTime.Now;
            s.CurrentPath = "";
        }
    }

    private static bool IsAttached(DirNode root, DirNode d)
    {
        for (DirNode? p = d; p != null; p = p.Parent)
            if (ReferenceEquals(p, root))
                return true;
        return false;
    }

    /// <summary>
    /// Looks at one folder again: its files, which sub folders are new (scanned fully) or gone. Sub folders that are
    /// still there keep their contents.
    /// </summary>
    internal static void Shallow(ScanSession s, DirNode node, CancellationToken ct)
    {
        var path = node.FullPath;
        if (!Directory.Exists(path))
        {
            node.Parent?.RemoveChild(node);
            return;
        }
        node.ClearFiles();
        RescanFilesOnly(s, node, path);
        var existing = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in node.Dirs)
            existing.TryAdd(d.Name, d);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var di in new DirectoryInfo(path).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                present.Add(di.Name);
                if (existing.TryGetValue(di.Name, out var keep))
                {
                    keep.Attributes = di.Attributes;
                    keep.SetOwnDates(di.CreationTimeUtc.Ticks, di.LastWriteTimeUtc.Ticks, di.LastAccessTimeUtc.Ticks);
                    continue;
                }
                if (!s.Options.IncludeFolder(di.Name, di.FullName))
                    continue;
                var nd = new DirNode(di.Name, node) { Attributes = di.Attributes };
                nd.SetOwnDates(di.CreationTimeUtc.Ticks, di.LastWriteTimeUtc.Ticks, di.LastAccessTimeUtc.Ticks);
                node.AddDir(nd);
                if ((di.Attributes & FileAttributes.ReparsePoint) == 0 || s.Options.FollowMountPoints)
                    new FileSystemScanner(s).Run(nd, di.FullName, ct);
                nd.Pending = false;
                if (OperatingSystem.IsWindows())
                    foreach (var d in nd.AllFolders())
                        d.Frn = Native.UsnJournal.FolderReference(d.FullPath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            node.Error = e.Message;
        }
        foreach (var (name, d) in existing)
            if (!present.Contains(name))
                node.RemoveChild(d);
    }

    /// <summary>Scans a folder of an existing scan again (with or without its sub folders).</summary>
    public static Task RefreshAsync(ScanSession s, DirNode node, bool recursive, CancellationToken ct) =>
        Task.Factory.StartNew(() => Refresh(s, node, recursive, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static void Refresh(ScanSession s, DirNode node, bool recursive, CancellationToken ct)
    {
        if (node.Parent == null && recursive)
        {
            node.ClearChildren();
            Scan(s, ct);
            return;
        }
        var prev = s.State;
        s.State = ScanState.Scanning;
        try
        {
            var path = node.FullPath;
            var keepDirs = recursive ? [] : node.Dirs;
            node.ClearChildren();
            if (recursive)
                new FileSystemScanner(s).Run(node, path, ct);
            else
            {
                RescanFilesOnly(s, node, path);
                foreach (var d in keepDirs)
                    node.AddDir(d);
            }
            // totals passed up during the scan are stale: recompute the whole tree once
            s.Root.Recalculate(s.Options.FolderDateFromFilesOnly);
            node.Sort(true);
        }
        finally
        {
            s.State = prev is ScanState.Scanning ? ScanState.Done : prev;
            foreach (var d in node.AllFolders())
                d.Pending = false;
        }
    }

    private static void RescanFilesOnly(ScanSession s, DirNode node, string path)
    {
        var vol = VolumeInfo.Get(path);
        var cluster = Math.Max(512, vol.ClusterSize);
        try
        {
            foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                if (s.Options.HasFilters && !s.Options.IncludeFile(fi.Name))
                    continue;
                var f = new FileItem(fi.Name, node)
                {
                    Attributes = fi.Attributes,
                    CreatedTicks = fi.CreationTimeUtc.Ticks,
                    ModifiedTicks = fi.LastWriteTimeUtc.Ticks,
                    AccessedTicks = fi.LastAccessTimeUtc.Ticks,
                    LinkCount = 1,
                };
                var alloc = fi.Length <= 0 ? 0 : (fi.Length + cluster - 1) / cluster * cluster;
                if (OperatingSystem.IsWindows())
                {
                    // one folder only: asking each file is cheap (also catches compact compression)
                    var c = Native.Win32.CompressedSize(fi.FullName);
                    if (c >= 0)
                        alloc = (c + cluster - 1) / cluster * cluster;
                    if (s.Options.CollectOwners && Native.Win32.GetOwner(fi.FullName) is { } owner)
                        f.OwnerId = s.Owners.GetId(owner);
                }
                f.SetSizes(fi.Length, alloc);
                node.AddFile(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            node.Error = e.Message;
        }
    }
}
