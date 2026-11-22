using ByteAtlas.Analysis;
using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas.Export;

public enum ColumnKind
{
    Text,
    Size,
    Number,
    Percent,
    Date,
}

public sealed record ExportColumn(string Header, ColumnKind Kind, double Width = 0);

public sealed class ExportRow
{
    public int Level { get; init; }
    public bool IsFolder { get; init; }
    public required object?[] Values { get; init; }

    /// <summary>0..1 for the size bar in HTML / PDF.</summary>
    public double? Bar { get; init; }
}

/// <summary>A table to export: list views and the directory tree (with levels) alike.</summary>
public sealed class ExportTable
{
    public string Title { get; set; } = "";
    public List<string> Subtitle { get; } = [];
    public List<ExportColumn> Columns { get; } = [];
    public List<ExportRow> Rows { get; } = [];
    public bool IsTree { get; init; }
}

/// <summary>Columns that can be exported for the directory tree / file lists.</summary>
public enum TreeColumn
{
    Name,
    FullPath,
    Size,
    Allocated,
    PercentOfParent,
    Files,
    Folders,
    Modified,
    Accessed,
    Created,
    Owner,
    Attributes,
    Extension,
    SizeChange,
    Wasted,
    Permissions,
    PermissionInheritance,
    CompressionFormat,
}

public sealed class ExportSettings
{
    public SizeUnit Unit { get; set; } = SizeUnit.Auto;
    public int Decimals { get; set; } = 1;

    /// <summary>Folder levels to export below the start folder (0 = all).</summary>
    public int MaxDepth { get; set; } = 3;

    public bool IncludeFiles { get; set; }

    /// <summary>Leave out items smaller than this (bytes).</summary>
    public long MinSize { get; set; }

    public List<TreeColumn> Columns { get; set; } =
    [
        TreeColumn.Name, TreeColumn.Size, TreeColumn.Allocated, TreeColumn.PercentOfParent, TreeColumn.Files, TreeColumn.Folders, TreeColumn.Modified, TreeColumn.Owner,
    ];

    public string? Title { get; set; }
    public string DateFormat { get; set; } = "";
    public char CsvSeparator { get; set; } = ';';

    /// <summary>Sizes as plain byte numbers in CSV / Excel (handy for further processing).</summary>
    public bool RawNumbers { get; set; }

    public string? ExcelPath { get; set; }

    public ExportSettings Clone()
    {
        var c = (ExportSettings)MemberwiseClone();
        c.Columns = [.. Columns];
        return c;
    }
}

public static class ExportBuilder
{
    public static string ColumnName(TreeColumn c) => c switch
    {
        TreeColumn.FullPath => "Full path",
        TreeColumn.Allocated => "Allocated",
        TreeColumn.PercentOfParent => "% of parent",
        TreeColumn.Modified => "Last modified",
        TreeColumn.Accessed => "Last accessed",
        TreeColumn.Created => "Created",
        TreeColumn.SizeChange => "Size change",
        TreeColumn.Wasted => "Wasted space",
        TreeColumn.PermissionInheritance => "Inherited permissions",
        TreeColumn.CompressionFormat => "Compression",
        _ => c.ToString(),
    };

    private static ColumnKind Kind(TreeColumn c) => c switch
    {
        TreeColumn.Size or TreeColumn.Allocated or TreeColumn.SizeChange or TreeColumn.Wasted => ColumnKind.Size,
        TreeColumn.Files or TreeColumn.Folders => ColumnKind.Number,
        TreeColumn.PercentOfParent => ColumnKind.Percent,
        TreeColumn.Modified or TreeColumn.Accessed or TreeColumn.Created => ColumnKind.Date,
        _ => ColumnKind.Text,
    };

