using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ByteAtlas.Analysis;
using ByteAtlas.Export;
using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas.Views;

/// <summary>A file or folder as a grid row (details, top files, search results).</summary>
public sealed class ItemRow(Item item, ScanSession? scan, AppSettings s, string? fullPath = null)
{
    public Item Item { get; } = item;
    public ScanSession? Scan { get; } = scan;
    private readonly string? _path = fullPath;

    public string Name => Item.Name;
    public string Path => _path ?? Item.FullPath;
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public long Size => Item.Size;
    public string SizeText => s.Size(Item.Size);
    public long Allocated => Item.Allocated;
    public string AllocatedText => s.Size(Item.Allocated);
    public double Percent => Item.PercentOfParent();
    public string PercentText => SizeFormat.Percent(Percent);
    public long Files => Item is DirNode d ? d.FileCount : 0;
    public string FilesText => Item is DirNode d ? SizeFormat.Count(d.FileCount) : "";
    public long Folders => Item is DirNode d ? d.FolderCount : 0;
    public string FoldersText => Item is DirNode d ? SizeFormat.Count(d.FolderCount) : "";
    public DateTime Modified => Item.Modified;
    public string ModifiedText => SizeFormat.Date(Item.Modified, s.DateFormat);
    public DateTime Accessed => Item.Accessed;
    public string AccessedText => SizeFormat.Date(Item.Accessed, s.DateFormat);
    public DateTime Created => Item.Created;
    public string CreatedText => SizeFormat.Date(Item.Created, s.DateFormat);
    public string Owner => Item.OwnerId >= 0 && Scan != null ? Scan.Owners.Name(Item.OwnerId) : "";
    public string Attributes => ExportBuilder.AttributeText(Item.Attributes);
    public string Type => Item.IsFolder ? "Folder" : Item.Extension.Length == 0 ? "File" : Item.Extension.ToUpperInvariant() + " file";
    public string Extension => Item.Extension;
    public long Wasted => Math.Max(0, Item.Allocated - Item.Size);
    public string WastedText => s.Size(Wasted);
    public double Compression => Item.IsFolder || Item.Size <= 0 || !Item.IsCompressed ? 0 : 1 - (double)Item.Allocated / Item.Size;
    public string CompressionText => Compression <= 0 ? "" : SizeFormat.Percent(Compression);
    public long Change => Item is DirNode { CompareSize: not null } d ? (d.CompareSize == -1 ? d.Size : d.SizeChange) : 0;
    public string ChangeText => Item is DirNode { CompareSize: not null } d ? (d.CompareSize == -1 ? "new" : SizeFormat.FormatChange(d.SizeChange, s.Unit, s.Decimals)) : "";
    public int Links => Item is FileItem f ? f.LinkCount : 0;
    public int Streams => Item is FileItem f ? f.StreamCount : 0;
    public string Costs => s.CostPerGb > 0 ? (Item.Allocated / (double)(1L << 30) * s.CostPerGb).ToString("N2", System.Globalization.CultureInfo.CurrentCulture) + " " + s.Currency : "";
    public long PathLength => Path.Length;

    // read lazily: only rows that are shown ask the file system
    private Security.PermissionInfo? _perm;
    private Security.PermissionInfo? Perm => OperatingSystem.IsWindows() ? _perm ??= Security.Permissions.Read(Path) : null;
    public string Permissions => OperatingSystem.IsWindows() ? Perm?.Summary ?? "" : "";
    public string PermissionInheritance => OperatingSystem.IsWindows() ? Perm?.InheritanceText ?? "" : "";

    private string? _format;
    public string Format => _format ??= OperatingSystem.IsWindows() && Item is FileItem
        ? Native.Compression.Name(Native.Compression.Get(Path, Item.Attributes))
        : "";

