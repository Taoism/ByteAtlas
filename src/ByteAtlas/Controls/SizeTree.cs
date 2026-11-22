using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas.Controls;

public enum RowKind
{
    Folder,
    FilesGroup,
    File,
}

/// <summary>One visible line of the directory tree.</summary>
public sealed class TreeRow
{
    public required ScanSession Scan { get; init; }
    public required Item Item { get; init; }
    public RowKind Kind { get; init; }
    public int Level { get; set; }
    public bool Expandable { get; set; }
    public bool Expanded { get; set; }
    internal TreeRowView? View;

    public DirNode? Folder => Item as DirNode;

    /// <summary>The folder whose files this "[Files]" row stands for.</summary>
    public DirNode? FilesOf => Kind == RowKind.FilesGroup ? (DirNode)Item : null;

    public void Invalidate() => View?.InvalidateVisual();
}

/// <summary>The left pane: every scan as a root, folders with a size bar, sorted by size.</summary>
public sealed class SizeTree : UserControl
{
    private readonly ListBox _list;
    private readonly AvaloniaList<TreeRow> _rows = [];
    private readonly Dictionary<object, TreeRow> _cache = [];
    private readonly HashSet<object> _expanded = [];

    public SizeTree(AppSettings settings)
    {
        Settings = settings;
        _list = new ListBox
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<TreeRow>((r, _) => new TreeRowView(this) { Row = r }, true),
            Padding = new Thickness(0),
        };
        _list.Classes.Add("sizeTree");
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _list.DoubleTapped += (_, e) =>
        {
            if (Selected is { } r && e.Source is Visual v && v.FindAncestorOfType<TreeRowView>() != null)
                RowActivated?.Invoke(this, r);
        };
        _list.KeyDown += OnKeyDown;
        _list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(MinHeightProperty, 0.0),
            },
        });
        // header: "Name" and the value column, whose width is dragged with the splitter between them
        _valueHeader = new TextBlock { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0), Classes = { "muted" }, FontSize = 11.5 };
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"*,6,{settings.TreeValueWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}"),
            Height = 22,
        };
        header.Children.Add(new TextBlock { Text = "Name", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Classes = { "muted" }, FontSize = 11.5 });
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };
        Grid.SetColumn(split, 1);
        header.Children.Add(split);
        _valueHeader.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Grid.SetColumn(_valueHeader, 2);
        header.Children.Add(_valueHeader);
        split.DragDelta += (_, _) =>
        {
            Settings.TreeValueWidth = Math.Clamp(header.ColumnDefinitions[2].ActualWidth, 50, 400);
            foreach (var r in _rows)
                r.Invalidate();
        };
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.Bind(Border.BorderBrushProperty, headerBorder.GetResourceObservable("EdgeBrush"));
        var dock = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        dock.Children.Add(headerBorder);
        dock.Children.Add(_list);
        Content = dock;
        UpdateHeader();
    }

    private readonly TextBlock _valueHeader;

    private void UpdateHeader() => _valueHeader.Text = (ShowChanges ? "Change  ·  " : "") + Settings.TreeMode switch
    {
        TreeValueMode.Allocated => "Allocated",
        TreeValueMode.Files => "Files",
        TreeValueMode.Percent => "Percent",
        _ => "Size",
    };

    /// <summary>Width of one value column (resizable in the header).</summary>
    public double ValueWidth => Settings.TreeValueWidth;

    public AppSettings Settings { get; }

    public List<ScanSession> Scans { get; } = [];

    /// <summary>Show size changes (after a comparison) instead of the size.</summary>
    public bool ShowChanges { get; set; }

    /// <summary>Filtered totals are shown (extension / owner filter active).</summary>
    public bool Filtered { get; set; }

    public event EventHandler? SelectionChanged;
    public event EventHandler<TreeRow>? RowActivated;

    public new ContextMenu? ContextMenu
    {
        get => _list.ContextMenu;
        set => _list.ContextMenu = value;
    }

    public TreeRow? Selected => _list.SelectedItem as TreeRow;

    public IReadOnlyList<TreeRow> SelectedRows => [.. _list.SelectedItems?.OfType<TreeRow>() ?? []];

    public IReadOnlyList<TreeRow> Rows => _rows;

    public double RowHeight => Settings.CompactRows ? 19 : 23;

    public void AddScan(ScanSession s)
    {
        Scans.Add(s);
        _expanded.Add(s.Root);
        Rebuild();
        Select(s.Root);
    }

    public void RemoveScan(ScanSession s)
    {
        Scans.Remove(s);
        Rebuild();
    }

    /// <summary>The root object changed (MFT scans of a sub folder replace it): keep it expanded.</summary>
    public void RootReplaced(ScanSession s)
    {
        _expanded.Add(s.Root);
        Rebuild();
    }

    public void Select(Item item)
    {
        var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Item, item) && r.Kind != RowKind.FilesGroup);
        if (row != null)
        {
            _list.SelectedItem = row;
            _list.ScrollIntoView(row);
        }
    }

    /// <summary>Expands the folders down to <paramref name="item"/> and selects it.</summary>
    public void Reveal(Item item)
    {
        for (var p = item.Parent; p != null; p = p.Parent)
            _expanded.Add(p);
        Rebuild();
        Select(item);
    }

    public void SetExpanded(TreeRow row, bool expanded)
    {
        var key = RowKey(row);
        if (expanded)
            _expanded.Add(key);
        else
            _expanded.Remove(key);
        Rebuild();
    }

    public void Toggle(TreeRow row)
    {
        if (row.Expandable)
            SetExpanded(row, !row.Expanded);
    }

    /// <summary>Expands everything below <paramref name="start"/> to <paramref name="levels"/> levels (0 = all).</summary>
    public void ExpandToLevel(DirNode start, int levels, long? largerThan = null)
    {
        void Walk(DirNode d, int depth)
        {
            if (levels > 0 && depth >= levels)
                return;
            if (largerThan is { } min && d.Size < min)
                return;
            _expanded.Add(d);
            foreach (var c in d.Dirs)
                Walk(c, depth + 1);
        }
        Walk(start, 0);
        Rebuild();
    }

    public void Collapse(DirNode start)
    {
        foreach (var d in start.AllFolders())
        {
            _expanded.Remove(d);
            _expanded.Remove(FilesKey(d));
        }
        _expanded.Add(start);
        Rebuild();
    }

    private static object RowKey(TreeRow r) => r.Kind == RowKind.FilesGroup ? FilesKey((DirNode)r.Item) : r.Item;

    private static object FilesKey(DirNode d) => (d, 'F');

    /// <summary>Re-reads the trees: new folders found by the scanner, changed sort order, filter, …</summary>
    public void Rebuild()
    {
        var list = new List<TreeRow>(_rows.Count + 16);
        foreach (var s in Scans)
            Add(list, s, s.Root, RowKind.Folder, 0);
        Sync(list);
        UpdateHeader();
        foreach (var r in _rows)
            r.Invalidate();
    }

    private void Add(List<TreeRow> list, ScanSession s, Item item, RowKind kind, int level)
    {
        var key = kind == RowKind.FilesGroup ? FilesKey((DirNode)item) : item;
        if (!_cache.TryGetValue(key, out var row) || !ReferenceEquals(row.Scan, s))
        {
            row = new TreeRow { Scan = s, Item = item, Kind = kind };
            _cache[key] = row;
        }
        row.Level = level;
        list.Add(row);
        switch (kind)
        {
            case RowKind.Folder:
            {
                var d = (DirNode)item;
                var dirs = VisibleDirs(d);
                var hasFiles = Settings.ListFilesInTree && FilesShown(d) > 0;
                row.Expandable = dirs.Count > 0 || hasFiles;
                row.Expanded = row.Expandable && _expanded.Contains(key);
                if (!row.Expanded)
                    return;
                var children = new List<(long Size, string Name, Item Item, RowKind Kind)>();
                foreach (var c in dirs)
                    children.Add((Value(c), c.Name, c, RowKind.Folder));
                if (hasFiles)
                    children.Add((FilesValue(d), "", d, RowKind.FilesGroup));
                if (Settings.SortByName)
                    children.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                else
                    children.Sort((a, b) => b.Size.CompareTo(a.Size));
                foreach (var c in children)
                    Add(list, s, c.Item, c.Kind, level + 1);
                break;
            }
            case RowKind.FilesGroup:
            {
                var d = (DirNode)item;
                row.Expandable = true;
                row.Expanded = _expanded.Contains(key);
                if (!row.Expanded)
                    return;
                IEnumerable<FileItem> files = d.Files.Where(FileShown);
                files = Settings.SortByName ? files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase) : files.OrderByDescending(f => f.Size);
                foreach (var f in files)
                    Add(list, s, f, RowKind.File, level + 1);
                break;
            }
            default:
                row.Expandable = false;
                break;
        }
    }

    public Func<FileItem, bool>? FileFilter { get; set; }

    private bool FileShown(FileItem f) =>
        (FileFilter == null || FileFilter(f)) && (!Settings.HideSmall || f.Size >= Settings.SizeThreshold);

    private int FilesShown(DirNode d)
    {
        if (FileFilter == null && !Settings.HideSmall)
            return d.OwnFileCount;
        return d.Files.Count(FileShown);
    }

    private List<DirNode> VisibleDirs(DirNode d)
    {
        var all = d.Dirs;
        var list = new List<DirNode>(all.Length);
        foreach (var c in all)
        {
            if (Settings.HideSmall && Value(c) < Settings.SizeThreshold && Settings.TreeMode != TreeValueMode.Files)
                continue;
            if ((Filtered || Settings.HideEmptyFolders) && (Filtered ? c.FilteredFiles : c.FileCount) == 0 && !c.Pending)
                continue;
            list.Add(c);
        }
        return list;
    }

    /// <summary>The number the tree sorts and shows by for a folder.</summary>
    public long Value(DirNode d) => Settings.TreeMode switch
    {
        TreeValueMode.Allocated => Filtered ? d.FilteredAllocated : d.Allocated,
        TreeValueMode.Files => Filtered ? d.FilteredFiles : d.FileCount,
        _ => Filtered ? d.FilteredSize : d.Size,
    };

    public long FilesValue(DirNode d)
    {
        if (Filtered || FileFilter != null)
        {
            long v = 0;
            foreach (var f in d.Files)
                if (FileShown(f))
                    v += Settings.TreeMode switch { TreeValueMode.Allocated => f.Allocated, TreeValueMode.Files => 1, _ => f.Size };
            return v;
        }
        return Settings.TreeMode switch
        {
            TreeValueMode.Allocated => d.OwnFilesAllocated,
            TreeValueMode.Files => d.OwnFileCount,
            _ => d.OwnFilesSize,
        };
    }

    public long RowValue(TreeRow r) => r.Kind switch
    {
        RowKind.Folder => Value((DirNode)r.Item),
        RowKind.FilesGroup => FilesValue((DirNode)r.Item),
        _ => Settings.TreeMode switch
        {
            TreeValueMode.Allocated => r.Item.Allocated,
            TreeValueMode.Files => 1,
            _ => r.Item.Size,
        },
    };

    /// <summary>Share of the parent row's value (0..1) for the bar.</summary>
    public double Fraction(TreeRow r)
    {
        DirNode? parent = r.Kind switch
        {
            RowKind.Folder => r.Item.Parent,
            RowKind.FilesGroup => (DirNode)r.Item,
            _ => r.Item.Parent,
        };
        if (parent == null)
            return 1;
        double whole = Value(parent);
        if (r.Kind == RowKind.File)
            whole = FilesValue(parent);
        return whole <= 0 ? 0 : Math.Clamp(RowValue(r) / whole, 0, 1);
    }

    /// <summary>Changes the visible list in place (keeps scroll position and selection).</summary>
    private void Sync(List<TreeRow> target)
    {
        var start = 0;
        while (start < _rows.Count && start < target.Count && ReferenceEquals(_rows[start], target[start]))
            start++;
        if (start == _rows.Count && start == target.Count)
            return;
        var endOld = _rows.Count - 1;
        var endNew = target.Count - 1;
        while (endOld >= start && endNew >= start && ReferenceEquals(_rows[endOld], target[endNew]))
        {
            endOld--;
            endNew--;
        }
        var selected = SelectedRows;
        if (endOld >= start)
            _rows.RemoveRange(start, endOld - start + 1);
        if (endNew >= start)
            _rows.InsertRange(start, target.GetRange(start, endNew - start + 1));
        // the ListBox drops removed items from the selection by itself; put back the ones still shown
        if (selected.Count > 0 && _list.SelectedItems != null && _list.SelectedItems.Count == 0)
            foreach (var r in selected)
                if (_rows.Contains(r))
                    _list.SelectedItems.Add(r);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Selected is not { } r)
            return;
        switch (e.Key)
        {
            case Key.Right when r.Expandable && !r.Expanded:
            case Key.Add:
                SetExpanded(r, true);
                e.Handled = true;
                break;
            case Key.Left when r.Expanded:
            case Key.Subtract:
                SetExpanded(r, false);
                e.Handled = true;
                break;
            case Key.Left when r.Item.Parent != null:
                Select(r.Item.Parent);
                e.Handled = true;
                break;
            case Key.Multiply when r.Folder != null:
                ExpandToLevel(r.Folder, 0);
                e.Handled = true;
                break;
        }
    }
}

