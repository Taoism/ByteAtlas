using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ByteAtlas.Controls;
using ByteAtlas.Model;
using ByteAtlas.Scanning;
using ByteAtlas.Storage;
using ByteAtlas.Views;

namespace ByteAtlas;

/// <summary>Main window: scans in a size tree on the left, views of the selected folder on the right.</summary>
public sealed partial class MainWindow : Window
{
    public static WindowIcon? AppIcon { get; private set; }

    private readonly AppSettings _settings;
    private readonly SizeTree _tree;
    private readonly DriveListPanel _drives;
    private readonly TabControl _tabs = new();
    private readonly TextBox _pathBox = new() { PlaceholderText = "Drive, folder or \\\\server\\share to scan", MinWidth = 260 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _statusRight = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
    private readonly Border _statusBar;
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Width = 120, Height = 4, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _filterBar;
    private readonly TextBlock _filterText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer;
    private readonly HistoryStore _history;
    private readonly Grid _body;
    private readonly string[] _args;

    private MenuItem _recentMenu = new() { Header = "_Recently scanned" };
    private MenuItem _scanDriveMenu = new() { Header = "Scan _drive" };

    public MainWindow(AppSettings settings, string[] args)
    {
        _settings = settings;
        _args = args;
        _history = new HistoryStore(string.IsNullOrEmpty(settings.HistoryFile) ? null : settings.HistoryFile);
        try
        {
            AppIcon = new WindowIcon(AssetLoader.Open(new Uri("avares://ByteAtlas/Assets/byteatlas.ico")));
            Icon = AppIcon;
        }
        catch (Exception e) when (e is IOException or FileNotFoundException)
        {
            AppIcon = null;
        }
        Title = "ByteAtlas" + (Shell.IsAdmin ? " (Administrator)" : "");
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        MinWidth = 900;
        MinHeight = 560;
        if (settings.WindowX is { } wx && settings.WindowY is { } wy)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(wx, wy);
        }
        else
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.Maximized)
            WindowState = WindowState.Maximized;

        _tree = new SizeTree(settings);
        _tree.SelectionChanged += (_, _) => OnTreeSelection();
        _tree.RowActivated += (_, r) =>
        {
            if (r.Kind == RowKind.File)
                Shell.Open(r.Item.FullPath);
            else
                _tree.Toggle(r);
        };
        _tree.ContextMenu = TreeContextMenu();
        DragOut.Attach(_tree, () => [.. _tree.SelectedRows.Where(r => r.Kind != RowKind.FilesGroup && r.Item.Parent != null).Select(r => r.Item.FullPath)]);

        _drives = new DriveListPanel(settings);
        _drives.ScanRequested += (_, p) => StartScan(p);

        _details = CreateDetailsGrid();
        AddExtraColumns();
        _topGrid = CreateTopGrid();
        BuildTabs();

        // drive list above the tree, with a splitter between them
        _left = new Grid { RowDefinitions = new RowDefinitions("230,6,*") };
        _left.Children.Add(_drives);
        _driveSplit = Resizing.Split(_left, 1, 0, columns: false, "main.drives");
        _driveRow = _left.RowDefinitions[0].Height;
        var treeBorder = new Border { Classes = { "panel" }, Child = _tree };
        Grid.SetRow(treeBorder, 2);
        _left.Children.Add(treeBorder);
        ShowDriveList(settings.ShowDriveList);
        var left = _left;

        _body = new Grid { ColumnDefinitions = new ColumnDefinitions($"{settings.TreeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)},5,*"), Margin = new Thickness(8, 4, 8, 4) };
        _body.Children.Add(left);
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(split, 1);
        _body.Children.Add(split);
        Grid.SetColumn(_tabs, 2);
        _body.Children.Add(_tabs);

        _filterBar = new Border
        {
            Classes = { "strip" },
            IsVisible = false,
            Child = DialogRow(8, _filterText, ToolButton("Clear filter", ClearFilter)),
        };

        _statusBar = new Border
        {
            Classes = { "strip" },
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsVisible = settings.ShowStatusBar,
            Child = StatusGrid(),
        };

