namespace ByteAtlas.Model;

/// <summary>A folder with recursive totals. Children may be added by scanner threads while the UI reads.</summary>
public sealed class DirNode : Item
{
    private readonly List<DirNode> _dirs = [];
    private readonly List<FileItem> _files = [];
    private readonly object _gate = new();

    public DirNode(string name, DirNode? parent) : base(name, parent) { }

    public override bool IsFolder => true;

    internal long _fileCount;
    internal long _folderCount;

    /// <summary>Files below this folder, recursively.</summary>
    public long FileCount => _fileCount;

    /// <summary>Folders below this folder, recursively (not counting itself).</summary>
    public long FolderCount => _folderCount;

    /// <summary>Size of the files directly in this folder.</summary>
    public long OwnFilesSize { get; internal set; }
    public long OwnFilesAllocated { get; internal set; }

    /// <summary>Set when the folder could not be read completely.</summary>
    public string? Error { get; internal set; }

    /// <summary>True while (or until) the scanner is still working on something below.</summary>
    public bool Pending { get; internal set; } = true;

    /// <summary>Deepest level of any folder below (0 = no sub folders).</summary>
    public int MaxDepthBelow { get; internal set; }

    // ----- comparison with an older scan -----

    /// <summary>Size of the same folder in the compared scan; null when nothing compared, -1 when new.</summary>
    public long? CompareSize { get; set; }
    public long? CompareFiles { get; set; }
    public long SizeChange => CompareSize is { } c ? Size - Math.Max(0, c) : 0;

    // ----- view filter (extension / user filter, hide empty folders) -----

    internal long _fSize, _fAlloc, _fFiles;
    public long FilteredSize => _fSize;
    public long FilteredAllocated => _fAlloc;
    public long FilteredFiles => _fFiles;

    public int DirCount
    {
        get
        {
            lock (_gate)
                return _dirs.Count;
        }
    }

    public int OwnFileCount
    {
        get
        {
            lock (_gate)
                return _files.Count;
        }
    }

    /// <summary>A copy of the sub folders (safe while scanning).</summary>
    public DirNode[] Dirs
    {
        get
        {
            lock (_gate)
                return [.. _dirs];
        }
    }

    /// <summary>A copy of the files directly in this folder (safe while scanning).</summary>
    public FileItem[] Files
    {
        get
        {
            lock (_gate)
                return [.. _files];
        }
    }

    /// <summary>Sub folders followed by files.</summary>
    public Item[] Children
    {
        get
        {
            lock (_gate)
            {
                var a = new Item[_dirs.Count + _files.Count];
                for (var i = 0; i < _dirs.Count; i++)
                    a[i] = _dirs[i];
                for (var i = 0; i < _files.Count; i++)
                    a[_dirs.Count + i] = _files[i];
                return a;
            }
        }
    }

    /// <summary>Direct access for single-threaded passes after the scan (no copy).</summary>
    internal List<DirNode> DirList => _dirs;
    internal List<FileItem> FileList => _files;

    internal void AddDir(DirNode d)
    {
        lock (_gate)
            _dirs.Add(d);
    }

    internal void AddFiles(List<FileItem> files)
    {
        lock (_gate)
            _files.AddRange(files);
    }

    internal void AddFile(FileItem f)
    {
        lock (_gate)
            _files.Add(f);
    }

    internal void RemoveChild(Item item)
    {
        lock (_gate)
        {
            if (item is DirNode d)
                _dirs.Remove(d);
            else if (item is FileItem f)
                _files.Remove(f);
        }
    }

    internal void ClearChildren()
    {
        lock (_gate)
        {
            _dirs.Clear();
            _files.Clear();
        }
    }

    /// <summary>Adds to this folder and every folder above (thread safe).</summary>
    internal void AddTotals(long size, long alloc, long files, long folders)
    {
        for (var n = this; n != null; n = n.Parent)
        {
            Interlocked.Add(ref n._size, size);
            Interlocked.Add(ref n._allocated, alloc);
            Interlocked.Add(ref n._fileCount, files);
            Interlocked.Add(ref n._folderCount, folders);
        }
    }