/// <summary>Draws one tree row: indent, expander, size bar, icon, name, value.</summary>
public sealed class TreeRowView : Control
{
    private readonly SizeTree _tree;

    public TreeRowView(SizeTree tree)
    {
        _tree = tree;
        Height = tree.RowHeight;
        ClipToBounds = true;
    }

    private TreeRow? _row;

    public TreeRow? Row
    {
        get => _row;
        set
        {
            if (_row?.View == this)
                _row.View = null;
            _row = value;
            if (_row != null)
                _row.View = this;
            InvalidateVisual();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Row = DataContext as TreeRow;
    }

    private const double Indent = 16;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_row == null || !_row.Expandable)
            return;
        var x = e.GetPosition(this).X;
        var gx = 4 + _row.Level * Indent;
        if (x >= gx - 2 && x <= gx + 14)
        {
            _tree.Toggle(_row);
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var r = _row;
        if (r == null)
            return;
        var w = Bounds.Width;
        var h = Bounds.Height;
        var s = _tree.Settings;
        var text = this.ThemeBrush("TextBrush", Brushes.White);
        var muted = this.ThemeBrush("MutedTextBrush", Brushes.Gray);
        var glyph = this.ThemeBrush("GlyphBrush", Brushes.Gray);
        var x = 4 + r.Level * Indent;

        // expander triangle
        if (r.Expandable)
        {
            var cy = h / 2;
            var geo = r.Expanded
                ? new PolylineGeometry([new Point(x + 2, cy - 3), new Point(x + 10, cy - 3), new Point(x + 6, cy + 2)], true)
                : new PolylineGeometry([new Point(x + 4, cy - 5), new Point(x + 9, cy), new Point(x + 4, cy + 5)], true);
            ctx.DrawGeometry(glyph, null, geo);
        }
        x += 15;

        // size bar behind icon and name (share of the parent folder)
        var showChange = _tree.ShowChanges && r.Folder?.CompareSize != null;
        var valueW = showChange ? _tree.ValueWidth * 2 : _tree.ValueWidth;
        var barLeft = x - 1;
        var barRight = w - valueW - 8;
        var frac = _tree.Fraction(r);
        if (s.ShowGradientBars && barRight > barLeft + 4 && frac > 0)
        {
            var full = this.ThemeBrush("SizeBarBrush", Brushes.SteelBlue) is ISolidColorBrush sb ? sb.Color : Colors.SteelBlue;
            var bw = Math.Max(3, (barRight - barLeft) * frac);
            var grad = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(full, 0),
                    new GradientStop(Color.FromArgb((byte)(full.A * 0.45), full.R, full.G, full.B), 1),
                },
            };
            ctx.FillRectangle(grad, new Rect(barLeft, 2, bw, h - 4), 2);
        }

        // icon
        DrawIcon(ctx, r, x, h);
        x += 20;

        // name
        var item = r.Item;
        IBrush nameBrush = text;
        if (s.CompressedBlue && item.IsCompressed)
            nameBrush = this.ThemeBrush("CompressedBrush", Brushes.CornflowerBlue);
        if (r.Folder is { Error: not null } || (r.Folder?.Pending == true && r.Scan.State is ScanState.Scanning or ScanState.Paused))
            nameBrush = muted;
        if (s.LongPathRed && r.Kind == RowKind.File && item.Parent != null && item.Parent.FullPath.Length + item.Name.Length + 1 > s.LongPathLength)
            nameBrush = this.ThemeBrush("LongPathBrush", Brushes.IndianRed);
        var value = _tree.RowValue(r);
        var bold = s.BoldLarge && r.Kind != RowKind.FilesGroup && item.Size >= s.BoldThreshold && s.TreeMode != TreeValueMode.Files;
        var name = r.Kind switch
        {
            RowKind.FilesGroup => $"[{SizeFormat.Count(r.FilesOf!.OwnFileCount)} Files]",
            RowKind.Folder when r.Level == 0 => r.Scan.DisplayName,
            _ => item.Name,
        };
        if (r.Folder?.Error != null)
            name += "  (" + r.Folder.Error + ")";
        var ft = Text(name, nameBrush, bold);
        ft.MaxTextWidth = Math.Max(10, w - valueW - x - 8);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        ctx.DrawText(ft, new Point(x, (h - ft.Height) / 2));

        // value column(s)
        var valueText = s.TreeMode switch
        {
            TreeValueMode.Files => SizeFormat.Count(value),
            TreeValueMode.Percent => SizeFormat.Percent(frac),
            _ => s.Size(value),
        };
        if (r.Scan.State is ScanState.Scanning or ScanState.Paused && r.Folder?.Pending == true)
            valueText = "… " + valueText;
        var vt = Text(valueText, r.Kind == RowKind.File ? muted : text, false);
        ctx.DrawText(vt, new Point(w - (showChange ? _tree.ValueWidth : 0) - 6 - vt.Width, (h - vt.Height) / 2));
        if (showChange)
        {
            var change = r.Folder!.CompareSize == -1 ? r.Folder.Size : r.Folder.SizeChange;
            var label = r.Folder.CompareSize == -1 ? "new" : SizeFormat.FormatChange(change, s.Unit, s.Decimals);
            var brush = change > 0 ? this.ThemeBrush("GrowBrush", Brushes.IndianRed) : change < 0 ? this.ThemeBrush("ShrinkBrush", Brushes.SeaGreen) : muted;
            var ct = Text(label, brush, false);
            ctx.DrawText(ct, new Point(w - 6 - ct.Width, (h - ct.Height) / 2));
        }
    }

    private void DrawIcon(DrawingContext ctx, TreeRow r, double x, double h)
    {
        var cy = h / 2;
        if (r.Kind == RowKind.File)
        {
            var b = this.ThemeBrush("GlyphBrush", Brushes.Gray);
            var pen = new Pen(b, 1.2);
            var geo = new PolylineGeometry([new Point(x + 3, cy - 6), new Point(x + 9, cy - 6), new Point(x + 12, cy - 3), new Point(x + 12, cy + 6), new Point(x + 3, cy + 6)], true);
            ctx.DrawGeometry(null, pen, geo);
            return;
        }
        var folder = r.Kind == RowKind.FilesGroup
            ? this.ThemeBrush("GlyphBrush", Brushes.Gray)
            : r.Level == 0 ? this.ThemeBrush("AccentBrush", Brushes.SteelBlue) : new SolidColorBrush(Color.FromRgb(0xD9, 0xB2, 0x4C));
        if (r.Kind == RowKind.FilesGroup)
        {
            var pen = new Pen(folder, 1.2);
            ctx.DrawRectangle(null, pen, new Rect(x + 2, cy - 5, 8, 10), 1);
            ctx.DrawRectangle(null, pen, new Rect(x + 5, cy - 7, 8, 10), 1);
            return;
        }
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x + 1, cy - 5), true);
            c.LineTo(new Point(x + 6, cy - 5));
            c.LineTo(new Point(x + 8, cy - 3));
            c.LineTo(new Point(x + 15, cy - 3));
            c.LineTo(new Point(x + 15, cy + 6));
            c.LineTo(new Point(x + 1, cy + 6));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(folder, null, g);
    }

    private static readonly Typeface Regular = new(FontFamily.Default);
    private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private static FormattedText Text(string s, IBrush brush, bool bold) =>
        new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Regular, 12.5, brush);
}
