using System.Collections.Concurrent;
using System.IO.Enumeration;
using ByteAtlas.Model;
using ByteAtlas.Native;

namespace ByteAtlas.Scanning;

/// <summary>Walks folders with several threads (works everywhere: local, network, other file systems).</summary>
public sealed class FileSystemScanner
{
    private readonly ScanSession _s;
    private readonly ScanOptions _o;
    private readonly ConcurrentStack<DirNode> _work = new();
    private readonly ConcurrentDictionary<DirNode, string> _paths = new();
    private readonly ConcurrentDictionary<(uint, ulong), byte> _seenLinks = new();
    private readonly ConcurrentDictionary<string, byte> _visitedTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<DirNode, int> _waiting = new();
    private long _outstanding;
    private readonly ManualResetEventSlim _done = new(false);
    private int _cluster = 4096;

    public FileSystemScanner(ScanSession session)
    {
        _s = session;
        _o = session.Options;
    }

    public static int AutoThreads(VolumeInfo v) =>
        v.IsNetwork ? 8 : Math.Clamp(Environment.ProcessorCount, 4, 16);

    /// <summary>Scans <paramref name="node"/> (the root or a folder to refresh) below <paramref name="path"/>.</summary>
    public void Run(DirNode node, string path, CancellationToken ct)
    {
        var vol = VolumeInfo.Get(path);
        _cluster = Math.Max(512, vol.ClusterSize);
        var threads = _o.Threads > 0 ? Math.Clamp(_o.Threads, 1, 32) : AutoThreads(vol);
        if (string.IsNullOrEmpty(_s.MethodUsed))
            _s.MethodUsed = $"File system ({threads} threads)";

        _paths[node] = path;
        _outstanding = 1;
        _work.Push(node);
        var workers = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            workers[i] = new Thread(() => Worker(ct)) { IsBackground = true, Priority = _o.Priority, Name = "ByteAtlas scan " + i };
            workers[i].Start();
        }
        foreach (var t in workers)
            t.Join();
        ct.ThrowIfCancellationRequested();
    }

    private void Worker(CancellationToken ct)
    {
        var spin = new SpinWait();
        while (!ct.IsCancellationRequested)
        {
            if (Interlocked.Read(ref _outstanding) == 0)
                return;
            if (!_work.TryPop(out var dir))
            {
                if (spin.Count > 30)
                    _done.Wait(5);
                else
                    spin.SpinOnce();
                continue;
            }
            spin.Reset();
            try
            {
                _s.PauseGate.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                ScanFolder(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                dir.Error = e.Message;
                _s.AddError(_paths.GetValueOrDefault(dir) ?? dir.Name, e.Message);
                FolderDone(dir);
            }
            if (Interlocked.Decrement(ref _outstanding) == 0)
                _done.Set();
        }
    }

    private readonly record struct Entry(string Name, FileAttributes Attributes, long Length, long Created, long Modified, long Accessed);

    private sealed class EntryEnumerator(string dir, EnumerationOptions options) : FileSystemEnumerator<Entry>(dir, options)
    {
        public int Error { get; private set; }

        protected override Entry TransformEntry(ref FileSystemEntry e) =>
            new(e.FileName.ToString(), e.Attributes, e.Length, e.CreationTimeUtc.UtcTicks, e.LastWriteTimeUtc.UtcTicks, e.LastAccessTimeUtc.UtcTicks);

        protected override bool ContinueOnError(int error)
        {
            Error = error;
            return true;
        }
    }

    private static readonly EnumerationOptions EnumOptions = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private static string Combine(string dir, string name) =>
        dir.EndsWith('\\') || dir.EndsWith('/') ? dir + name : dir + Path.DirectorySeparatorChar + name;

    private void ScanFolder(DirNode dir)
    {
        var path = _paths[dir];
        _s.CurrentPath = path;
        var files = new List<FileItem>();
        var subdirs = new List<(DirNode Node, string Path)>();
        long ownSize = 0, ownAlloc = 0;
        var windows = OperatingSystem.IsWindows();
        var enumPath = windows ? Win32.LongPath(path) : path;

        using (var en = new EntryEnumerator(enumPath, EnumOptions))
        {
            while (en.MoveNext())
            {
                var e = en.Current;
                var full = Combine(path, e.Name);
                if ((e.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!_o.IncludeFolder(e.Name, full))
                        continue;
                    var d = new DirNode(e.Name, dir) { Attributes = e.Attributes };
                    d.SetOwnDates(e.Created, e.Modified, e.Accessed);
                    if (_o.CollectOwners && windows)
                        d.OwnerId = OwnerId(full);
                    subdirs.Add((d, full));
                    continue;
                }
                if (_o.HasFilters && !_o.IncludeFile(e.Name))
                    continue;
                var f = new FileItem(e.Name, dir)
                {
                    Attributes = e.Attributes,
                    CreatedTicks = e.Created,
                    ModifiedTicks = e.Modified,
                    AccessedTicks = e.Accessed,
                    LinkCount = 1,
                };
                var alloc = RoundUp(e.Length);
                if (windows && (e.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
                {
                    var c = Win32.CompressedSize(full);
                    if (c >= 0)
                        alloc = RoundUp(c);
                }
                if (windows && _o.TrackHardlinksAndStreams)
                    alloc = TrackLinksAndStreams(full, f, alloc);
                if (_o.CollectOwners && windows)
                    f.OwnerId = OwnerId(full);
                f.SetSizes(e.Length, alloc);
                ownSize += e.Length;
                ownAlloc += alloc;
                files.Add(f);
            }
            if (en.Error != 0 && files.Count == 0 && subdirs.Count == 0)
            {
                var msg = new System.ComponentModel.Win32Exception(en.Error).Message;
                dir.Error = msg;
                _s.AddError(path, msg);
            }
        }

        dir.AddFiles(files);
        dir.OwnFilesSize = ownSize;
        dir.OwnFilesAllocated = ownAlloc;
        dir.AddTotals(ownSize, ownAlloc, files.Count, subdirs.Count);
        Interlocked.Add(ref _s._filesDone, files.Count);
        Interlocked.Increment(ref _s._foldersDone);

        var toScan = new List<DirNode>(subdirs.Count);
        foreach (var (node, full) in subdirs)
        {
            dir.AddDir(node);
            if (node.IsReparsePoint && !ShouldFollow(full))
            {
                node.Pending = false;
                continue;
            }
            _paths[node] = full;
            toScan.Add(node);
        }

        _waiting[dir] = toScan.Count + 1;
        Interlocked.Add(ref _outstanding, toScan.Count);
        for (var i = toScan.Count - 1; i >= 0; i--)
            _work.Push(toScan[i]);
        FolderDone(dir);
    }

    /// <summary>Called once for the folder itself and once per finished sub folder.</summary>
    private void FolderDone(DirNode dir)
    {
        var n = dir;
        while (n != null)
        {
            var left = _waiting.AddOrUpdate(n, 0, (_, v) => v - 1);
            if (left > 0)
                return;
            _waiting.TryRemove(n, out _);
            _paths.TryRemove(n, out _);
            n.Pending = false;
            n = n.Parent;
            if (n == null || !_waiting.ContainsKey(n))
                return;
        }
    }

    private bool ShouldFollow(string full)
    {
        if (!_o.FollowMountPoints)
            return false;
        try
        {
            var target = Directory.ResolveLinkTarget(full, true)?.FullName;
            if (target == null)
                return true; // volume mount point: no link target, but a separate volume
            if (_s.Path.StartsWith(target, StringComparison.OrdinalIgnoreCase) || full.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                return false; // loops back into the scan
            return _visitedTargets.TryAdd(target, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private long RoundUp(long bytes) => bytes <= 0 ? 0 : (bytes + _cluster - 1) / _cluster * _cluster;

    private int OwnerId(string path)
    {
        if (!OperatingSystem.IsWindows())
            return -1;
        var owner = Win32.GetOwner(path);
        return owner == null ? -1 : _s.Owners.GetId(owner);
    }

    private long TrackLinksAndStreams(string full, FileItem f, long alloc)
    {
        if (!OperatingSystem.IsWindows())
            return alloc;
        using (var h = Win32.CreateFile(Win32.LongPath(full), 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
        {
            if (!h.IsInvalid && Win32.GetFileInformationByHandle(h, out var info))
            {
                f.LinkCount = (int)info.NumberOfLinks;
                if (info.NumberOfLinks > 1)
                {
                    var key = (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
                    if (!_seenLinks.TryAdd(key, 0))
                        alloc = 0; // counted at its first link already
                }
            }
        }
        var hFind = Win32.FindFirstStreamW(Win32.LongPath(full), 0, out var data, 0);
        if (hFind == new IntPtr(-1))
            return alloc;
        try
        {
            do
            {
                if (data.cStreamName != "::$DATA")
                {
                    f.StreamCount++;
                    f.StreamSize += data.StreamSize;
                    if (alloc > 0 || f.LinkCount <= 1)
                        alloc += RoundUp(data.StreamSize);
                }
            }
            while (Win32.FindNextStreamW(hFind, out data));
        }
        finally
        {
            Win32.FindClose(hFind);
        }
        return alloc;
    }
}
