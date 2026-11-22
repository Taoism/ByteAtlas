using System.Collections.Concurrent;
using ByteAtlas.Model;

namespace ByteAtlas.Scanning;

public enum ScanState
{
    Waiting,
    Scanning,
    Paused,
    Done,
    Cancelled,
    Failed,
}

public sealed record ScanError(string Path, string Message);

/// <summary>Owner (user / group) names seen during a scan, by index.</summary>
public sealed class OwnerTable
{
    private readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _names = [];
    private readonly object _gate = new();

    public int GetId(string name)
    {
        if (_ids.TryGetValue(name, out var id))
            return id;
        lock (_gate)
        {
            if (_ids.TryGetValue(name, out id))
                return id;
            id = _names.Count;
            _names.Add(name);
            _ids[name] = id;
            return id;
        }
    }

    public string Name(int id)
    {
        lock (_gate)
            return id >= 0 && id < _names.Count ? _names[id] : "";
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _names.Count;
        }
    }

    /// <summary>Replaces the table with another one (ids stay those of <paramref name="other"/>).</summary>
    internal void CopyFrom(OwnerTable other)
    {
        lock (_gate)
        {
            _ids.Clear();
            _names.Clear();
            for (var i = 0; i < other.Count; i++)
            {
                var n = other.Name(i);
                _names.Add(n);
                _ids[n] = i;
            }
        }
    }
}

/// <summary>One scan in the window: its root, state, progress and errors.</summary>
public sealed class ScanSession
{
    public ScanSession(string path, ScanOptions options)
    {
        Path = path;
        Options = options;
        Root = new DirNode(NormalizeRoot(path), null);
    }

    public string Path { get; }
    public ScanOptions Options { get; }
    public DirNode Root { get; internal set; }
    public OwnerTable Owners { get; } = new();

    public string? Title { get; set; }
    public string DisplayName => string.IsNullOrEmpty(Title) ? Path : $"{Title} ({Path})";

    public ScanState State { get; internal set; } = ScanState.Waiting;

    /// <summary>"MFT" or "File system" (and thread count) — shown in the overview.</summary>
    public string MethodUsed { get; internal set; } = "";

    public DateTime Started { get; internal set; }
    public DateTime Finished { get; internal set; }
    public TimeSpan Duration => (State is ScanState.Scanning or ScanState.Paused ? DateTime.Now : Finished) - Started;

    /// <summary>When the data was originally collected (differs from Started for loaded index files).</summary>
    public DateTime ScanDate { get; internal set; }

    public long FoldersDone => Interlocked.Read(ref _foldersDone);
    public long FilesDone => Interlocked.Read(ref _filesDone);
    internal long _foldersDone, _filesDone;

    public string CurrentPath { get; internal set; } = "";

    /// <summary>How far the scan is, 0..1, when the scanner can tell (master file table); -1 otherwise.</summary>
    public double Fraction { get; internal set; } = -1;

    /// <summary>Where the scan time went (diagnostics, shown by the CLI with --verbose).</summary>
    public string Timings { get; internal set; } = "";

    public ConcurrentQueue<ScanError> Errors { get; } = new();

    /// <summary>Volume information when the root is a drive or a folder on one.</summary>
    public long VolumeTotal { get; internal set; }
    public long VolumeFree { get; internal set; }
    public int ClusterSize { get; internal set; } = 4096;
    public string FileSystem { get; internal set; } = "";

    /// <summary>A background scan replacing this one (progress is read from it); null otherwise.</summary>
    public ScanSession? Progress { get; internal set; }

    /// <summary>Takes the result of a background scan of the same path.</summary>
    internal void TakeOver(ScanSession fresh)
    {
        Owners.CopyFrom(fresh.Owners);
        Root = fresh.Root;
        Usn = fresh.Usn;
        MethodUsed = fresh.MethodUsed;
        Timings = fresh.Timings;
        VolumeTotal = fresh.VolumeTotal;
        VolumeFree = fresh.VolumeFree;
        ClusterSize = fresh.ClusterSize;
        FileSystem = fresh.FileSystem;
        ScanDate = fresh.ScanDate;
        Started = fresh.Started;
        Finished = fresh.Finished;
        _filesDone = fresh._filesDone;
        _foldersDone = fresh._foldersDone;
        while (Errors.TryDequeue(out _))
        {
        }
        foreach (var e in fresh.Errors)
            Errors.Enqueue(e);
        State = fresh.State;
    }

    /// <summary>Change-journal position when the drive was read (NTFS): lets a refresh read only what changed since.</summary>
    public Native.UsnPosition? Usn { get; internal set; }

    /// <summary>Set when the scan was loaded from an index file.</summary>
    public string? IndexFile { get; internal set; }

    /// <summary>True for the virtual "All scans" root (see ScanGroup).</summary>
    public bool IsGroup { get; internal set; }

    /// <summary>The scans inside a group.</summary>
    public IReadOnlyList<ScanSession> Members { get; internal set; } = [];

    /// <summary>Description of the compared scan when a comparison is active.</summary>
    public string? ComparedWith { get; set; }

    internal readonly ManualResetEventSlim PauseGate = new(true);

    public void Pause()
    {
        if (State == ScanState.Scanning)
        {
            PauseGate.Reset();
            State = ScanState.Paused;
        }
    }

    public void Resume()
    {
        if (State == ScanState.Paused)
        {
            State = ScanState.Scanning;
            PauseGate.Set();
        }
    }

    internal void AddError(string path, string message)
    {
        if (Errors.Count < 100_000)
            Errors.Enqueue(new ScanError(path, message));
    }

    public static string NormalizeRoot(string path)
    {
        var p = path.Trim();
        if (p.Length == 2 && p[1] == ':')
            p += "\\";
        if (p.Length > 3 && (p.EndsWith('\\') || p.EndsWith('/')) && !p.EndsWith(":\\", StringComparison.Ordinal))
            p = p.TrimEnd('\\', '/');
        return p;
    }

    /// <summary>A session around an already built tree (index files, tests).</summary>
    internal static ScanSession FromTree(string path, DirNode root, ScanOptions? options = null)
    {
        var s = new ScanSession(path, options ?? new ScanOptions()) { Root = root, State = ScanState.Done };
        root.Pending = false;
        return s;
    }
}
