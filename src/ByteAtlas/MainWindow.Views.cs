using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ByteAtlas.Analysis;
using ByteAtlas.Controls;
using ByteAtlas.Model;
using ByteAtlas.Scanning;
using ByteAtlas.Storage;
using Avalonia.Controls.Primitives;
using ByteAtlas.Views;

namespace ByteAtlas;

public sealed partial class MainWindow
{
    // overview
    private readonly WrapPanel _overview = new() { Orientation = Orientation.Horizontal };

    // chart tab
    private readonly PieChart _pie = new();
    private readonly BarChart _bars = new();
    private readonly TreemapChart _treemap = new();
    private string _chartSource = "Folders";

    // details
    private readonly DataGrid _details;

    // statistics
    private readonly DataGrid _extGrid = GridFactory.Create(GridFactory.StatColumns);
    private readonly BarChart _extChart = new() { MaxBars = 15 };
    private readonly DataGrid _userGrid = GridFactory.Create(GridFactory.StatColumns.Where(c => c.Header != "Description"));
    private readonly BarChart _userChart = new() { MaxBars = 15 };
    private readonly DataGrid _ageGrid = GridFactory.Create(GridFactory.StatColumns.Where(c => c.Header != "Description"));
    private readonly BarChart _ageChart = new() { KeepOrder = true };
    private readonly DataGrid _topGrid;
    private readonly LineChart _historyChart = new();
    private readonly TextBlock _historyInfo = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };

    private object? _shownFor;
    private int _shownTab = -1;
    private CancellationTokenSource? _statsCts;
    private DateTime _lastLive;

    private ViewFilter? _filter;

    /// <summary>Puts the keyboard focus into the shown tab (the chart, the list, …).</summary>
    private void FocusTab()
    {
        Control? target = _tabs.SelectedIndex switch
        {
            0 => _treemap.IsVisible ? _treemap : _pie.IsVisible ? _pie : _bars,
            1 => _settings.DetailsThumbnails ? _thumbs?.List : _details,
            2 => _extGrid,
            3 => _userGrid,
            4 => _ageGrid,
            5 => _topGrid,
            DupFilesTab => _dupFiles?.Grid,
            DupFoldersTab => _dupFolders?.Grid,
            _ => _historyChart,
        };
        target?.Focus();
    }

    private void BuildTabs()
    {
        _tabs.Items.Add(new TabItem { Header = "Chart", Content = ChartTab() });
        _tabs.Items.Add(new TabItem { Header = "Details", Content = DetailsTab() });
        _tabs.Items.Add(new TabItem { Header = "Extensions", Content = ExtensionsTab() });
        _tabs.Items.Add(new TabItem { Header = "Users", Content = UsersTab() });
        _tabs.Items.Add(new TabItem { Header = "Age of Files", Content = AgeTab() });
        _tabs.Items.Add(new TabItem { Header = "Top Files", Content = TopTab() });
        _tabs.Items.Add(new TabItem { Header = "Duplicate Files", Content = DuplicateTab(folders: false) });
        _tabs.Items.Add(new TabItem { Header = "Duplicate Folders", Content = DuplicateTab(folders: true) });
        _tabs.Items.Add(new TabItem { Header = "History", Content = HistoryTab() });
        var idx = _tabs.Items.OfType<TabItem>().ToList().FindIndex(t => (t.Header as string) == _settings.LastTab);
        _tabs.SelectedIndex = Math.Max(0, idx);
        _tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, _tabs))
                UpdateViews(force: true);
        };
    }

    // wrap the tab content with the overview strip on top
    private static DockPanel WithOverview(Control content, Control? toolbar = null)
    {
        var dock = new DockPanel();
        var over = new Border { Classes = { "card" }, Margin = new Thickness(0, 0, 0, 6), Child = new ContentControl() };
        DockPanel.SetDock(over, Dock.Top);
        dock.Children.Add(over);
        if (toolbar != null)
        {
            var tb = new Border { Padding = new Thickness(0, 0, 0, 6), Child = toolbar };
            DockPanel.SetDock(tb, Dock.Top);
            dock.Children.Add(tb);
        }
        dock.Children.Add(content);
        return dock;
    }

    private void MoveOverviewTo(int tab)
    {
        // the overview strip lives in exactly one tab at a time
        if (_overview.Parent is ContentControl old)
            old.Content = null;
        if (_tabs.Items[tab] is TabItem { Content: DockPanel dock } && dock.Children[0] is Border { Child: ContentControl cc })
            cc.Content = _overview;
    }

    private void UpdateOverview(ScanSession? s, DirNode? d)
    {
        _overview.Children.Clear();
        if (s == null || d == null)
        {
            _overview.Children.Add(new TextBlock { Text = "No scan yet", Classes = { "muted" } });
            return;
        }
        void Stat(string label, string value, string? tip = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 28, 2) };
            sp.Children.Add(new TextBlock { Text = label, Classes = { "muted" }, FontSize = 11.5 });
            sp.Children.Add(new TextBlock { Text = value, Classes = { "big" } });
            if (tip != null)
                ToolTip.SetTip(sp, tip);
            _overview.Children.Add(sp);
        }
        Stat("Size", _settings.Size(d.Size));
        Stat("Allocated", _settings.Size(d.Allocated), d.Allocated > d.Size ? $"Wasted by cluster rounding: {_settings.Size(d.Allocated - d.Size)}" : "Compressed or sparse files take less space than their size");
        Stat("Files", SizeFormat.Count(d.FileCount));
        Stat("Folders", SizeFormat.Count(d.FolderCount));
        if (d.Parent != null)
            Stat("Of the scan", SizeFormat.Percent(s.Root.Size <= 0 ? 0 : (double)d.Size / s.Root.Size));
        if (s.VolumeTotal > 0)
        {
            var used = s.VolumeTotal - s.VolumeFree;
            var sp = new StackPanel { Margin = new Thickness(0, 0, 28, 2), MinWidth = 170 };
            sp.Children.Add(new TextBlock { Text = $"Drive: {_settings.Size(s.VolumeFree)} free of {_settings.Size(s.VolumeTotal)}", Classes = { "muted" }, FontSize = 11.5 });
            sp.Children.Add(new PercentBar { Value = (double)used / s.VolumeTotal, Width = 170, Margin = new Thickness(0, 4, 0, 0) });
            _overview.Children.Add(sp);
        }
        if (_settings.CostPerGb > 0)
            Stat("Costs", (d.Allocated / (double)(1L << 30) * _settings.CostPerGb).ToString("N2", System.Globalization.CultureInfo.CurrentCulture) + " " + _settings.Currency);
        if (d.CompareSize is { } c)
            Stat("Change", c == -1 ? "new" : SizeFormat.FormatChange(d.SizeChange, _settings.Unit, _settings.Decimals), s.ComparedWith);
        var when = s.IndexFile != null ? $"Index file from {s.ScanDate:g}" : s.State is ScanState.Scanning or ScanState.Paused ? "Scanning…" : $"Scanned {s.Started:g} in {s.Duration.TotalSeconds:N1} s";
        Stat("Last change", SizeFormat.Date(d.Modified, _settings.DateFormat), when + "  ·  " + s.MethodUsed);
    }

    /// <summary>Fills the visible tab for the selected folder (stats are computed in the background).</summary>
    private void UpdateViews(bool force, bool live = false)
    {
        var s = SelectedScan;
        var d = SelectedFolder;
        var tab = _tabs.SelectedIndex;
        if (live)
        {
            if ((DateTime.Now - _lastLive).TotalSeconds < 2)
                return;
            _lastLive = DateTime.Now;
        }
        else if (!force && ReferenceEquals(_shownFor, d) && _shownTab == tab)
            return;
        _shownFor = d;
        _shownTab = tab;
        MoveOverviewTo(Math.Max(0, tab));
        UpdateOverview(s, d);
        if (s == null || d == null)
        {
            _details.ItemsSource = null;
            _treemap.Root = null;
            _pie.SetData([]);
            return;
        }
        switch (tab)
        {
            case 0:
                UpdateChart(s, d);
                break;
            case 1:
                UpdateDetails(s, d);
                break;
            case 2:
                RunInBackground(() => _settings.GroupExtensions ? Statistics.ByGroup(d, _settings.FileGroupSet) : Statistics.ByExtension(d, _settings.FileGroupSet, _filter?.Predicate), rows => FillStats(_extGrid, _extChart, rows, "File types in " + d.Name));
                break;
            case 3:
                RunInBackground(() => s.IsGroup && d.Parent == null ? Statistics.ByOwner(s.Members) : Statistics.ByOwner(d, s.Owners), rows => FillStats(_userGrid, _userChart, rows, "Owners in " + d.Name));
                break;
            case 4:
                RunInBackground(() => Statistics.ByAge(d, _settings.AgeIntervalSet, _settings.AgeBasis), rows => FillStats(_ageGrid, _ageChart, rows, $"Age of files ({_settings.AgeBasis.ToString().ToLowerInvariant()}) in {d.Name}", keepColors: true));
                break;
            case 5:
                RunInBackground(() => Statistics.TopFiles(d, _settings.TopCount, _settings.TreeMode == TreeValueMode.Allocated), files =>
                    _topGrid.ItemsSource = files.Select(f => new ItemRow(f, s, _settings)).ToList());
                break;
            case DupFilesTab or DupFoldersTab:
                UpdateDuplicates(tab, s, d);
                break;
            case 8:
                UpdateHistory(s);
                break;
        }
    }

    private void RunInBackground<T>(Func<T> compute, Action<T> show)
    {
        _statsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _statsCts = cts;
        _ = Task.Run(() =>
        {
            T result;
            try
            {
                result = compute();
            }
            catch (InvalidOperationException)
            {
                return; // tree changed under us during a scan: the next tick tries again
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!cts.IsCancellationRequested)
                    show(result);
            });
        }, CancellationToken.None);
    }

    private void FillStats(DataGrid grid, BarChart chart, List<StatRow> rows, string title, bool keepColors = false)
    {
        var items = rows.Select((r, i) => new StatItem(r, _settings, keepColors ? Palette.At(0) : i < 14 ? Palette.At(i) : Palette.Other)).ToList();
        grid.ItemsSource = items;
        chart.Title = title;
        chart.Format = v => _settings.Size(v);
        chart.SetData(rows.Select((r, i) => new ChartSlice
        {
            Label = r.Key.Length == 0 ? "(none)" : r.Key,
            Value = r.Size,
            ValueText = $"{_settings.Size(r.Size)}  ·  {SizeFormat.Count(r.Files)} files",
            Tag = r,
            Color = keepColors ? Palette.At(0) : null,
        }));
    }

    // ---------------- chart tab ----------------

    private DockPanel ChartTab()
    {
        _pie.Format = _bars.Format = _treemap.Format = v => _settings.Size(v);
        _pie.ItemDoubleClicked += (_, t) => ChartActivate(t);
        _bars.ItemDoubleClicked += (_, t) => ChartActivate(t);
        _pie.ItemClicked += (_, t) => ChartSelect(t);
        _bars.ItemClicked += (_, t) => ChartSelect(t);
        // one click: select in the tree (the map stays); double click on a folder: zoom in; right click: its menu
        _treemap.ItemClicked += (_, t) =>
        {
            ChartSelect(t);
            if (t is Item i)
            {
                _keepTreemapRoot = true;
                ShowInTree(i);
                _keepTreemapRoot = false;
                _treemap.Highlight = i;
            }
        };
        _treemap.ItemDoubleClicked += (_, t) =>
        {
            if (t is DirNode d)
                ShowInTree(d);
            else if (t is FileItem f)
                Shell.OpenItem(f.FullPath);
        };
        _treemap.UpRequested += (_, _) => GoUp();
        Avalonia.Automation.AutomationProperties.SetName(_treemap, "Treemap of the selected folder");
        Avalonia.Automation.AutomationProperties.SetName(_pie, "Pie chart");
        Avalonia.Automation.AutomationProperties.SetName(_bars, "Bar chart");
        Avalonia.Automation.AutomationProperties.SetName(_details, "Contents of the selected folder");
        Avalonia.Automation.AutomationProperties.SetName(_topGrid, "Largest files");
        Avalonia.Automation.AutomationProperties.SetName(_extGrid, "File types");
        Avalonia.Automation.AutomationProperties.SetName(_userGrid, "Owners");
        Avalonia.Automation.AutomationProperties.SetName(_ageGrid, "Age of files");
        _treemap.ItemRightClicked += (_, t) =>
        {
            if (t is Item i && i.Parent != null)
                ItemContextMenu(() => [i]).Open(_treemap);
        };
        _pie.ItemRightClicked += (_, t) =>
        {
            if (t is DirNode { Parent: not null } i)
                ItemContextMenu(() => [i]).Open(_pie);
        };
        ApplyChartSettings();

        var host = new DockPanel();
        var crumbs = new Border { Padding = new Thickness(0, 0, 0, 4), Child = _crumbs };
        DockPanel.SetDock(crumbs, Dock.Top);
        host.Children.Add(crumbs);
        var charts = new Panel();
        _pie.IsVisible = _bars.IsVisible = false; // UpdateChart shows the chosen one
        charts.Children.Add(_pie);
        charts.Children.Add(_bars);
        charts.Children.Add(_treemap);
        host.Children.Add(charts);

        ComboBox Combo(string[] items, string selected, Action<string> set)
        {
            var c = new ComboBox { ItemsSource = items, SelectedItem = selected, MinWidth = 110 };
            c.SelectionChanged += (_, _) =>
            {
                if (c.SelectedItem is string v)
                    set(v);
            };
            return c;
        }
        var type = Combo(["Treemap", "Pie", "Bar"], _settings.ChartType, v =>
        {
            _settings.ChartType = v;
            UpdateViews(true);
        });
        var source = Combo(["Folders", "Extensions", "Users", "Age"], _chartSource, v =>
        {
            _chartSource = v;
            UpdateViews(true);
        });
        CheckBox Opt(string text, bool on, Action<bool> set)
        {
            var cb = new CheckBox { Content = text, IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
            cb.IsCheckedChanged += (_, _) =>
            {
                set(cb.IsChecked == true);
                ApplyChartSettings();
                UpdateViews(true);
            };
            return cb;
        }
        var depth = new NumericUpDown { Minimum = 1, Maximum = 20, Value = _settings.TreemapDepth, Width = 110, FormatString = "0", VerticalAlignment = VerticalAlignment.Center };
        depth.ValueChanged += (_, _) =>
        {
            _settings.TreemapDepth = (int)(depth.Value ?? 6);
            ApplyChartSettings();
        };
        ToolTip.SetTip(depth, "Level of detail: folder levels drawn in the treemap");
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 4 };
        bar.Children.Add(type);
        bar.Children.Add(source);
        bar.Children.Add(Opt("Legend", _settings.ChartLegend, v => _settings.ChartLegend = v));
        bar.Children.Add(Opt("Labels", _settings.ChartLabels, v => _settings.ChartLabels = v));
        bar.Children.Add(Opt("Free space", _settings.ChartFreeSpace, v => _settings.ChartFreeSpace = v));
        bar.Children.Add(Opt("Hierarchy", _settings.TreemapHierarchy, v => _settings.TreemapHierarchy = v));
        bar.Children.Add(Opt("Single files", _settings.TreemapFiles, v => _settings.TreemapFiles = v));
        bar.Children.Add(Opt("Colour by type", _settings.TreemapColorByType, v => _settings.TreemapColorByType = v));
        bar.Children.Add(depth);
        bar.Children.Add(ToolButton("Up", GoUp, "Show the parent folder"));
        bar.Children.Add(ToolButton("Save image…", SaveChartAsync, "Save the chart as a PNG image"));
        return WithOverview(host, bar);
    }

    private void ApplyChartSettings()
    {
        _pie.ShowLegend = _settings.ChartLegend;
        _pie.ShowLabels = _settings.ChartLabels;
        _pie.OtherPercent = _settings.ChartOtherPercent;
        _bars.ShowGrid = _settings.ChartGrid;
        _treemap.ShowLabels = _settings.ChartLabels;
        _treemap.ShowHierarchy = _settings.TreemapHierarchy;
        _treemap.ShowFiles = _settings.TreemapFiles;
        _treemap.ColorByType = _settings.TreemapColorByType;
        _treemap.MaxDepth = _settings.TreemapDepth;
        _treemap.Groups = _settings.FileGroupSet;
        _treemap.InvalidateVisual();
    }

    private readonly WrapPanel _crumbs = new() { Orientation = Orientation.Horizontal };
    private bool _keepTreemapRoot;

    /// <summary>The path of the shown folder as buttons: click one to go there.</summary>
    private void UpdateCrumbs(DirNode d)
    {
        _crumbs.Children.Clear();
        var chain = new List<DirNode>();
        for (DirNode? p = d; p != null; p = p.Parent)
            chain.Add(p);
        chain.Reverse();
        if (_group != null && chain.Count > 0 && _tree.Scans.Any(x => ReferenceEquals(x.Root, chain[0])))
            chain.Insert(0, _group.Root);
        for (var i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            if (i > 0)
                _crumbs.Children.Add(new TextBlock { Text = "›", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0) });
            var label = node.Parent == null ? (ReferenceEquals(node, _group?.Root) ? ScanGroup.Name : node.FullPath) : node.Name;
            var b = new Button { Content = label, Classes = { "tool" }, Padding = new Thickness(6, 2), MinHeight = 24 };
            if (i == chain.Count - 1)
                b.FontWeight = FontWeight.SemiBold;
            ToolTip.SetTip(b, $"{_settings.Size(node.Size)}  ·  {SizeFormat.Count(node.FileCount)} files");
            b.Click += (_, _) => ShowInTree(node);
            _crumbs.Children.Add(b);
        }
    }

    private void UpdateChart(ScanSession s, DirNode d)
    {
        UpdateCrumbs(_keepTreemapRoot && _treemap.Root != null ? _treemap.Root : d);
        var type = _chartSource == "Folders" ? _settings.ChartType : _settings.ChartType == "Treemap" ? "Pie" : _settings.ChartType;
        _treemap.IsVisible = type == "Treemap";
        _pie.IsVisible = type == "Pie";
        _bars.IsVisible = type == "Bar";
        if (type == "Treemap")
        {
            if (!_keepTreemapRoot || _treemap.Root == null)
                _treemap.Root = d;
            _treemap.Highlight = _tree.Selected?.Item;
            _treemap.InvalidateVisual();
            return;
        }
        var title = $"{_chartSource} in {d.FullPath}";
        _pie.Title = _bars.Title = title;
        if (_chartSource == "Folders")
        {
            var slices = new List<ChartSlice>();
            foreach (var c in d.Dirs)
                slices.Add(new ChartSlice { Label = c.Name, Value = _tree.Value(c), Tag = c, ValueText = FormatTreeValue(_tree.Value(c)) });
            if (d.OwnFileCount > 0)
                slices.Add(new ChartSlice { Label = $"[{SizeFormat.Count(d.OwnFileCount)} Files]", Value = _tree.FilesValue(d), Tag = d, Color = Palette.Other, ValueText = FormatTreeValue(_tree.FilesValue(d)) });
            if (_settings.ChartFreeSpace && d.Parent == null && s.VolumeFree > 0 && _settings.TreeMode != TreeValueMode.Files)
                slices.Add(new ChartSlice { Label = "Free space", Value = s.VolumeFree, IsFree = true, ValueText = _settings.Size(s.VolumeFree) });
            SetChart(slices);
            return;
        }
        RunInBackground<List<StatRow>>(() => _chartSource switch
        {
            "Extensions" => Statistics.ByExtension(d, _settings.FileGroupSet),
            "Users" => Statistics.ByOwner(d, s.Owners),
            _ => Statistics.ByAge(d, _settings.AgeIntervalSet, _settings.AgeBasis),
        }, rows => SetChart(rows.Select(r => new ChartSlice { Label = r.Key.Length == 0 ? "(none)" : r.Key, Value = r.Size, Tag = r, ValueText = _settings.Size(r.Size) }).ToList()));
    }

    private void SetChart(List<ChartSlice> slices)
    {
        _bars.KeepOrder = _chartSource == "Age";
        _pie.SetData(slices);
        _bars.SetData(slices.Select(x => new ChartSlice { Label = x.Label, Value = x.Value, Tag = x.Tag, ValueText = x.ValueText, Color = x.IsFree ? Palette.Free : null, IsFree = x.IsFree }));
    }

    private string FormatTreeValue(long v) => _settings.TreeMode == TreeValueMode.Files ? SizeFormat.Count(v) + " files" : _settings.Size(v);

    private void ChartSelect(object? tag)
    {
        if (tag is Item i)
            SetStatus($"{i.FullPath}: {_settings.Size(i.Size)}");
    }

    private void ChartActivate(object? tag)
    {
        if (tag is DirNode d)
            ShowInTree(d);
        else if (tag is StatRow r && _chartSource == "Extensions")
            ApplyFilter(ViewFilter.ForExtensions([r.Key]));
    }

    private async Task SaveChartAsync()
    {
        var path = await SaveFileAsync("Save chart", "chart.png", ("PNG image", "*.png"));
        if (path == null)
            return;
        ChartBase chart = _treemap.IsVisible ? _treemap : _pie.IsVisible ? _pie : _bars;
        var size = chart.Bounds.Size;
        chart.SavePng(path, Math.Max(800, (int)size.Width), Math.Max(500, (int)size.Height));
        SetStatus("Chart saved: " + path);
    }

    // ---------------- details ----------------

    private PreviewPane? _preview;
    private ThumbnailView? _thumbs;
    private Grid? _detailsHost;

    private DockPanel DetailsTab()
    {
        _preview = new PreviewPane(_settings);
        _thumbs = new ThumbnailView(_settings);
        _thumbs.Activated += (_, i) =>
        {
            if (i is DirNode d)
                ShowInTree(d);
            else
                Shell.OpenItem(i.FullPath);
        };
        _thumbs.SelectionChanged += (_, i) => _preview.Show(i);
        _thumbs.ContextMenu = ItemContextMenu(() => [.. _thumbs.SelectedItems]);
        DragOut.Attach(_thumbs.List, () => [.. _thumbs.SelectedItems.Select(i => i.FullPath)]);
        _details.SelectionChanged += (_, _) => _preview.Show((_details.SelectedItem as ItemRow)?.Item);

        // list or thumbnails | splitter | preview pane
        _detailsHost = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,380") };
        _detailsHost.Children.Add(_details);
        _detailsHost.Children.Add(_thumbs);
        var split = Resizing.Split(_detailsHost, 1, 2, columns: true, "details.preview");
        Grid.SetColumn(_preview, 2);
        _detailsHost.Children.Add(_preview);

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6 };
        var thumbs = new ToggleButton { Content = Icons.Content(Icons.Image, "Thumbnails"), IsChecked = _settings.DetailsThumbnails, Classes = { "tool" } };
        var preview = new ToggleButton { Content = Icons.Content(Icons.Eye, "Preview"), IsChecked = _settings.PreviewPane, Classes = { "tool" } };
        void Apply()
        {
            _settings.DetailsThumbnails = thumbs.IsChecked == true;
            _settings.PreviewPane = preview.IsChecked == true;
            _details.IsVisible = !_settings.DetailsThumbnails;
            _thumbs.IsVisible = _settings.DetailsThumbnails;
            _preview.IsVisible = split.IsVisible = _settings.PreviewPane;
            _detailsHost.ColumnDefinitions[2].MaxWidth = _settings.PreviewPane ? double.PositiveInfinity : 0;
            UpdateViews(true);
        }
        thumbs.IsCheckedChanged += (_, _) => Apply();
        preview.IsCheckedChanged += (_, _) => Apply();
        ToolTip.SetTip(thumbs, "Large Explorer thumbnails instead of the list");
        ToolTip.SetTip(preview, "Preview pane: pictures, text files, thumbnails; compare two files side by side");
        bar.Children.Add(thumbs);
        bar.Children.Add(preview);
        bar.Children.Add(IconButton(Icons.Columns, "Columns…", ChooseDetailColumns));
        bar.Children.Add(ToolButton("Export list…", () => ExportListAsync(_details, "Details")));
        bar.Children.Add(ToolButton("Select all", () => _details.SelectAll()));
        bar.Children.Add(ToolButton("Invert selection", () => InvertSelection(_details)));
        bar.Children.Add(IconButton(Icons.Up, "Go up", GoUp));
        _details.IsVisible = !_settings.DetailsThumbnails;
        _thumbs.IsVisible = _settings.DetailsThumbnails;
        _preview.IsVisible = split.IsVisible = _settings.PreviewPane;
        _detailsHost.ColumnDefinitions[2].MaxWidth = _settings.PreviewPane ? double.PositiveInfinity : 0;
        return WithOverview(_detailsHost, bar);
    }

    private DataGrid CreateDetailsGrid()
    {
        var g = GridFactory.Create(_settings.DetailColumns.Where(GridFactory.ItemColumns.ContainsKey).Select(c => GridFactory.ItemColumns[c]));
        g.DoubleTapped += (_, _) =>
        {
            if (g.SelectedItem is ItemRow r)
            {
                if (r.Item is DirNode d)
                    ShowInTree(d);
                else
                    Shell.OpenItem(r.Path);
            }
        };
        g.ContextMenu = ItemContextMenu(() => g.SelectedItems.OfType<ItemRow>().Select(r => r.Item).ToList());
        Resizing.RememberColumns(g, "details");
        DragOut.Attach(g, () => [.. g.SelectedItems.OfType<ItemRow>().Select(r => r.Path)]);
        return g;
    }

    private void UpdateDetails(ScanSession s, DirNode d)
    {
        IEnumerable<Item> items = _tree.Selected?.Kind == RowKind.FilesGroup ? d.Files : d.Children;
        if (_filter != null)
            items = items.Where(i => i is DirNode dd ? dd.FilteredFiles > 0 : _filter.Predicate((FileItem)i));
        var sorted = items.OrderByDescending(i => i.Size).ToList();
        if (_settings.DetailsThumbnails)
            _thumbs?.Show(sorted);
        else
        {
            var rows = sorted.Select(i => new ItemRow(i, s, _settings)).ToList();
            FillExtraColumns(rows);
            _details.ItemsSource = rows;
        }
    }

    private void ChooseDetailColumns()
    {
        var menu = new ContextMenu();
        foreach (var (key, col) in GridFactory.ItemColumns.Where(k => k.Key is not ("Group" or "Hash" or "Detail")))
        {
            if (key == "Name")
                continue;
            var k = key;
            menu.Items.Add(Check(col.Header, _settings.DetailColumns.Contains(k), on =>
            {
                if (on)
                    _settings.DetailColumns.Add(k);
                else
                    _settings.DetailColumns.Remove(k);
                RebuildDetailColumns();
            }));
        }
        if (_settings.DetailExtraColumns.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var c in _settings.DetailExtraColumns.ToList())
            {
                var col = c;
                menu.Items.Add(Item($"Remove \"{c.Label}\"", () =>
                {
                    _settings.DetailExtraColumns.Remove(col);
                    RebuildDetailColumns();
                }));
            }
        }
        menu.Open(_details);
    }

    private void RebuildDetailColumns()
    {
        _details.Columns.Clear();
        foreach (var c in _settings.DetailColumns.Where(GridFactory.ItemColumns.ContainsKey))
            _details.Columns.Add(GridFactory.Column(GridFactory.ItemColumns[c]));
        AddExtraColumns();
        UpdateViews(true);
    }

    private static void InvertSelection(DataGrid g)
    {
        if (g.ItemsSource is not System.Collections.IEnumerable all)
            return;
        var selected = g.SelectedItems.Cast<object>().ToHashSet();
        g.SelectedItems.Clear();
        foreach (var o in all)
            if (!selected.Contains(o))
                g.SelectedItems.Add(o);
    }

    // ---------------- extensions / users / age ----------------

    private static Grid StatsLayout(DataGrid grid, BarChart chart, string key)
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("2*,6,3*") };
        g.Children.Add(new Border { Classes = { "panel" }, Child = chart });
        Resizing.Split(g, 1, 0, columns: false, key + ".chart");
        Grid.SetRow(grid, 2);
        g.Children.Add(grid);
        Resizing.RememberColumns(grid, key);
        return g;
    }

    private DockPanel ExtensionsTab()
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6 };
        var group = new CheckBox { Content = "Group file types", IsChecked = _settings.GroupExtensions, VerticalAlignment = VerticalAlignment.Center };
        group.IsCheckedChanged += (_, _) =>
        {
            _settings.GroupExtensions = group.IsChecked == true;
            UpdateViews(true);
        };
        bar.Children.Add(group);
        bar.Children.Add(ToolButton("Filter tree by selection", () =>
        {
            var keys = _extGrid.SelectedItems.OfType<StatItem>().Select(i => i.Row.Key).ToList();
            if (keys.Count == 0)
                return;
            ApplyFilter(_settings.GroupExtensions ? ViewFilter.ForGroup(_settings.FileGroupSet, keys[0]) : ViewFilter.ForExtensions(keys));
        }));
        bar.Children.Add(ToolButton("Search these files", () =>
        {
            var keys = _extGrid.SelectedItems.OfType<StatItem>().Select(i => i.Row.Key).ToList();
            if (keys.Count > 0 && !_settings.GroupExtensions)
                OpenSearchFor(c => c.Include.Add(new FilterRule { Pattern = string.Join(';', keys.Select(k => k.Length == 0 ? "*." : "*." + k)), Exclude = false }));
        }));
        bar.Children.Add(ToolButton("Copy file list", () => CopyFilesWhereAsync(f => _extGrid.SelectedItems.OfType<StatItem>().Any(i => _settings.GroupExtensions ? _settings.FileGroupSet.GroupOf(f.Extension) == i.Row.Key : i.Row.Key == f.Extension))));
        bar.Children.Add(ToolButton("Add column to Details", () =>
        {
            foreach (var i in _extGrid.SelectedItems.OfType<StatItem>())
                AddExtraColumn(_settings.GroupExtensions ? "group" : "ext", i.Row.Key, _settings.GroupExtensions ? i.Row.Key : (i.Row.Key.Length == 0 ? "(no ext.)" : "." + i.Row.Key));
        }));
        bar.Children.Add(ToolButton("Export list…", () => ExportStatsAsync(_extGrid, "Extensions")));
        _extChart.ItemDoubleClicked += (_, t) =>
        {
            if (t is StatRow r)
                ApplyFilter(_settings.GroupExtensions ? ViewFilter.ForGroup(_settings.FileGroupSet, r.Key) : ViewFilter.ForExtensions([r.Key]));
        };
        _extChart.Format = v => _settings.Size(v);
        return WithOverview(StatsLayout(_extGrid, _extChart, "extensions"), bar);
    }

    private DockPanel UsersTab()
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6 };
        bar.Children.Add(ToolButton("Filter tree by selection", () =>
        {
            if (SelectedScan is not { } s)
                return;
            var names = _userGrid.SelectedItems.OfType<StatItem>().Select(i => i.Row.Key).ToList();
            var ids = Enumerable.Range(-1, s.Owners.Count + 1).Where(id => names.Contains(id < 0 ? "(unknown)" : s.Owners.Name(id))).ToList();
            if (ids.Count > 0)
                ApplyFilter(ViewFilter.ForOwners(ids, string.Join(", ", names)));
        }));
        bar.Children.Add(ToolButton("Search their files", () =>
        {
            if (_userGrid.SelectedItem is StatItem i)
                OpenSearchFor(c => c.Owner = i.Row.Key);
        }));
        bar.Children.Add(ToolButton("Copy file list", () =>
        {
            if (SelectedScan is not { } s)
                return Task.CompletedTask;
            var names = _userGrid.SelectedItems.OfType<StatItem>().Select(i => i.Row.Key).ToHashSet();
            return CopyFilesWhereAsync(f => names.Contains(f.OwnerId < 0 ? "(unknown)" : s.Owners.Name(f.OwnerId)));
        }));
        bar.Children.Add(ToolButton("Add column to Details", () =>
        {
            foreach (var i in _userGrid.SelectedItems.OfType<StatItem>())
                AddExtraColumn("owner", i.Row.Key, OperatingSystem.IsWindows() ? Security.Permissions.ShortName(i.Row.Key) : i.Row.Key);
        }));
        bar.Children.Add(ToolButton("Export list…", () => ExportStatsAsync(_userGrid, "Users")));
        var note = new TextBlock { Text = "Owners are collected when Options › Scan › \"Statistics on file owners\" is on.", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(note);
        _userChart.Format = v => _settings.Size(v);
        return WithOverview(StatsLayout(_userGrid, _userChart, "users"), bar);
    }

    private DockPanel AgeTab()
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6 };
        var basis = new ComboBox { ItemsSource = new[] { "Last modified", "Last accessed", "Created" }, SelectedIndex = (int)_settings.AgeBasis, MinWidth = 140 };
        basis.SelectionChanged += (_, _) =>
        {
            _settings.AgeBasis = (AgeBasis)Math.Max(0, basis.SelectedIndex);
            UpdateViews(true);
        };
        bar.Children.Add(basis);
        bar.Children.Add(ToolButton("Search files of this interval", () =>
        {
            if (_ageGrid.SelectedItem is StatItem { Row: var r })
                OpenSearchFor(c =>
                {
                    c.DateBasis = _settings.AgeBasis;
                    c.NewerThan = r.From == DateTime.MinValue ? null : r.From;
                    c.OlderThan = r.To;
                });
        }));
        bar.Children.Add(ToolButton("Customize intervals…", () => OpenOptions("Age of files")));
        bar.Children.Add(ToolButton("Add column to Details", () =>
        {
            foreach (var i in _ageGrid.SelectedItems.OfType<StatItem>())
                if (i.Row.Key != Statistics.FutureLabel)
                    AddExtraColumn("age", i.Row.Key, i.Row.Key);
        }));
        bar.Children.Add(ToolButton("Export list…", () => ExportStatsAsync(_ageGrid, "Age of files")));
        _ageChart.Format = v => _settings.Size(v);
        return WithOverview(StatsLayout(_ageGrid, _ageChart, "ages"), bar);
    }

    private DockPanel TopTab()
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6 };
        var count = new ComboBox { ItemsSource = new[] { 50, 100, 250, 500, 1000, 5000 }, SelectedItem = _settings.TopCount, MinWidth = 90 };
        count.SelectionChanged += (_, _) =>
        {
            if (count.SelectedItem is int n)
            {
                _settings.TopCount = n;
                UpdateViews(true);
            }
        };
        bar.Children.Add(new TextBlock { Text = "Largest", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(count);
        bar.Children.Add(new TextBlock { Text = "files", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(ToolButton("Export list…", () => ExportListAsync(_topGrid, "Top files")));
        bar.Children.Add(ToolButton("Open search", () => OpenSearch(null, Search.SearchTemplate.LargeFiles)));
        return WithOverview(_topGrid, bar);
    }

    private static readonly string[] TopColumns = ["Name", "Size", "Allocated", "Modified", "Accessed", "Owner", "Path"];

    private DataGrid CreateTopGrid()
    {
        var g = GridFactory.Create(TopColumns.Select(k => GridFactory.ItemColumns[k]));
        g.DoubleTapped += (_, _) =>
        {
            if (g.SelectedItem is ItemRow r)
                ShowInTree(r.Item);
        };
        g.ContextMenu = ItemContextMenu(() => g.SelectedItems.OfType<ItemRow>().Select(r => r.Item).ToList());
        Resizing.RememberColumns(g, "top");
        DragOut.Attach(g, () => [.. g.SelectedItems.OfType<ItemRow>().Select(r => r.Path)]);
        return g;
    }

    // ---------------- history ----------------

    private DockPanel HistoryTab()
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6 };
        var zero = new CheckBox { Content = "Show zero point", VerticalAlignment = VerticalAlignment.Center };
        zero.IsCheckedChanged += (_, _) =>
        {
            _historyChart.ZeroBased = zero.IsChecked == true;
            _historyChart.InvalidateVisual();
        };
        bar.Children.Add(zero);
        bar.Children.Add(ToolButton("Export…", async () =>
        {
            var p = await SaveFileAsync("Export history", "history.csv", ("CSV", "*.csv"));
            if (p != null)
                _history.ExportCsv(p, SelectedScan?.Root.FullPath);
        }));
        bar.Children.Add(ToolButton("Import…", async () =>
        {
            var p = await OpenFileAsync("Import history", ("CSV", "*.csv"));
            if (p != null)
                SetStatus($"{_history.ImportCsv(p)} history values imported.");
            UpdateViews(true);
        }));
        bar.Children.Add(ToolButton("Purge…", PurgeHistoryAsync));
        bar.Children.Add(ToolButton("Change storage location…", async () =>
        {
            var p = await SaveFileAsync("History data file", "history.db", ("SQLite", "*.db"));
            if (p != null)
            {
                _history.MoveTo(p);
                _settings.HistoryFile = p;
                UpdateViews(true);
            }
        }));
        bar.Children.Add(_historyInfo);
        _historyChart.Format = v => _settings.Size(v);

        // what changed since an earlier scan, folder by folder
        _growthGrid = GridFactory.Create([
            new("Folder", nameof(GrowthItem.Folder), Width: 380),
            new("Change", nameof(GrowthItem.ChangeText), nameof(GrowthItem.Change), 110, true),
            new("Then", nameof(GrowthItem.ThenText), nameof(GrowthItem.Then), 110, true),
            new("Now", nameof(GrowthItem.NowText), nameof(GrowthItem.Now), 110, true),
            new("Files", nameof(GrowthItem.FilesChangeText), nameof(GrowthItem.FilesChange), 90, true),
        ]);
        Avalonia.Automation.AutomationProperties.SetName(_growthGrid, "What changed");
        Resizing.RememberColumns(_growthGrid, "growth");
        _growthGrid.DoubleTapped += (_, _) =>
        {
            if (_growthGrid.SelectedItem is GrowthItem { Row.Node: { } n })
                ShowInTree(n);
        };
        _growthGrid.SelectionChanged += (_, _) =>
        {
            if (_growthGrid.SelectedItem is GrowthItem { Row.Node: { } n })
                _tree.Mark([n], reveal: true);
        };
        _growthSince.MinWidth = 200;
        Avalonia.Automation.AutomationProperties.SetName(_growthSince, "Compare with the scan of");
        _growthSince.SelectionChanged += (_, _) =>
        {
            if (!_fillingGrowth)
                ShowGrowth();
        };
        _growthDeepest.IsCheckedChanged += (_, _) => ShowGrowth();
        ToolTip.SetTip(_growthDeepest, "Leave out a folder when one of its sub folders changed by (nearly) as much: the list names where it happened");
        var growthBar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, Margin = new Thickness(0, 0, 0, 4) };
        growthBar.Children.Add(new TextBlock { Text = "What changed since", VerticalAlignment = VerticalAlignment.Center, Classes = { "header" } });
        growthBar.Children.Add(_growthSince);
        growthBar.Children.Add(_growthDeepest);
        growthBar.Children.Add(_growthInfo);
        var growth = new DockPanel();
        DockPanel.SetDock(growthBar, Dock.Top);
        growth.Children.Add(growthBar);
        growth.Children.Add(_growthGrid);

        var split = new Grid { RowDefinitions = new RowDefinitions("*,6,*") };
        split.Children.Add(new Border { Classes = { "panel" }, Child = _historyChart });
        Resizing.Split(split, 1, 0, columns: false, "history.chart");
        Grid.SetRow(growth, 2);
        split.Children.Add(growth);
        return WithOverview(split, bar);
    }

    private DataGrid _growthGrid = null!;
    private readonly ComboBox _growthSince = new();
    private readonly CheckBox _growthDeepest = new() { Content = "Only where it happened", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _growthInfo = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
    private bool _fillingGrowth;
    private List<DateTime> _growthDates = [];
    private ScanSession? _growthScan;

    /// <summary>Fills the snapshot list for the scan shown (newest first, the scan itself left out).</summary>
    private void UpdateGrowthDates(ScanSession s)
    {
        List<DateTime> dates;
        try
        {
            dates = s.IsGroup ? [] : _history.Snapshots(s.Root.FullPath);
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            dates = [];
        }
        // the snapshot of this very scan shows no change
        dates.RemoveAll(d => Math.Abs((d - s.ScanDate).TotalSeconds) < 2);
        var same = ReferenceEquals(_growthScan, s) && dates.SequenceEqual(_growthDates);
        _growthScan = s;
        _growthDates = dates;
        if (same)
        {
            ShowGrowth();
            return;
        }
        _fillingGrowth = true;
        _growthSince.ItemsSource = dates.Select(d => d.ToString("g", System.Globalization.CultureInfo.CurrentCulture)).ToList();
        _growthSince.SelectedIndex = dates.Count > 0 ? 0 : -1;
        _fillingGrowth = false;
        ShowGrowth();
    }

    private void ShowGrowth()
    {
        if (_growthScan is not { } s || s.IsGroup)
        {
            _growthGrid.ItemsSource = null;
            _growthInfo.Text = "Select a scan.";
            return;
        }
        if (_growthSince.SelectedIndex < 0 || _growthSince.SelectedIndex >= _growthDates.Count)
        {
            _growthGrid.ItemsSource = null;
            _growthInfo.Text = "Folder sizes are kept with each scan from now on: the next scan of this folder shows what changed.";
            return;
        }
        try
        {
            var then = _history.Folders(s.Root.FullPath, _growthDates[_growthSince.SelectedIndex]);
            var rows = Growth.Compare(s.Root, then, _growthDeepest.IsChecked == true);
            _growthGrid.ItemsSource = rows.Select(r => new GrowthItem(r, _settings)).ToList();
            _growthInfo.Text = rows.Count == 0 ? "No folder changed." : $"{rows.Count:N0} folders  ·  total {SizeFormat.FormatChange(s.Root.Size - (then.TryGetValue("", out var root) ? root.Size : s.Root.Size))}";
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            _growthInfo.Text = "History not readable: " + e.Message;
        }
    }

    private void UpdateHistory(ScanSession s)
    {
        if (s.IsGroup)
        {
            _historyChart.Title = "Select a scan to see its history";
            _historyChart.SetData([]);
            _historyInfo.Text = "";
            UpdateGrowthDates(s);
            return;
        }
        List<HistoryPoint> pts;
        try
        {
            pts = _history.Get(s.Root.FullPath);
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            _historyInfo.Text = "History not readable: " + e.Message;
            return;
        }
        _historyChart.Title = "Used space of " + s.Root.FullPath;
        var series = new List<LineChart.Series>
        {
            new("Size", Palette.At(0), [.. pts.Select(p => (p.At, p.Size))]),
            new("Allocated", Palette.At(1), [.. pts.Select(p => (p.At, p.Allocated))]),
        };
        if (pts.Any(p => p.VolumeTotal > 0) && s.Root.Parent == null && s.Root.FullPath.Length <= 3)
            series.Add(new("Free on drive", Palette.At(2), [.. pts.Where(p => p.VolumeTotal > 0).Select(p => (p.At, p.VolumeFree))]));
        _historyChart.SetData(series);
        _historyInfo.Text = pts.Count == 0 ? "" : $"{pts.Count} scans since {pts[0].At:d}  ·  data file {_history.File}";
        UpdateGrowthDates(s);
    }

    private async Task PurgeHistoryAsync()
    {
        var t = await Dialogs.InputDialog.AskAsync(this, "Purge history", "Remove history values older than this many days:", "365");
        if (t == null || !int.TryParse(t, out var days))
            return;
        var n = _history.Purge(SelectedScan?.Root.FullPath, DateTime.Now.AddDays(-days));
        SetStatus($"{n} history values removed.");
        UpdateViews(true);
    }

    // ---------------- view filter ----------------

    private void ApplyFilter(ViewFilter f)
    {
        _filter = f;
        foreach (var s in _tree.Scans)
            f.Apply(s.Root);
        if (_group != null)
            f.Apply(_group.Root);
        _tree.Filtered = true;
        _tree.FileFilter = f.Predicate;
        _tree.Rebuild();
        _filterText.Text = "Filter: " + f.Description;
        _filterBar.IsVisible = true;
        UpdateViews(true);
    }

    private void ClearFilter()
    {
        _filter = null;
        _tree.Filtered = false;
        _tree.FileFilter = null;
        _filterBar.IsVisible = false;
        _tree.Rebuild();
        UpdateViews(true);
    }
}
