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
                if (OperatingSystem.IsWindows() && (fi.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.ReparsePoint)) != 0)
                {
                    var c = Native.Win32.CompressedSize(fi.FullName);
                    if (c >= 0)
                        alloc = (c + cluster - 1) / cluster * cluster;
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