    /// <summary>The directory tree below <paramref name="start"/> as rows with levels.</summary>
    public static ExportTable Tree(ScanSession s, DirNode start, ExportSettings o)
    {
        var t = new ExportTable { IsTree = true, Title = o.Title ?? s.Title ?? $"ByteAtlas report: {start.FullPath}" };
        AddHeader(t, s, start);
        foreach (var c in o.Columns)
            t.Columns.Add(new ExportColumn(ColumnName(c), Kind(c)));
        AddNode(t, s, start, 0, o);
        return t;
    }

    public static void AddHeader(ExportTable t, ScanSession s, DirNode start)
    {
        t.Subtitle.Add($"Path: {start.FullPath}");
        t.Subtitle.Add($"Size: {SizeFormat.Format(start.Size)}  ·  Allocated: {SizeFormat.Format(start.Allocated)}  ·  Files: {SizeFormat.Count(start.FileCount)}  ·  Folders: {SizeFormat.Count(start.FolderCount)}");
        if (s.VolumeTotal > 0)
            t.Subtitle.Add($"Drive: {SizeFormat.Format(s.VolumeTotal)} total, {SizeFormat.Format(s.VolumeFree)} free ({SizeFormat.Percent((double)s.VolumeFree / s.VolumeTotal)})");
        t.Subtitle.Add($"Scanned: {(s.ScanDate == default ? DateTime.Now : s.ScanDate):g}");
        if (s.ComparedWith != null)
            t.Subtitle.Add($"Compared with: {s.ComparedWith}");
    }

    private static void AddNode(ExportTable t, ScanSession s, Item item, int level, ExportSettings o)
    {
        t.Rows.Add(new ExportRow
        {
            Level = level,
            IsFolder = item.IsFolder,
            Values = [.. o.Columns.Select(c => Value(s, item, c))],
            Bar = level == 0 ? 1 : item.PercentOfParent(),
        });
        if (item is not DirNode d || (o.MaxDepth > 0 && level >= o.MaxDepth))
            return;
        foreach (var c in d.Dirs)
            if (c.Size >= o.MinSize)
                AddNode(t, s, c, level + 1, o);
        if (o.IncludeFiles)
        {
            foreach (var f in d.Files)
                if (f.Size >= o.MinSize)
                    AddNode(t, s, f, level + 1, o);
        }
        else if (d.OwnFileCount > 0 && d.OwnFilesSize >= o.MinSize && level + 1 <= (o.MaxDepth == 0 ? int.MaxValue : o.MaxDepth))
        {
            // the files of this folder as one "[Files]" row, as in the tree view
            t.Rows.Add(new ExportRow
            {
                Level = level + 1,
                Values = [.. o.Columns.Select(c => FilesRowValue(d, c))],
                Bar = d.Size <= 0 ? 0 : (double)d.OwnFilesSize / d.Size,
            });
        }
    }

    private static object? FilesRowValue(DirNode d, TreeColumn c) => c switch
    {
        TreeColumn.Name => $"[{d.OwnFileCount} Files]",
        TreeColumn.FullPath => Path.Combine(d.FullPath, "[Files]"),
        TreeColumn.Size => d.OwnFilesSize,
        TreeColumn.Allocated => d.OwnFilesAllocated,
        TreeColumn.PercentOfParent => d.Size <= 0 ? 0.0 : (double)d.OwnFilesSize / d.Size,
        TreeColumn.Files => (long)d.OwnFileCount,
        TreeColumn.Folders => 0L,
        _ => null,
    };