        var dock = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        DockPanel.SetDock(_filterBar, Dock.Top);
        dock.Children.Add(_filterBar);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        dock.Children.Add(_statusBar);
        dock.Children.Add(_body);
        Content = dock;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => OnTimer();
        _timer.Start();

        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, (_, e) => e.DragEffects = !DragOut.Active && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Link : DragDropEffects.None);
        DragDrop.AddDropHandler(this, (_, e) =>
        {
            if (DragOut.Active)
                return;
            foreach (var f in e.DataTransfer.TryGetFiles() ?? [])
                if (f.TryGetLocalPath() is { } p)
                    StartScan(Directory.Exists(p) ? p : Path.GetDirectoryName(p) ?? p);
        });

        AddKeyBindings();
        Opened += (_, _) => OnOpened();
        Closing += (_, _) => SaveWindowState();
    }

    private void OnOpened()
    {
        RefreshRecentMenu();
        var paths = new List<string>();
        var search = false;
        foreach (var a in _args)
        {
            if (a is "--search" or "/search")
                search = true;
            else if (!a.StartsWith('-'))
                paths.Add(a);
        }
        if (search && paths.Count > 0)
        {
            OpenSearch(paths, null);
            return;
        }
        if (paths.Count == 0)
        {
            paths = _settings.Startup switch
            {
                StartupAction.LastPaths => [.. _settings.LastPaths],
                StartupAction.FixedPaths => [.. _settings.StartupPaths],
                _ => [],
            };
        }
        foreach (var p in paths)
        {
            if (File.Exists(p) && Path.GetExtension(p).ToLowerInvariant() is ".sqlite" or ".xml" or ".badb")
                _ = LoadIndexAsync(p);
            else
                StartScan(p);
        }
        if (paths.Count == 0)
            SetStatus("Choose a drive on the left or type a path and press Scan.");
    }

    private Grid StatusGrid()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12 };
        g.Children.Add(_status);
        Grid.SetColumn(_progress, 1);
        g.Children.Add(_progress);
        Grid.SetColumn(_statusRight, 2);
        g.Children.Add(_statusRight);
        return g;
    }

    private void SetStatus(string text) => _status.Text = text;

    private Grid _left = new();
    private GridSplitter? _driveSplit;
    private GridLength _driveRow = new(230);

    private void ShowDriveList(bool show)
    {
        if (!show && _left.RowDefinitions[0].Height.Value > 0)
            _driveRow = _left.RowDefinitions[0].Height;
        _drives.IsVisible = show;
        if (_driveSplit != null)
            _driveSplit.IsVisible = show;
        _left.RowDefinitions[0].Height = show ? (_driveRow.Value > 20 ? _driveRow : new GridLength(230)) : new GridLength(0);
    }

    private void SaveWindowState()
    {
        _settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }
        _settings.TreeWidth = _body.ColumnDefinitions[0].ActualWidth > 100 ? _body.ColumnDefinitions[0].ActualWidth : _settings.TreeWidth;
        _settings.LastPaths = [.. _tree.Scans.Where(s => s.IndexFile == null).Select(s => s.Path)];
        if (_tabs.SelectedItem is TabItem t && t.Header is string h)
            _settings.LastTab = h;
        _settings.Save();
        foreach (var cts in _running.Values)
            cts.Cancel();
    }

    // ---------------- toolbar ----------------

    private Control BuildToolbar()
    {
        _pathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_pathBox.Text))
            {
                StartScan(_pathBox.Text.Trim());
                e.Handled = true;
            }
        };
        var browse = IconButton(Icons.FolderOpen, null, async () =>
        {
            var p = await PickFolderAsync("Choose a folder to scan");
            if (p != null)
            {
                _pathBox.Text = p;
                StartScan(p);
            }
        });
        ToolTip.SetTip(browse, "Choose a folder");
        var scan = ToolButton("Scan", () => StartScan(_pathBox.Text?.Trim() ?? ""));
        scan.Content = Icons.Content(Icons.Play, "Scan");
        if (scan.Content is StackPanel sp && sp.Children[0] is Avalonia.Controls.Shapes.Path play)
            play.Fill = Brushes.White;
        scan.Classes.Add("accent");
        scan.Classes.Remove("tool");

        var unit = new ComboBox { ItemsSource = new[] { "Auto", "Bytes", "KB", "MB", "GB", "TB" }, SelectedIndex = (int)_settings.Unit, MinWidth = 84 };
        unit.SelectionChanged += (_, _) =>
        {
            _settings.Unit = (SizeUnit)Math.Max(0, unit.SelectedIndex);
            RefreshAll();
        };
        var mode = new ComboBox { ItemsSource = new[] { "Size", "Allocated space", "Number of files", "Percent" }, SelectedIndex = (int)_settings.TreeMode, MinWidth = 130 };
        mode.SelectionChanged += (_, _) =>
        {
            _settings.TreeMode = (TreeValueMode)Math.Max(0, mode.SelectedIndex);
            _tree.Rebuild();
        };
        _modeCombo = mode;
        _unitCombo = unit;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_pathBox);
        row.Children.Add(browse);
        row.Children.Add(scan);
        row.Children.Add(Sep());
        row.Children.Add(IconButton(Icons.Stop, "Stop", StopSelected, "Stop the selected scan (Esc)"));
        row.Children.Add(IconButton(Icons.Pause, "Pause", PauseSelected, "Pause / resume the selected scan"));
        row.Children.Add(IconButton(Icons.Refresh, "Refresh", () => RefreshSelected(true), "Scan the selected folder again (F5)"));
        row.Children.Add(Sep());
        row.Children.Add(IconButton(Icons.Back, null, GoBack, "Back (Alt+Left)"));
        row.Children.Add(IconButton(Icons.Forward, null, GoForward, "Forward (Alt+Right)"));
        row.Children.Add(IconButton(Icons.Up, null, GoUp, "Parent folder (Backspace)"));
        row.Children.Add(Sep());
        row.Children.Add(unit);
        row.Children.Add(mode);
        row.Children.Add(Sep());
        row.Children.Add(IconButton(Icons.Search, "Search", () => OpenSearch(null, null), "File search: large, old, temporary, duplicate files and more (Ctrl+F)"));
        row.Children.Add(IconButton(Icons.Export, "Export", () => ExportTreeAsync(null), "Export the selected branch"));
        row.Children.Add(IconButton(Icons.Options, "Options", OpenOptions, "Options (Ctrl+,)"));
        return new Border { Classes = { "strip" }, Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = row } };
    }

    private ComboBox? _modeCombo;
    private ComboBox? _unitCombo;

    private static Control Sep() => new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)) };

    internal static Button ToolButton(string text, Action action, string? tip = null)
    {
        var b = new Button { Content = text, Classes = { "tool" } };
        b.Click += (_, _) => action();
        if (tip != null)
            ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>A flat toolbar button with a dark grey glyph (and a label when <paramref name="text"/> is set).</summary>
    internal static Button IconButton(string icon, string? text, Action action, string? tip = null)
    {
        var b = ToolButton(text ?? "", action, tip ?? text);
        b.Content = Icons.Content(icon, text);
        return b;
    }

    internal static Button IconButton(string icon, string? text, Func<Task> action, string? tip = null)
    {
        var b = ToolButton(text ?? "", action, tip ?? text);
        b.Content = Icons.Content(icon, text);
        return b;
    }

    internal static Button ToolButton(string text, Func<Task> action, string? tip = null)
    {
        var b = new Button { Content = text, Classes = { "tool" } };
        b.Click += async (_, _) => await action();
        if (tip != null)
            ToolTip.SetTip(b, tip);
        return b;
    }

    internal static StackPanel DialogRow(double spacing, params Control[] c)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var x in c)
            sp.Children.Add(x);
        return sp;
    }

    // ---------------- menu ----------------

    private static MenuItem Item(string header, Action action, KeyGesture? key = null)
    {
        var mi = new MenuItem { Header = header, InputGesture = key };
        mi.Click += (_, _) => action();
        return mi;
    }

    private static MenuItem Item(string header, Func<Task> action, KeyGesture? key = null)
    {
        var mi = new MenuItem { Header = header, InputGesture = key };
        mi.Click += async (_, _) => await action();
        return mi;
    }

    private static MenuItem Check(string header, bool on, Action<bool> set)
    {
        var mi = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
        mi.Click += (_, _) =>
        {
            set(!on);
            on = !on;
            mi.IsChecked = on;
        };
        return mi;
    }

    private static MenuItem Radio(string header, string group, bool on, Action set)
    {
        var mi = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, GroupName = group, IsChecked = on };
        mi.Click += (_, _) => set();
        return mi;
    }

    private static MenuItem Sub(string header, params Control[] items)
    {
        var mi = new MenuItem { Header = header };
        foreach (var i in items)
            mi.Items.Add(i);
        return mi;
    }

    private Menu BuildMenu()
    {
        _scanDriveMenu.SubmenuOpened += (_, _) => FillDriveMenu();
        _scanDriveMenu.Items.Add(new MenuItem { Header = "…" });

        var file = Sub("_File",
            Item("_Select folder to scan…", async () =>
            {
                var p = await PickFolderAsync("Choose a folder to scan");
                if (p != null)
                    StartScan(p);
            }, new KeyGesture(Key.O, KeyModifiers.Control)),
            _scanDriveMenu,
            _recentMenu,
            new Separator(),
            Item("_Load scan from index file…", LoadIndexDialogAsync),
            Item("Sa_ve scan to index file…", SaveIndexAsync, new KeyGesture(Key.S, KeyModifiers.Control)),
            Sub("_Compare",
                Item("With _saved scan (index file)…", CompareWithIndexAsync),
                Item("With another _path…", CompareWithPathAsync),
                Item("With a _snapshot…", CompareWithSnapshotAsync),
                Item("_Clear comparison", ClearComparison)),
            Item("Create s_napshot of this drive", CreateSnapshotAsync),
            new Separator(),
            Sub("_Export",
                Item("_Excel file…", () => ExportTreeAsync(Export.ExportFormat.Excel)),
                Item("_PDF report…", () => ExportTreeAsync(Export.ExportFormat.Pdf)),
                Item("_HTML file…", () => ExportTreeAsync(Export.ExportFormat.Html)),
                Item("_CSV file…", () => ExportTreeAsync(Export.ExportFormat.Csv)),
                Item("Plain _text…", () => ExportTreeAsync(Export.ExportFormat.Text)),
                new Separator(),
                Item("Copy branch to clip_board", CopyBranchAsync),
                Item("Copy _list of files", CopyFileListAsync),
                Item("Send by _email…", EmailReportAsync),
                Item("List of _all files (CSV)…", ExportAllFilesAsync),
                new Separator(),
                Item("Customi_ze export…", () => OpenOptions("Export"))),
            Item("_Print report…", PrintReportAsync, new KeyGesture(Key.P, KeyModifiers.Control)),
            new Separator(),
            Item("Set _title…", SetTitleAsync),
            Item("_Remove scan", RemoveSelectedScan),
            new Separator(),
            Item("E_xit", Close));

        var scan = Sub("_Scan",
            Item("_Refresh selected folder", () => RefreshSelected(true), new KeyGesture(Key.F5)),
            Item("Refresh folder _without sub folders", () => RefreshSelected(false), new KeyGesture(Key.F5, KeyModifiers.Shift)),
            Item("Refresh _all scans", RefreshAllScans, new KeyGesture(Key.F5, KeyModifiers.Control)),
            Item("_Stop", StopSelected),
            Item("_Pause / resume", PauseSelected),
            new Separator(),
            Check("_Watch for file system changes", _settings.WatchChanges, v =>
            {
                _settings.WatchChanges = v;
                UpdateWatchers();
            }),
            Item("Always scan on _startup", () =>
            {
                if (SelectedScan is { } s)
                {
                    _settings.Startup = StartupAction.FixedPaths;
                    if (!_settings.StartupPaths.Contains(s.Path, StringComparer.OrdinalIgnoreCase))
                        _settings.StartupPaths.Add(s.Path);
                    SetStatus($"{s.Path} will be scanned on every start.");
                }
            }),
            new Separator(),
            Item("Sc_hedule this scan…", ScheduleScanAsync),
            Item("_Manage scheduled scans…", ManageSchedulesAsync),
            new Separator(),
            Item("Show all _errors…", ShowErrorsAsync),
            Item("Start as a_dministrator", RestartAsAdmin));

        var expandItems = new List<Control>();
        for (var lvl = 1; lvl <= 6; lvl++)
        {
            var l = lvl;
            expandItems.Add(Item($"To level _{l}", () => ExpandSelected(l), new KeyGesture(Key.D0 + l, KeyModifiers.Control)));
        }
        expandItems.Add(Item("_Full expand", () => ExpandSelected(0), new KeyGesture(Key.Multiply)));
        expandItems.Add(Item("Folders _larger than threshold", () =>
        {
            if (SelectedFolder is { } d)
                _tree.ExpandToLevel(d, 0, _settings.SizeThreshold);
        }));
        expandItems.Add(Item("_Reset expand state", () =>
        {
            if (SelectedScan is { } s)
                _tree.Collapse(s.Root);
        }));

        var view = Sub("_View",
            Sub("Show in tree",
                Radio("_Size", "mode", _settings.TreeMode == TreeValueMode.Size, () => SetMode(TreeValueMode.Size)),
                Radio("_Allocated space", "mode", _settings.TreeMode == TreeValueMode.Allocated, () => SetMode(TreeValueMode.Allocated)),
                Radio("_Number of files", "mode", _settings.TreeMode == TreeValueMode.Files, () => SetMode(TreeValueMode.Files)),
                Radio("_Percent", "mode", _settings.TreeMode == TreeValueMode.Percent, () => SetMode(TreeValueMode.Percent))),
            Sub("_Unit",
                Radio("_Automatic", "unit", _settings.Unit == SizeUnit.Auto, () => SetUnit(SizeUnit.Auto)),
                Radio("_Bytes", "unit", _settings.Unit == SizeUnit.Bytes, () => SetUnit(SizeUnit.Bytes)),
                Radio("_KB", "unit", _settings.Unit == SizeUnit.KB, () => SetUnit(SizeUnit.KB)),
                Radio("_MB", "unit", _settings.Unit == SizeUnit.MB, () => SetUnit(SizeUnit.MB)),
                Radio("_GB", "unit", _settings.Unit == SizeUnit.GB, () => SetUnit(SizeUnit.GB)),
                Radio("_TB", "unit", _settings.Unit == SizeUnit.TB, () => SetUnit(SizeUnit.TB))),
            Sub("_Decimals",
                Radio("_0", "dec", _settings.Decimals == 0, () => SetDecimals(0)),
                Radio("_1", "dec", _settings.Decimals == 1, () => SetDecimals(1)),
                Radio("_2", "dec", _settings.Decimals == 2, () => SetDecimals(2)),
                Radio("_3", "dec", _settings.Decimals == 3, () => SetDecimals(3))),
            Sub("S_ort",
                Radio("By _size", "sort", !_settings.SortByName, () => SetSort(false)),
                Radio("By _name", "sort", _settings.SortByName, () => SetSort(true))),
            Sub("_Expand", [.. expandItems]),
            new Separator(),
            Check("_Hide elements smaller than threshold", _settings.HideSmall, v =>
            {
                _settings.HideSmall = v;
                _tree.Rebuild();
            }),
            Item("Set size _threshold…", SetThresholdAsync),
            Check("Hide _empty folders", _settings.HideEmptyFolders, v =>
            {
                _settings.HideEmptyFolders = v;
                _tree.Rebuild();
            }),
            Check("List single _files in the tree", _settings.ListFilesInTree, v =>
            {
                _settings.ListFilesInTree = v;
                _tree.Rebuild();
            }),
            Check("_Group scans (\"All scans\" with totals)", _settings.GroupScans, v =>
            {
                _settings.GroupScans = v;
                UpdateGroup();
                _tree.Rebuild();
            }),
            Check("Show size _changes (after comparing)", false, v =>
            {
                _tree.ShowChanges = v;
                _tree.Rebuild();
            }),
            new Separator(),
            Item("Find _deepest nesting", FindDeepest),
            Item("_Find folder…", FindFolderAsync, new KeyGesture(Key.G, KeyModifiers.Control)),
            new Separator(),
            Check("Dri_ve list", _settings.ShowDriveList, v =>
            {
                _settings.ShowDriveList = v;
                ShowDriveList(v);
            }),
            Check("Status _bar", _settings.ShowStatusBar, v =>
            {
                _settings.ShowStatusBar = v;
                _statusBar.IsVisible = v;
            }),
            Sub("_Theme",
                Radio("_Dark", "theme", _settings.Theme == "Dark", () => SetTheme("Dark")),
                Radio("_Light", "theme", _settings.Theme == "Light", () => SetTheme("Light")),
                Radio("Follow _system", "theme", _settings.Theme is not ("Dark" or "Light"), () => SetTheme("System"))));

        var tools = Sub("_Tools",
            Item("File _search…", () => OpenSearch(null, null), new KeyGesture(Key.F, KeyModifiers.Control)),
            Item("_Large files", () => OpenSearch(null, Search.SearchTemplate.LargeFiles)),
            Item("_Old files", () => OpenSearch(null, Search.SearchTemplate.OldFiles)),
            Item("_Temporary files", () => OpenSearch(null, Search.SearchTemplate.TemporaryFiles)),
            Item("_Duplicate files", () => OpenSearch(null, Search.SearchTemplate.Duplicates)),
            Item("Files with long _paths", () => OpenSearch(null, Search.SearchTemplate.LongPaths)),
            new Separator(),
            Item("_Bulk rename…", () => OpenRenamer(SelectedPaths())),
            Sub("_Compress selection",
                Item("_NTFS (LZNT1)", () => CompressSelectedAsync(Native.CompressionFormat.Ntfs)),
                Item("XPRESS _4K", () => CompressSelectedAsync(Native.CompressionFormat.Xpress4K)),
                Item("XPRESS _8K", () => CompressSelectedAsync(Native.CompressionFormat.Xpress8K)),
                Item("XPRESS _16K", () => CompressSelectedAsync(Native.CompressionFormat.Xpress16K)),
                Item("_LZX", () => CompressSelectedAsync(Native.CompressionFormat.Lzx)),
                new Separator(),
                Item("_Uncompress", () => CompressSelectedAsync(Native.CompressionFormat.None))),
            Item("_Permissions of selection…", () =>
            {
                if (SelectedPaths().FirstOrDefault() is { } p && OperatingSystem.IsWindows())
                    _ = new Dialogs.PermissionsDialog(p).ShowDialog(this);
            }),
            Item("Search by _permissions…", () => OpenSearch(null, Search.SearchTemplate.Permissions)),
            new Separator(),
            Item("Empty _recycle bin", () =>
            {
                Native.Platform.EmptyRecycleBin(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
                _drives.Refresh();
            }),
            Item("Remove obsolete so_ftware", () => Shell.RunAndForget("control.exe", "appwiz.cpl")),
            Item("Configure system restore", () => Shell.RunAndForget("SystemPropertiesProtection.exe")),
            Item("_Map network drive…", () =>
            {
                Native.Platform.MapNetworkDrive(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
                _drives.Refresh();
            }),
            Item("Open command _prompt here", () =>
            {
                if (SelectedFolder is { } d)
                    Shell.CommandPrompt(d.FullPath);
            }),
            new Separator(),
            Item("O_ptions…", OpenOptions, new KeyGesture(Key.OemComma, KeyModifiers.Control)));

        var help = Sub("_Help",
            Item("_Read me (online)", () => Shell.Open("https://github.com/Taoism/ByteAtlas#readme")),
            Item("_Keyboard shortcuts", () => _ = Dialogs.MessageBox.ShowAsync(this, Shortcuts, title: "Keyboard shortcuts")),
            Item("_About ByteAtlas", () => _ = new Dialogs.AboutDialog().ShowDialog(this)));

        var m = new Menu();
        m.Items.Add(file);
        m.Items.Add(scan);
        m.Items.Add(view);
        m.Items.Add(tools);
        m.Items.Add(help);
        return m;
    }

    private const string Shortcuts = """
        Ctrl+O   Select folder to scan
        F5       Refresh selected folder (Shift: without sub folders, Ctrl: all scans)
        Esc      Stop the selected scan
        Ctrl+F   File search
        Ctrl+G   Find folder in the tree
        Ctrl+1…6 Expand to level      *  Expand all
        Ctrl+S   Save scan to index file
        Ctrl+P   Print report (PDF)
        Alt+←/→  Back / forward      Backspace  Parent folder
        Del      Delete (recycle bin)      Shift+Del  Delete for good
        Ctrl+C   Copy path
        """;

    private void AddKeyBindings()
    {
        void Bind(Key k, KeyModifiers m, Action a) => KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(k, m), Command = new RelayCommand(a) });
        Bind(Key.Escape, KeyModifiers.None, StopSelected);
        Bind(Key.Left, KeyModifiers.Alt, GoBack);
        Bind(Key.Right, KeyModifiers.Alt, GoForward);
        Bind(Key.Back, KeyModifiers.None, GoUp);
        Bind(Key.Delete, KeyModifiers.None, () => _ = DeleteSelectedAsync(true));
        Bind(Key.Delete, KeyModifiers.Shift, () => _ = DeleteSelectedAsync(false));
        Bind(Key.C, KeyModifiers.Control, () => _ = CopyPathsAsync());
    }

    private sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }

    private void SetMode(TreeValueMode m)
    {
        _settings.TreeMode = m;
        if (_modeCombo != null)
            _modeCombo.SelectedIndex = (int)m;
        _tree.Rebuild();
    }

    private void SetUnit(SizeUnit u)
    {
        _settings.Unit = u;
        if (_unitCombo != null)
            _unitCombo.SelectedIndex = (int)u;
        RefreshAll();
    }

    private void SetDecimals(int d)
    {
        _settings.Decimals = d;
        RefreshAll();
    }

    private void SetSort(bool byName)
    {
        _settings.SortByName = byName;
        _tree.Rebuild();
    }

    private void SetTheme(string t)
    {
        _settings.Theme = t;
        App.ApplyTheme(t);
    }

    /// <summary>Redraws everything after a unit / format change.</summary>
    private void RefreshAll()
    {
        _tree.Rebuild();
        UpdateViews(force: true);
        _drives.Refresh();
    }

    private void FillDriveMenu()
    {
        _scanDriveMenu.Items.Clear();
        foreach (var d in DriveListPanel.ReadyDrives())
        {
            var path = d.RootDirectory.FullName;
            var label = $"{path}  {(string.IsNullOrEmpty(d.VolumeLabel) ? "" : d.VolumeLabel)}";
            _scanDriveMenu.Items.Add(Item(label, () => StartScan(path)));
        }
        _scanDriveMenu.Items.Add(new Separator());
        _scanDriveMenu.Items.Add(Item("All _local drives", () =>
        {
            foreach (var d in DriveListPanel.ReadyDrives().Where(x => x.DriveType == DriveType.Fixed))
                StartScan(d.RootDirectory.FullName);
        }));
    }

    private void RefreshRecentMenu()
    {
        _recentMenu.Items.Clear();
        foreach (var p in _settings.Recent)
        {
            var path = p;
            _recentMenu.Items.Add(Item(path.Replace("_", "__"), () => StartScan(path)));
        }
        if (_settings.Recent.Count == 0)
            _recentMenu.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
        else
        {
            _recentMenu.Items.Add(new Separator());
            _recentMenu.Items.Add(Item("_Clear list", () =>
            {
                _settings.Recent.Clear();
                RefreshRecentMenu();
            }));
        }
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var r = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    }
}