    // search extras
    public int Group { get; init; }
    public string Hash { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Checked { get; set; }

    /// <summary>Duplicate tabs: the heading of the row's group.</summary>
    public string GroupTitle { get; init; } = "";

    /// <summary>Values of extra columns (per extension / owner / age interval), filled by MainWindow.</summary>
    public Dictionary<string, long> Extra { get; } = [];
}

/// <summary>A grouped statistic row (extension, owner, age interval).</summary>
public sealed class StatItem(StatRow r, AppSettings s, Color color)
{
    public StatRow Row { get; } = r;
    public string Key => Row.Key.Length == 0 ? "(none)" : Row.Key;
    public string Description => Row.Description;
    public long Size => Row.Size;
    public string SizeText => s.Size(Row.Size);
    public long Allocated => Row.Allocated;
    public string AllocatedText => s.Size(Row.Allocated);
    public double Percent => Row.Percent;
    public string PercentText => SizeFormat.Percent(Row.Percent);
    public long Files => Row.Files;
    public string FilesText => SizeFormat.Count(Row.Files);
    public IBrush Swatch { get; } = new SolidColorBrush(color);
}

/// <summary>Column definition for <see cref="GridFactory"/>.</summary>
public sealed record GridColumn(string Header, string Text, string? Sort = null, double Width = 110, bool Right = false, bool Bar = false, bool Swatch = false);

public static class GridFactory
{
    /// <summary>All columns the details / file lists can show, by name.</summary>
    public static readonly Dictionary<string, GridColumn> ItemColumns = new()
    {
        ["Name"] = new("Name", nameof(ItemRow.Name), Width: 260),
        ["Size"] = new("Size", nameof(ItemRow.SizeText), nameof(ItemRow.Size), 100, true),
        ["Allocated"] = new("Allocated", nameof(ItemRow.AllocatedText), nameof(ItemRow.Allocated), 100, true),
        ["Percent"] = new("% of parent", nameof(ItemRow.PercentText), nameof(ItemRow.Percent), 130, true, Bar: true),
        ["Files"] = new("Files", nameof(ItemRow.FilesText), nameof(ItemRow.Files), 80, true),
        ["Folders"] = new("Folders", nameof(ItemRow.FoldersText), nameof(ItemRow.Folders), 72, true),
        ["Modified"] = new("Last modified", nameof(ItemRow.ModifiedText), nameof(ItemRow.Modified), 135),
        ["Accessed"] = new("Last accessed", nameof(ItemRow.AccessedText), nameof(ItemRow.Accessed), 135),
        ["Created"] = new("Created", nameof(ItemRow.CreatedText), nameof(ItemRow.Created), 135),
        ["Owner"] = new("Owner", nameof(ItemRow.Owner), Width: 170),
        ["Attributes"] = new("Attributes", nameof(ItemRow.Attributes), Width: 76),
        ["Type"] = new("Type", nameof(ItemRow.Type), Width: 100),
        ["Path"] = new("Path", nameof(ItemRow.Path), Width: 320),
        ["Folder"] = new("Folder", nameof(ItemRow.Folder), Width: 300),
        ["Wasted"] = new("Wasted space", nameof(ItemRow.WastedText), nameof(ItemRow.Wasted), 100, true),
        ["Compression"] = new("Compression", nameof(ItemRow.CompressionText), nameof(ItemRow.Compression), 90, true),
        ["Change"] = new("Size change", nameof(ItemRow.ChangeText), nameof(ItemRow.Change), 100, true),
        ["Links"] = new("Hard links", nameof(ItemRow.Links), nameof(ItemRow.Links), 76, true),
        ["Streams"] = new("Streams", nameof(ItemRow.Streams), nameof(ItemRow.Streams), 70, true),
        ["Costs"] = new("Costs", nameof(ItemRow.Costs), nameof(ItemRow.Allocated), 90, true),
        ["PathLength"] = new("Path length", nameof(ItemRow.PathLength), nameof(ItemRow.PathLength), 80, true),
        ["Group"] = new("Group", nameof(ItemRow.Group), nameof(ItemRow.Group), 60, true),
        ["Hash"] = new("Checksum", nameof(ItemRow.Hash), Width: 260),
        ["Permissions"] = new("Permissions", nameof(ItemRow.Permissions), Width: 320),
        ["Inherited"] = new("Inherited", nameof(ItemRow.PermissionInheritance), Width: 140),
        ["Format"] = new("Compression format", nameof(ItemRow.Format), Width: 120),
        ["Detail"] = new("Access", nameof(ItemRow.Detail), Width: 320),
    };

    public static readonly GridColumn[] StatColumns =
    [
        new("", "", Width: 22, Swatch: true),
        new("Name", nameof(StatItem.Key), Width: 160),
        new("Description", nameof(StatItem.Description), Width: 160),
        new("Size", nameof(StatItem.SizeText), nameof(StatItem.Size), 100, true),
        new("Allocated", nameof(StatItem.AllocatedText), nameof(StatItem.Allocated), 100, true),
        new("% of size", nameof(StatItem.PercentText), nameof(StatItem.Percent), 140, true, Bar: true),
        new("Files", nameof(StatItem.FilesText), nameof(StatItem.Files), 90, true),
    ];

    public static DataGrid Create(IEnumerable<GridColumn> columns)
    {
        var g = new DataGrid
        {
            IsReadOnly = true,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            AutoGenerateColumns = false,
            RowHeight = 24,
        };
        foreach (var c in columns)
            g.Columns.Add(Column(c));
        return g;
    }

    public static DataGridColumn Column(GridColumn c)
    {
        if (c.Swatch)
        {
            return new DataGridTemplateColumn
            {
                Header = c.Header,
                Width = new DataGridLength(c.Width),
                CanUserSort = false,
                CellTemplate = new FuncDataTemplate<StatItem>((r, _) => new Border
                {
                    Width = 12,
                    Height = 12,
                    CornerRadius = new CornerRadius(2),
                    Background = r?.Swatch,
                    HorizontalAlignment = HorizontalAlignment.Center,
                }),
            };
        }
        if (c.Bar)
        {
            return new DataGridTemplateColumn
            {
                Header = c.Header,
                Width = new DataGridLength(c.Width),
                SortMemberPath = c.Sort ?? c.Text,
                CellTemplate = new FuncDataTemplate<object>((r, _) => new PercentBar
                {
                    Value = r switch { ItemRow ir => ir.Percent, StatItem si => si.Percent, _ => 0 },
                }),
            };
        }
        var col = new DataGridTextColumn
        {
            Header = c.Header,
            Binding = new Binding(c.Text),
            SortMemberPath = c.Sort ?? c.Text,
            Width = new DataGridLength(c.Width),
        };
        if (c.Right)
            col.CellStyleClasses.Add("right");
        return col;
    }
}

/// <summary>A small percentage bar with the number on it.</summary>
public sealed class PercentBar : Control
{
    public double Value { get; set; }

    public PercentBar()
    {
        Height = 18;
        Margin = new Thickness(6, 0);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(this.ThemeBrush("SizeBarBrush", Brushes.SteelBlue), new Rect(0, 3, w, h - 6), 2);
        ctx.FillRectangle(this.ThemeBrush("SizeBarFullBrush", Brushes.SteelBlue), new Rect(0, 3, Math.Max(0, w * Math.Clamp(Value, 0, 1)), h - 6), 2);
        var t = new FormattedText(SizeFormat.Percent(Value), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 11, this.ThemeBrush("TextBrush", Brushes.White));
        ctx.DrawText(t, new Point(w - t.Width - 4, (h - t.Height) / 2));
    }
}
