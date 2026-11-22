using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Layout;
using ByteAtlas.Analysis;
using ByteAtlas.Export;
using ByteAtlas.Model;
using ByteAtlas.Scanning;

using ByteAtlas.Controls;

namespace ByteAtlas.Dialogs;

/// <summary>All settings, on pages: View, Directory tree, File groups, Age of files, Charts, Scan, Filter, Export, System.</summary>
public sealed class OptionsDialog : DialogWindow
{
    private readonly AppSettings _s;
    private readonly List<Action> _apply = [];
    private readonly ListBox _pages = new();
    private readonly ContentControl _page = new();
    private readonly Dictionary<string, Control> _content = [];

    public OptionsDialog(AppSettings settings, string? page) : base("Options", 940)
    {
        _s = settings;
        Add("View", ViewPage());
        Add("Directory tree", TreePage());
        Add("File groups", GroupsPage());
        Add("Age of files", AgePage());
        Add("Charts", ChartsPage());
        Add("Scan", ScanPage());
        Add("Filter", FilterPage());
        Add("Export", ExportPage());
        Add("System", SystemPage());
        Resizing.Remember(this, "options", 960, 700, 640, 420);
        _pages.ItemsSource = _content.Keys.ToList();
        _pages.SelectionChanged += (_, _) =>
        {
            if (_pages.SelectedItem is string k)
                _page.Content = new ScrollViewer { Content = _content[k], Padding = new Thickness(0, 0, 12, 0) };
        };
        _pages.SelectedItem = page != null && _content.ContainsKey(page) ? page : "View";

        var import = MainWindow.ToolButton("Import…", ImportAsync);
        var export = MainWindow.ToolButton("Export…", ExportAsync);
        var reset = MainWindow.ToolButton("Reset all", async () =>
        {
            if (await MessageBox.ConfirmAsync(this, "Reset every option to its default?", "Reset", "Options"))
            {
                Copy(new AppSettings(), _s);
                Close(true);
            }
        });
        var side = new DockPanel();
        var tools = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        tools.Children.Add(import);
        tools.Children.Add(export);
        tools.Children.Add(reset);
        DockPanel.SetDock(tools, Dock.Bottom);
        side.Children.Add(tools);
        side.Children.Add(new Border { Classes = { "panel" }, Child = _pages });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("190,10,*") };
        grid.Children.Add(side);
        Resizing.Split(grid, 1, 0, columns: true, "options.pages");
        Grid.SetColumn(_page, 2);
        grid.Children.Add(_page);
        SetBody(grid);
    }

    private void Add(string name, Control c) => _content[name] = c;

    protected override Task<bool> ValidateAsync()
    {
        foreach (var a in _apply)
            a();
        return Task.FromResult(true);
    }

    // ---------- builders ----------

    private static StackPanel PageOf(params Control[] c)
    {
        var sp = new StackPanel { Spacing = 8 };
        foreach (var x in c)
            sp.Children.Add(x);
        return sp;
    }

    private static TextBlock H(string t) => new() { Text = t, Classes = { "header" }, Margin = new Thickness(0, 8, 0, 0) };

    private CheckBox Check(string text, bool value, Action<bool> set, string? tip = null)
    {
        var c = new CheckBox { Content = text, IsChecked = value };
        if (tip != null)
            ToolTip.SetTip(c, tip);
        _apply.Add(() => set(c.IsChecked == true));
        return c;
    }

    private Control Number(string label, double value, double min, double max, Action<double> set, string format = "0")
    {
        var n = new NumericUpDown { Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, FormatString = format, Width = 140 };
        _apply.Add(() => set((double)(n.Value ?? (decimal)value)));
        return Row(8, Label(label), n);
    }

    private Control SizeBox(string label, long value, Action<long> set)
    {
        var t = new TextBox { Text = SizeFormat.Format(value), Width = 140 };
        _apply.Add(() =>
        {
            if (SizeFormat.TryParse(t.Text ?? "", out var v))
                set(v);
        });
        return Row(8, Label(label), t);
    }

    private Control TextField(string label, string value, Action<string> set, double width = 300)
    {
        var t = new TextBox { Text = value, Width = width };
        _apply.Add(() => set(t.Text ?? ""));
        return Row(8, Label(label), t);
    }

    private Control Choice(string label, string[] items, int index, Action<int> set)
    {
        var c = new ComboBox { ItemsSource = items, SelectedIndex = index, MinWidth = 200 };
        _apply.Add(() => set(Math.Max(0, c.SelectedIndex)));
        return Row(8, Label(label), c);
    }

    // ---------- pages ----------

    private Control ViewPage() => PageOf(
        H("Appearance"),
        Choice("Theme:", ["Dark", "Light", "Follow the system"], _s.Theme switch { "Dark" => 0, "Light" => 1, _ => 2 }, i => _s.Theme = i switch { 0 => "Dark", 1 => "Light", _ => "System" }),
        Check("Compact line height", _s.CompactRows, v => _s.CompactRows = v),
        H("Numbers"),
        Choice("Size unit:", ["Automatic", "Bytes", "KB", "MB", "GB", "TB"], (int)_s.Unit, i => _s.Unit = (SizeUnit)i),
        Number("Decimals:", _s.Decimals, 0, 3, v => _s.Decimals = (int)v),
        TextField("Date / time format:", _s.DateFormat, v => _s.DateFormat = v, 200),
        Label("Empty = the Windows format. Examples: yyyy-MM-dd HH:mm, dd.MM.yyyy", true),
        H("Costs"),
        Number("Storage costs per GB:", _s.CostPerGb, 0, 100000, v => _s.CostPerGb = v, "0.####"),
        TextField("Currency:", _s.Currency, v => _s.Currency = v, 80),
        Label("With a price set, the overview and the Costs column show what the allocated space costs.", true),
        H("Scanning"),
        Check("Update the views while scanning", _s.UpdateDuringScan, v => _s.UpdateDuringScan = v),
        Check("Show the error list after a scan with errors", _s.ShowErrorsAfterScan, v => _s.ShowErrorsAfterScan = v),
        Check("Bring the window to the front when a scan finishes", _s.NotifyWhenDone, v => _s.NotifyWhenDone = v),
        Number("Entries in \"Recently scanned\":", _s.RecentMax, 0, 50, v => _s.RecentMax = (int)v));

    private Control TreePage() => PageOf(
        H("Directory tree"),
        Check("Gradient bars as size indicators", _s.ShowGradientBars, v => _s.ShowGradientBars = v),
        Check("Bold for elements larger than the size below", _s.BoldLarge, v => _s.BoldLarge = v),
        SizeBox("Bold from:", _s.BoldThreshold, v => _s.BoldThreshold = v),
        Check("NTFS-compressed elements in blue", _s.CompressedBlue, v => _s.CompressedBlue = v),
        Check("Files with long paths in red", _s.LongPathRed, v => _s.LongPathRed = v),
        Number("Long path from (characters):", _s.LongPathLength, 50, 32767, v => _s.LongPathLength = (int)v),
        Check("List single files below the [Files] rows", _s.ListFilesInTree, v => _s.ListFilesInTree = v),
        Check("Hide empty folders", _s.HideEmptyFolders, v => _s.HideEmptyFolders = v),
        SizeBox("Size threshold (hide / expand):", _s.SizeThreshold, v => _s.SizeThreshold = v),
        Check("Show drive names in the drive list", _s.ShowDriveNames, v => _s.ShowDriveNames = v),
        Check("Show the drive list", _s.ShowDriveList, v => _s.ShowDriveList = v),
        Check("Show the status bar", _s.ShowStatusBar, v => _s.ShowStatusBar = v));

    private Control GroupsPage()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            Height = 380,
            Classes = { "mono" },
            Text = string.Join(Environment.NewLine, _s.FileGroups.Select(g => $"{g.Key} = {g.Value}")),
        };
        _apply.Add(() =>
        {
            var d = new Dictionary<string, string>();
            foreach (var line in (box.Text ?? "").Split('\n'))
            {
                var i = line.IndexOf('=');
                if (i > 0)
                    d[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
            if (d.Count > 0)
                _s.FileGroups = d;
        });
        var reset = MainWindow.ToolButton("Default groups", () => box.Text = string.Join(Environment.NewLine, new FileGroups().Groups.Select(g => $"{g.Key} = {g.Value}")));
        return PageOf(H("File groups"), Label("One group per line: name = extensions separated by ';'. Used by \"Group file types\", the treemap colours and the Description column.", true), box, reset);
    }

    private Control AgePage()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            Height = 220,
            Classes = { "mono" },
            Text = string.Join(Environment.NewLine, _s.AgeIntervals.Select(a => $"{a.Label} = {(a.Days <= 0 ? "" : a.Days.ToString(System.Globalization.CultureInfo.InvariantCulture))}")),
        };
        _apply.Add(() =>
        {
            var list = new List<AppSettings.AgeInterval>();
            foreach (var line in (box.Text ?? "").Split('\n'))
            {
                var i = line.IndexOf('=');
                if (i <= 0)
                    continue;
                double.TryParse(line[(i + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var days);
                list.Add(new AppSettings.AgeInterval { Label = line[..i].Trim(), Days = days });
            }
            if (list.Count > 0)
            {
                list = [.. list.OrderBy(a => a.Days <= 0 ? double.MaxValue : a.Days)];
                _s.AgeIntervals = list;
            }
        });
        return PageOf(H("Age of files"),
            Label("One interval per line: label = upper age in days. Leave the number empty for the last (open) interval.", true),
            box,
            Choice("Date used:", ["Last modified", "Last accessed", "Created"], (int)_s.AgeBasis, i => _s.AgeBasis = (AgeBasis)i));
    }

    private Control ChartsPage() => PageOf(
        H("Pie and bar charts"),
        Check("Show legend", _s.ChartLegend, v => _s.ChartLegend = v),
        Check("Show labels", _s.ChartLabels, v => _s.ChartLabels = v),
        Check("Show free space (drives)", _s.ChartFreeSpace, v => _s.ChartFreeSpace = v),
        Check("Show grid", _s.ChartGrid, v => _s.ChartGrid = v),
        Number("Merge slices below (%) into \"Other\":", _s.ChartOtherPercent, 0, 20, v => _s.ChartOtherPercent = v, "0.#"),
        H("Treemap"),
        Check("Show the full hierarchy (folder frames)", _s.TreemapHierarchy, v => _s.TreemapHierarchy = v),
        Check("Show single files as tiles", _s.TreemapFiles, v => _s.TreemapFiles = v),
        Check("Colour by file type (else by level)", _s.TreemapColorByType, v => _s.TreemapColorByType = v),
        Number("Level of detail (folder levels):", _s.TreemapDepth, 1, 20, v => _s.TreemapDepth = (int)v));

    private Control ScanPage()
    {
        var o = _s.Scan;
        return PageOf(
            H("Method"),
            Choice("Read drives with:", ["Automatic (master file table when possible)", "Folder walk only", "Master file table only"], (int)o.Method, i => o.Method = (ScanMethod)i),
            Label("The NTFS master file table is read directly on local NTFS drives when ByteAtlas runs as administrator: a whole drive in seconds.", true),
            Number("Threads for the folder walk (0 = automatic):", o.Threads, 0, 32, v => o.Threads = (int)v),
            Choice("Thread priority:", ["Lowest", "Below normal", "Normal", "Above normal", "Highest"], (int)o.Priority, i => o.Priority = (ThreadPriority)i),
            H("Accuracy"),
            Check("Follow mount points and directory links", o.FollowMountPoints, v => o.FollowMountPoints = v),
            Check("Track NTFS hard links and alternate data streams (count shared files once)", o.TrackHardlinksAndStreams, v => o.TrackHardlinksAndStreams = v, "Slower on the folder walk; free with the master file table"),
            Check("Folder dates from the files inside only", o.FolderDateFromFilesOnly, v => o.FolderDateFromFilesOnly = v),
            Check("Count Windows compact compression (XPRESS / LZX) on the folder walk (slower)", o.DetectCompactCompression, v => o.DetectCompactCompression = v,
                "Windows hides compact compression from the normal file attributes, so the folder walk has to ask every file. The master file table always knows."),
            H("Statistics"),
            Check("Statistics on file owners (slower on the folder walk)", o.CollectOwners, v => o.CollectOwners = v),
            Label("File type and age statistics are always available; they are computed for the selected folder when you open their tab.", true));
    }

    private Control FilterPage()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            Height = 260,
            Classes = { "mono" },
            Text = string.Join(Environment.NewLine, _s.Scan.Filters.Select(f => $"{(f.Enabled ? "" : "#")}{(f.Exclude ? "-" : "+")}{(f.Folders ? "dir:" : "")}{(f.Kind == PatternKind.Regex ? "re:" : f.Kind == PatternKind.WholeName ? "name:" : "")}{f.Pattern}")),
        };
        _apply.Add(() =>
        {
            var list = new List<FilterRule>();
            foreach (var raw in (box.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var line = raw;
                var rule = new FilterRule();
                if (line.StartsWith('#'))
                {
                    rule.Enabled = false;
                    line = line[1..];
                }
                if (line.StartsWith('+') || line.StartsWith('-'))
                {
                    rule.Exclude = line[0] == '-';
                    line = line[1..];
                }
                if (line.StartsWith("dir:", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Folders = true;
                    line = line[4..];
                }
                if (line.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Kind = PatternKind.Regex;
                    line = line[3..];
                }
                else if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Kind = PatternKind.WholeName;
                    line = line[5..];
                }
                rule.Pattern = line;
                if (line.Length > 0)
                    list.Add(rule);
            }
            _s.Scan.Filters = list;
        });
        return PageOf(H("Include / exclude"),
            Label("One rule per line, applied while scanning (rescan to apply):\n" +
                  "  -*.tmp            leave out files matching *.tmp\n" +
                  "  +*.mkv;*.mp4      count only these files\n" +
                  "  -dir:node_modules leave out folders with this name\n" +
                  "  -re:^~\\$          regular expression\n" +
                  "  -name:Thumbs.db   exact name\n" +
                  "  #-*.log           disabled rule", true),
            box);
    }

    private Control ExportPage()
    {
        var e = _s.Export;
        var cols = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 4 };
        foreach (var c in Enum.GetValues<TreeColumn>())
        {
            var col = c;
            var cb = new CheckBox { Content = ExportBuilder.ColumnName(c), IsChecked = e.Columns.Contains(c) };
            _apply.Add(() =>
            {
                if (cb.IsChecked == true && !e.Columns.Contains(col))
                    e.Columns.Add(col);
                if (cb.IsChecked != true)
                    e.Columns.Remove(col);
            });
            cols.Children.Add(cb);
        }
        _apply.Add(() =>
        {
            e.Columns.Remove(TreeColumn.Name);
            e.Columns.Insert(0, TreeColumn.Name);
        });
        return PageOf(
            H("Reports (Excel, PDF, HTML, CSV, text, print, email)"),
            Number("Folder levels (0 = all):", e.MaxDepth, 0, 100, v => e.MaxDepth = (int)v),
            Check("List single files", e.IncludeFiles, v => e.IncludeFiles = v),
            SizeBox("Leave out items smaller than:", e.MinSize, v => e.MinSize = v),
            TextField("Report title (empty = path):", e.Title ?? "", v => e.Title = v.Length == 0 ? null : v),
            H("Columns"),
            cols,
            H("CSV and Excel"),
            TextField("CSV separator:", e.CsvSeparator.ToString(), v => e.CsvSeparator = v.Length > 0 ? (v == "\\t" ? '\t' : v[0]) : ';', 60),
            Check("Sizes as plain byte numbers (CSV / Excel)", e.RawNumbers, v => e.RawNumbers = v),
            TextField("Default export folder:", _s.ExportFolder, v => _s.ExportFolder = v),
            H("Email"),
            Check("Put the report in the mail text", _s.MailBody, v => _s.MailBody = v),
            Check("Attach the report (Excel)", _s.MailAttach, v => _s.MailAttach = v));
    }

    private Control SystemPage()
    {
        var page = PageOf(
            H("On start"),
            Choice("Without a path on the command line:", ["Do nothing", "Scan the same paths as last time", "Always scan these paths"], (int)_s.Startup, i => _s.Startup = (StartupAction)i),
            TextField("Paths (separated by ';'):", string.Join(';', _s.StartupPaths), v => _s.StartupPaths = [.. v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], 420));
        if (OperatingSystem.IsWindows())
        {
            page.Children.Add(H("Explorer context menu (current user)"));
            page.Children.Add(Check("\"Analyze with ByteAtlas\" on folders and drives", Shell.ContextMenuInstalled(), v => { if (OperatingSystem.IsWindows()) Shell.SetContextMenu(false, v); }));
            page.Children.Add(Check("\"Search with ByteAtlas\" on folders and drives", Shell.ContextMenuInstalled(true), v => { if (OperatingSystem.IsWindows()) Shell.SetContextMenu(true, v); }));
            page.Children.Add(Label("On Windows 11 these entries are under \"Show more options\" (Shift+F10).", true));
        }
        page.Children.Add(H("Data"));
        page.Children.Add(Label("Settings: " + AppSettings.Folder, true));
        page.Children.Add(Label("History: " + (string.IsNullOrEmpty(_s.HistoryFile) ? Storage.HistoryStore.DefaultFile : _s.HistoryFile), true));
        return page;
    }

    // ---------- import / export ----------

    private async Task ExportAsync()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export options",
            SuggestedFileName = "ByteAtlas-options.json",
            DefaultExtension = "json",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Options") { Patterns = ["*.json"] }],
        });
        if (f?.TryGetLocalPath() is { } p)
        {
            foreach (var a in _apply)
                a();
            _s.Save(p);
        }
    }

    private async Task ImportAsync()
    {
        var f = await PickFileAsync("Import options", new Avalonia.Platform.Storage.FilePickerFileType("Options") { Patterns = ["*.json"] });
        if (f == null)
            return;
        Copy(AppSettings.Load(f), _s);
        Close(true);
    }

    private static void Copy(AppSettings from, AppSettings to)
    {
        foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
            p.SetValue(to, p.GetValue(from));
    }
}