    public static object? Value(ScanSession s, Item i, TreeColumn c) => c switch
    {
        TreeColumn.Name => i.Name,
        TreeColumn.FullPath => i.FullPath,
        TreeColumn.Size => i.Size,
        TreeColumn.Allocated => i.Allocated,
        TreeColumn.PercentOfParent => i.PercentOfParent(),
        TreeColumn.Files => i is DirNode d ? d.FileCount : null,
        TreeColumn.Folders => i is DirNode d2 ? d2.FolderCount : null,
        TreeColumn.Modified => i.Modified,
        TreeColumn.Accessed => i.Accessed,
        TreeColumn.Created => i.Created,
        TreeColumn.Owner => i.OwnerId >= 0 ? s.Owners.Name(i.OwnerId) : "",
        TreeColumn.Attributes => AttributeText(i.Attributes),
        TreeColumn.Extension => i.Extension,
        TreeColumn.SizeChange => i is DirNode d3 && d3.CompareSize != null ? d3.SizeChange : null,
        TreeColumn.Wasted => Math.Max(0, i.Allocated - i.Size),
        TreeColumn.Permissions => OperatingSystem.IsWindows() ? Security.Permissions.Read(i.FullPath).Summary : "",
        TreeColumn.PermissionInheritance => OperatingSystem.IsWindows() ? Security.Permissions.Read(i.FullPath).InheritanceText : "",
        TreeColumn.CompressionFormat => OperatingSystem.IsWindows() && !i.IsFolder ? Native.Compression.Name(Native.Compression.Get(i.FullPath, i.Attributes)) : "",
        _ => null,
    };

    public static string AttributeText(FileAttributes a)
    {
        Span<char> c = stackalloc char[8];
        var n = 0;
        if ((a & FileAttributes.ReadOnly) != 0) c[n++] = 'R';
        if ((a & FileAttributes.Hidden) != 0) c[n++] = 'H';
        if ((a & FileAttributes.System) != 0) c[n++] = 'S';
        if ((a & FileAttributes.Archive) != 0) c[n++] = 'A';
        if ((a & FileAttributes.Compressed) != 0) c[n++] = 'C';
        if ((a & FileAttributes.Encrypted) != 0) c[n++] = 'E';
        if ((a & FileAttributes.SparseFile) != 0) c[n++] = 'P';
        if ((a & FileAttributes.ReparsePoint) != 0) c[n++] = 'L';
        return new string(c[..n]);
    }

    /// <summary>A grouped statistic (extensions, owners, age) as a table.</summary>
    public static ExportTable Stats(string title, string keyHeader, IEnumerable<StatRow> rows, ScanSession s, DirNode branch, bool withDescription = false)
    {
        var t = new ExportTable { Title = title };
        AddHeader(t, s, branch);
        t.Columns.Add(new ExportColumn(keyHeader, ColumnKind.Text));
        if (withDescription)
            t.Columns.Add(new ExportColumn("Description", ColumnKind.Text));
        t.Columns.Add(new ExportColumn("Size", ColumnKind.Size));
        t.Columns.Add(new ExportColumn("Allocated", ColumnKind.Size));
        t.Columns.Add(new ExportColumn("% of size", ColumnKind.Percent));
        t.Columns.Add(new ExportColumn("Files", ColumnKind.Number));
        foreach (var r in rows)
        {
            var v = new List<object?> { r.Key.Length == 0 && withDescription ? "(none)" : r.Key };
            if (withDescription)
                v.Add(r.Description);
            v.AddRange([r.Size, r.Allocated, r.Percent, r.Files]);
            t.Rows.Add(new ExportRow { Values = [.. v], Bar = r.Percent });
        }
        return t;
    }

    /// <summary>A flat list of files / folders (details, top files, search results).</summary>
    public static ExportTable Items(string title, IEnumerable<Item> items, ScanSession? s, IReadOnlyList<TreeColumn> columns, Func<Item, object?[]>? extra = null, IReadOnlyList<ExportColumn>? extraColumns = null)
    {
        var t = new ExportTable { Title = title };
        foreach (var c in columns)
            t.Columns.Add(new ExportColumn(ColumnName(c), Kind(c)));
        if (extraColumns != null)
            t.Columns.AddRange(extraColumns);
        foreach (var i in items)
        {
            var v = columns.Select(c => s == null && c == TreeColumn.Owner ? "" : Value(s ?? EmptySession, i, c)).ToList();
            if (extra != null)
                v.AddRange(extra(i));
            t.Rows.Add(new ExportRow { Values = [.. v], IsFolder = i.IsFolder, Bar = i.PercentOfParent() });
        }
        return t;
    }

    private static readonly ScanSession EmptySession = new("", new ScanOptions());
}