    /// <summary>Recomputes totals, dates and depths bottom-up (single threaded, after a scan or an edit).</summary>
    public void Recalculate(bool folderDateFromFilesOnly = true) =>
        Recalculate(folderDateFromFilesOnly, DateTime.UtcNow.AddDays(1).Ticks);

    /// <summary>Dates after <paramref name="future"/> (bad clocks, wrong time stamps) do not count for folder dates.</summary>
    private void Recalculate(bool folderDateFromFilesOnly, long future)
    {
        long size = 0, alloc = 0, files = 0, folders = 0, mod = 0, acc = 0;
        var depth = 0;
        long ownSize = 0, ownAlloc = 0;
        foreach (var f in _files)
        {
            ownSize += f.Size;
            ownAlloc += f.Allocated;
            if (f.ModifiedTicks <= future)
                mod = Math.Max(mod, f.ModifiedTicks);
            if (f.AccessedTicks <= future)
                acc = Math.Max(acc, f.AccessedTicks);
        }
        size += ownSize;
        alloc += ownAlloc;
        files += _files.Count;
        foreach (var d in _dirs)
        {
            d.Recalculate(folderDateFromFilesOnly, future);
            size += d.Size;
            alloc += d.Allocated;
            files += d.FileCount;
            folders += d.FolderCount + 1;
            mod = Math.Max(mod, d._aggModified);
            acc = Math.Max(acc, d._aggAccessed);
            depth = Math.Max(depth, d.MaxDepthBelow + 1);
        }
        _size = size;
        _allocated = alloc;
        _fileCount = files;
        _folderCount = folders;
        OwnFilesSize = ownSize;
        OwnFilesAllocated = ownAlloc;
        MaxDepthBelow = depth;
        if (!folderDateFromFilesOnly)
        {
            if (_ownModified <= future)
                mod = Math.Max(mod, _ownModified);
            if (_ownAccessed <= future)
                acc = Math.Max(acc, _ownAccessed);
        }
        _aggModified = mod == 0 ? _ownModified : mod;
        _aggAccessed = acc == 0 ? _ownAccessed : acc;
        ModifiedTicks = _aggModified;
        AccessedTicks = _aggAccessed;
    }

    // the folder's own dates (as reported by the file system) and the aggregated ones
    internal long _ownModified, _ownAccessed, _aggModified, _aggAccessed;

    internal void SetOwnDates(long created, long modified, long accessed)
    {
        CreatedTicks = created;
        _ownModified = modified;
        _ownAccessed = accessed;
        ModifiedTicks = modified;
        AccessedTicks = accessed;
    }

    /// <summary>Sorts children by size (descending) or name.</summary>
    public void Sort(bool bySize, bool recursive = true)
    {
        lock (_gate)
        {
            if (bySize)
            {
                _dirs.Sort((a, b) => b.Size.CompareTo(a.Size));
                _files.Sort((a, b) => b.Size.CompareTo(a.Size));
            }
            else
            {
                _dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                _files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
        }
        if (recursive)
            foreach (var d in Dirs)
                d.Sort(bySize, true);
    }

    /// <summary>Finds a folder by a path relative to this one ("a\b\c"), case-insensitive.</summary>
    public DirNode? FindRelative(string relative)
    {
        var node = this;
        foreach (var part in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            DirNode? next = null;
            foreach (var d in node.Dirs)
                if (string.Equals(d.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    next = d;
                    break;
                }
            if (next == null)
                return null;
            node = next;
        }
        return node;
    }

    /// <summary>All folders below (depth first, including this one).</summary>
    public IEnumerable<DirNode> AllFolders()
    {
        var stack = new Stack<DirNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            yield return d;
            foreach (var c in d.Dirs)
                stack.Push(c);
        }
    }

    /// <summary>All files below (recursive).</summary>
    public IEnumerable<FileItem> AllFiles()
    {
        foreach (var d in AllFolders())
            foreach (var f in d.Files)
                yield return f;
    }
}
