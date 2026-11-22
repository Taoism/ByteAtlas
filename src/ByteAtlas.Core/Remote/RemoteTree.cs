using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas.Remote;

/// <summary>Builds a scan tree from a flat listing ("a/b/c.txt" with size and dates).</summary>
internal sealed class RemoteTree
{
    private readonly ScanSession _s;
    private readonly Dictionary<string, DirNode> _dirs = new(StringComparer.Ordinal);
    private readonly char _sep;
    private long _files;

    public RemoteTree(ScanSession s, char separator = '/')
    {
        _s = s;
        _sep = separator;
        _dirs[""] = s.Root;
    }

    public DirNode Folder(string relative)
    {
        relative = relative.Trim(_sep);
        if (_dirs.TryGetValue(relative, out var d))
            return d;
        var cut = relative.LastIndexOf(_sep);
        var parent = cut < 0 ? _s.Root : Folder(relative[..cut]);
        var name = cut < 0 ? relative : relative[(cut + 1)..];
        d = new DirNode(name, parent) { Pending = false, Attributes = FileAttributes.Directory };
        parent.DirList.Add(d);
        _dirs[relative] = d;
        Interlocked.Increment(ref _s._foldersDone);
        return d;
    }

    public void FolderDates(string relative, DateTime modified)
    {
        var d = Folder(relative);
        var t = modified == default ? 0 : modified.ToUniversalTime().Ticks;
        d.SetOwnDates(t, t, t);
    }

    public void File(string relative, long size, DateTime modified, long allocated = -1, DateTime accessed = default, string? owner = null)
    {
        relative = relative.Trim(_sep);
        var cut = relative.LastIndexOf(_sep);
        var dir = cut < 0 ? _s.Root : Folder(relative[..cut]);
        var f = new FileItem(cut < 0 ? relative : relative[(cut + 1)..], dir)
        {
            ModifiedTicks = modified == default ? 0 : modified.ToUniversalTime().Ticks,
            AccessedTicks = accessed == default ? (modified == default ? 0 : modified.ToUniversalTime().Ticks) : accessed.ToUniversalTime().Ticks,
            CreatedTicks = modified == default ? 0 : modified.ToUniversalTime().Ticks,
            Attributes = FileAttributes.Normal,
            LinkCount = 1,
        };
        if (owner != null)
            f.OwnerId = _s.Owners.GetId(owner);
        f.SetSizes(size, allocated < 0 ? size : allocated);
        dir.FileList.Add(f);
        _s._filesDone = ++_files;
        if ((_files & 1023) == 0)
            _s.CurrentPath = relative;
    }
}
