namespace ByteAtlas.Model;

/// <summary>A file or folder in a scan. Folders carry recursive totals, files their own values.</summary>
public abstract class Item
{
    protected Item(string name, DirNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    public string Name { get; internal set; }
    public DirNode? Parent { get; internal set; }

    /// <summary>Logical size in bytes (folders: sum of everything below).</summary>
    public long Size => _size;

    /// <summary>Space taken on disk (cluster rounded, compressed / sparse aware).</summary>
    public long Allocated => _allocated;

    internal long _size;
    internal long _allocated;

    public long CreatedTicks { get; internal set; }
    public long ModifiedTicks { get; internal set; }
    public long AccessedTicks { get; internal set; }
    public FileAttributes Attributes { get; internal set; }

    /// <summary>Index into <see cref="ScanSession.Owners"/>, -1 when not collected.</summary>
    public int OwnerId { get; internal set; } = -1;

    public abstract bool IsFolder { get; }

    public DateTime Created => Ticks(CreatedTicks);
    public DateTime Modified => Ticks(ModifiedTicks);
    public DateTime Accessed => Ticks(AccessedTicks);

    private static DateTime Ticks(long t) => t <= 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc).ToLocalTime();

    public bool IsCompressed => (Attributes & FileAttributes.Compressed) != 0;
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Extension in lower case without the dot ("" for none).</summary>
    public string Extension
    {
        get
        {
            if (IsFolder)
                return "";
            var i = Name.LastIndexOf('.');
            return i <= 0 || i == Name.Length - 1 ? "" : Name[(i + 1)..].ToLowerInvariant();
        }
    }

    public string FullPath
    {
        get
        {
            if (Parent == null)
                return Name;
            var parts = new List<string>();
            for (Item? i = this; i != null; i = i.Parent)
                parts.Add(i.Name);
            parts.Reverse();
            var root = parts[0];
            var sb = new System.Text.StringBuilder(root);
            for (var k = 1; k < parts.Count; k++)
            {
                if (sb.Length > 0 && sb[^1] != '\\' && sb[^1] != '/')
                    sb.Append(Path.DirectorySeparatorChar);
                sb.Append(parts[k]);
            }
            return sb.ToString();
        }
    }

    /// <summary>Depth below the scan root (root = 0).</summary>
    public int Level
    {
        get
        {
            var l = 0;
            for (var p = Parent; p != null; p = p.Parent)
                l++;
            return l;
        }
    }

    /// <summary>Share of the parent's size, 0..1.</summary>
    public double PercentOfParent(bool allocated = false)
    {
        if (Parent == null)
            return 1;
        var whole = allocated ? Parent.Allocated : Parent.Size;
        var part = allocated ? Allocated : Size;
        return whole <= 0 ? 0 : (double)part / whole;
    }

    public override string ToString() => Name;
}

public sealed class FileItem : Item
{
    public FileItem(string name, DirNode? parent) : base(name, parent) { }

    public override bool IsFolder => false;

    /// <summary>Number of hard links to this file (0 = unknown).</summary>
    public int LinkCount { get; internal set; }

    /// <summary>Number of named alternate data streams (only counted when tracking is on).</summary>
    public int StreamCount { get; internal set; }

    /// <summary>Bytes in alternate data streams.</summary>
    public long StreamSize { get; internal set; }

    internal void SetSizes(long size, long allocated)
    {
        _size = size;
        _allocated = allocated;
    }
}
