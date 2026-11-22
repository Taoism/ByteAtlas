using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ByteAtlas.Analysis;
using ByteAtlas.Export;
using ByteAtlas.Model;
using ByteAtlas.Operations;
using ByteAtlas.Scanning;
using ByteAtlas.Search;
using ByteAtlas.Views;

using ByteAtlas.Controls;

namespace ByteAtlas.Dialogs;

/// <summary>File search: templates (large, old, temporary, duplicate, long paths) and custom criteria, with bulk actions.</summary>
public sealed class SearchWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<ScanSession> _scans;
    private readonly ListBox _templates = new();
    private readonly TextBox _paths = new() { AcceptsReturn = true, Height = 64, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBox _include = new() { PlaceholderText = "e.g. *.iso;*.mkv (empty = all)" };
    private readonly TextBox _exclude = new() { PlaceholderText = "names or folders to leave out, e.g. node_modules;*.sys" };
    private readonly ComboBox _patternKind = new() { ItemsSource = new[] { "Wildcards", "Regular expression", "Whole name" }, SelectedIndex = 0 };
    private readonly TextBox _minSize = new() { PlaceholderText = "e.g. 100 MB", Width = 110 };
    private readonly TextBox _maxSize = new() { PlaceholderText = "no limit", Width = 110 };
    private readonly ComboBox _dateBasis = new() { ItemsSource = new[] { "Last modified", "Last accessed", "Created" }, SelectedIndex = 0 };
    private readonly NumericUpDown _olderDays = new() { Minimum = 0, Maximum = 36500, Value = 0, FormatString = "0", Width = 130 };
    private readonly NumericUpDown _newerDays = new() { Minimum = 0, Maximum = 36500, Value = 0, FormatString = "0", Width = 130 };
    private readonly TextBox _owner = new() { PlaceholderText = "part of the owner name" };
    private readonly NumericUpDown _pathLen = new() { Minimum = 0, Maximum = 32767, Value = 0, FormatString = "0", Width = 130 };
    private readonly CheckBox _hidden = new() { Content = "Hidden" };
    private readonly CheckBox _system = new() { Content = "System" };
    private readonly CheckBox _readOnly = new() { Content = "Read-only" };
    private readonly CheckBox _compressed = new() { Content = "Compressed" };
    private readonly CheckBox _folders = new() { Content = "Find folders too" };
    private readonly NumericUpDown _max = new() { Minimum = 0, Maximum = 10_000_000, Value = 0, FormatString = "0", Width = 130 };
    private readonly CheckBox _dupName = new() { Content = "Same name" };
    private readonly CheckBox _dupDate = new() { Content = "Same date" };
    private readonly ComboBox _dupHash = new() { ItemsSource = new[] { "Size only (no content check)", "MD5 checksum", "SHA-256 checksum" }, SelectedIndex = 1 };
    private readonly StackPanel _dupPanel;
    private readonly TextBox _permAccount = new() { PlaceholderText = "DOMAIN\\user, user or group" };
    private readonly ComboBox _permLevel = new() { ItemsSource = new[] { "Any access", "Read", "Write", "Modify", "Full control" }, SelectedIndex = 0 };
    private readonly CheckBox _permExplicit = new() { Content = "Only where granted explicitly (not inherited)", IsChecked = true };
    private readonly CheckBox _permFolders = new() { Content = "Folders only (files usually inherit; faster)", IsChecked = true };
    private readonly StackPanel _permPanel = new() { Spacing = 8 };
    private readonly DataGrid _grid;
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _start;
    private CancellationTokenSource? _cts;
    private List<ItemRow> _rows = [];
    private SearchCriteria _criteria = new();
    private bool _duplicates;

    private static readonly string[] ResultColumns = ["Name", "Size", "Allocated", "Modified", "Accessed", "Owner", "Folder"];

    public event EventHandler<Item>? ShowItem;
    public event EventHandler? FilesChanged;

    private static readonly (string Name, SearchTemplate T)[] Templates =
    [
        ("Large files", SearchTemplate.LargeFiles),
        ("Old files", SearchTemplate.OldFiles),
        ("Temporary files", SearchTemplate.TemporaryFiles),
        ("Duplicate files", SearchTemplate.Duplicates),
        ("Files with long paths", SearchTemplate.LongPaths),
        ("Permissions of a user or group", SearchTemplate.Permissions),
        ("Custom search", SearchTemplate.Custom),
    ];

    public SearchWindow(AppSettings settings, List<ScanSession> scans, List<string> paths, SearchTemplate? template, SearchCriteria? preset = null)
    {
        _settings = settings;
        _scans = scans;
        Title = "ByteAtlas File Search";
        Icon = MainWindow.AppIcon;
        Resizing.Remember(this, "search", 1250, 820, 800, 500);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _paths.Text = string.Join(Environment.NewLine, paths);

        _templates.ItemsSource = Templates.Select(t => t.Name).ToList();
        _templates.SelectionChanged += (_, _) =>
        {
            if (_templates.SelectedIndex >= 0)
                Load(SearchCriteria.FromTemplate(Templates[_templates.SelectedIndex].T));
        };

        _dupPanel = Row(_dupName, _dupDate, _dupHash);
        _start = new Button { Content = "Search", Classes = { "accent" }, MinWidth = 110 };
        _start.Click += async (_, _) => await StartAsync();

        var browse = new Button { Content = "Add folder…" };
        browse.Click += async (_, _) =>
        {
            var r = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Add folder", AllowMultiple = true });
            foreach (var f in r)
                if (f.TryGetLocalPath() is { } p)
                    _paths.Text = (_paths.Text ?? "").TrimEnd() + (string.IsNullOrWhiteSpace(_paths.Text) ? "" : Environment.NewLine) + p;
        };
        var drives = new Button { Content = "All local drives" };
        drives.Click += (_, _) => _paths.Text = string.Join(Environment.NewLine, DriveListPanel.ReadyDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName));

        var criteria = new StackPanel { Spacing = 8 };
        criteria.Children.Add(H("Search in"));
        criteria.Children.Add(_paths);
        criteria.Children.Add(Row(browse, drives));
        criteria.Children.Add(H("Names"));
        criteria.Children.Add(Field("Include:", _include));
        criteria.Children.Add(Field("Exclude:", _exclude));
        criteria.Children.Add(Field("Patterns are:", _patternKind));
        criteria.Children.Add(H("Size and date"));
        criteria.Children.Add(Row(Lbl("At least"), _minSize, Lbl("at most"), _maxSize));
        criteria.Children.Add(Field("Date:", _dateBasis));
        criteria.Children.Add(Row(Lbl("Older than (days)"), _olderDays));
        criteria.Children.Add(Row(Lbl("Newer than (days)"), _newerDays));
        criteria.Children.Add(H("More"));
        criteria.Children.Add(Field("Owner:", _owner));
        criteria.Children.Add(Row(Lbl("Path at least (chars)"), _pathLen));
        criteria.Children.Add(Row(_hidden, _system, _readOnly, _compressed));
        criteria.Children.Add(_folders);
        criteria.Children.Add(Row(Lbl("Max. results (0 = all)"), _max));
        criteria.Children.Add(H("Duplicates"));
        criteria.Children.Add(_dupPanel);
        _permPanel.Children.Add(Field("Account:", _permAccount));
        _permPanel.Children.Add(Field("At least:", _permLevel));
        _permPanel.Children.Add(_permExplicit);
        _permPanel.Children.Add(_permFolders);
        _permPanel.Children.Add(new TextBlock { Text = "Group memberships count (local groups, and domain groups when the PC is in a domain).", Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        criteria.Children.Add(H("Permissions"));
        criteria.Children.Add(_permPanel);

        var left = new DockPanel();
        var tpl = new Border { Classes = { "panel" }, Child = _templates, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(tpl, Dock.Top);
        left.Children.Add(tpl);
        var startRow = new Border { Padding = new Thickness(0, 8, 0, 0), Child = _start };
        DockPanel.SetDock(startRow, Dock.Bottom);
        left.Children.Add(startRow);
        left.Children.Add(new ScrollViewer { Content = criteria, Padding = new Thickness(0, 0, 10, 0) });

        _grid = GridFactory.Create(ResultColumns.Select(k => GridFactory.ItemColumns[k]));
        _grid.DoubleTapped += (_, _) =>
        {
            if (_grid.SelectedItem is ItemRow r)
                ShowItem?.Invoke(this, r.Item);
        };
        _grid.ContextMenu = ResultMenu();
        Resizing.RememberColumns(_grid, "search");
        DragOut.Attach(_grid, () => [.. _grid.SelectedItems.OfType<ItemRow>().Select(r => r.Path)]);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4 };
        actions.Children.Add(MainWindow.ToolButton("Delete…", () => DeleteAsync(true)));
        actions.Children.Add(MainWindow.ToolButton("Move / copy…", MoveCopyAsync));
        actions.Children.Add(MainWindow.ToolButton("Archive (zip)…", ArchiveAsync));
        actions.Children.Add(MainWindow.ToolButton("Bulk rename…", () => new RenameWindow(_settings, [.. Selected().Select(r => r.Path)]).Show(this)));
        actions.Children.Add(MainWindow.ToolButton("Select duplicates (keep first)", SelectDuplicates, "Selects every copy except the first of each group"));
        actions.Children.Add(MainWindow.ToolButton("Replace copies with hard links…", HardlinkAsync, "Keeps one file per group; the copies become hard links to it (same drive only)"));
        actions.Children.Add(MainWindow.ToolButton("Export…", ExportAsync));
        actions.Children.Add(MainWindow.ToolButton("Copy paths", async () =>
        {
            if (Clipboard != null)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(Clipboard, string.Join(Environment.NewLine, Selected().Select(r => r.Path)));
        }));

        var right = new DockPanel();
        var top = new Border { Padding = new Thickness(0, 0, 0, 6), Child = actions };
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        var bottom = new Border { Padding = new Thickness(0, 6, 0, 0), Child = _summary };
        DockPanel.SetDock(bottom, Dock.Bottom);
        right.Children.Add(bottom);
        right.Children.Add(_grid);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("380,10,*"), Margin = new Thickness(12) };
        body.Children.Add(left);
        Resizing.Split(body, 1, 0, columns: true, "search.left");
        Grid.SetColumn(right, 2);
        body.Children.Add(right);
        Content = body;

        if (preset != null)
        {
            _templates.SelectedIndex = Templates.Length - 1;
            Load(preset);
        }
        else
            _templates.SelectedIndex = template is { } t ? Array.FindIndex(Templates, x => x.T == t) : 0;
        if (template != null || preset != null)
            Opened += async (_, _) => await StartAsync();
    }

    private static TextBlock H(string t) => new() { Text = t, Classes = { "header" }, Margin = new Thickness(0, 6, 0, 0) };
    private static TextBlock Lbl(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(params Control[] c)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var x in c)
            sp.Children.Add(x);
        return sp;
    }

    private static Grid Field(string label, Control c)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
        g.Children.Add(Lbl(label));
        Grid.SetColumn(c, 1);
        g.Children.Add(c);
        return g;
    }

    private void Load(SearchCriteria c)
    {
        _criteria = c;
        _include.Text = string.Join(';', c.Include.Select(r => r.Pattern));
        _exclude.Text = string.Join(';', c.Exclude.Select(r => r.Pattern));
        _minSize.Text = c.MinSize is { } mn && mn > 1 ? SizeFormat.Format(mn, SizeUnit.Auto, 0) : "";
        _maxSize.Text = c.MaxSize is { } mx ? SizeFormat.Format(mx, SizeUnit.Auto, 0) : "";
        _dateBasis.SelectedIndex = (int)c.DateBasis;
        _olderDays.Value = c.OlderThan is { } o ? Math.Round((decimal)(DateTime.Now - o).TotalDays) : 0;
        _newerDays.Value = c.NewerThan is { } n ? Math.Round((decimal)(DateTime.Now - n).TotalDays) : 0;
        _owner.Text = c.Owner ?? "";
        _pathLen.Value = c.MinPathLength ?? 0;
        _folders.IsChecked = c.IncludeFolders;
        _max.Value = c.MaxResults;
        _dupName.IsChecked = c.DuplicatesSameName;
        _dupDate.IsChecked = c.DuplicatesSameDate;
        _dupHash.SelectedIndex = (int)c.DuplicatesHash;
        _dupPanel.IsEnabled = c.Template == SearchTemplate.Duplicates;
        _permPanel.IsEnabled = c.Template == SearchTemplate.Permissions;
        _permAccount.Text = c.PermissionAccount ?? "";
        _permLevel.SelectedIndex = (int)c.PermissionLevel;
        _permExplicit.IsChecked = c.PermissionExplicitOnly;
        _permFolders.IsChecked = c.PermissionFoldersOnly;
    }

    private SearchCriteria Read()
    {
        var kind = (PatternKind)Math.Max(0, _patternKind.SelectedIndex);
        var c = new SearchCriteria
        {
            Template = _criteria.Template,
            Name = _criteria.Name,
            SortBySizeDesc = _criteria.SortBySizeDesc,
            DateBasis = (AgeBasis)Math.Max(0, _dateBasis.SelectedIndex),
            Owner = string.IsNullOrWhiteSpace(_owner.Text) ? null : _owner.Text.Trim(),
            MinPathLength = _pathLen.Value is > 0 ? (int)_pathLen.Value : null,
            IncludeFolders = _folders.IsChecked == true,
            MaxResults = (int)(_max.Value ?? 0),
            DuplicatesSameName = _dupName.IsChecked == true,
            DuplicatesSameDate = _dupDate.IsChecked == true,
            DuplicatesHash = (HashKind)Math.Max(0, _dupHash.SelectedIndex),
        };
        if (_criteria.Template == SearchTemplate.Permissions)
        {
            c.PermissionAccount = string.IsNullOrWhiteSpace(_permAccount.Text) ? null : _permAccount.Text.Trim();
            c.PermissionLevel = (AccessLevel)Math.Max(0, _permLevel.SelectedIndex);
            c.PermissionExplicitOnly = _permExplicit.IsChecked == true;
            c.PermissionFoldersOnly = _permFolders.IsChecked == true;
        }
        if (!string.IsNullOrWhiteSpace(_include.Text))
            c.Include.Add(new FilterRule { Pattern = _include.Text.Trim(), Kind = kind, Exclude = false });
        if (!string.IsNullOrWhiteSpace(_exclude.Text))
        {
            c.Exclude.Add(new FilterRule { Pattern = _exclude.Text.Trim(), Kind = kind, Exclude = true });
            c.Exclude.Add(new FilterRule { Pattern = _exclude.Text.Trim(), Kind = kind, Exclude = true, Folders = true });
        }
        if (SizeFormat.TryParse(_minSize.Text ?? "", out var min))
            c.MinSize = min;
        else if (c.Template == SearchTemplate.Duplicates)
            c.MinSize = 1;
        if (SizeFormat.TryParse(_maxSize.Text ?? "", out var max))
            c.MaxSize = max;
        if (_olderDays.Value is > 0)
            c.OlderThan = DateTime.Now.AddDays(-(double)_olderDays.Value.Value);
        if (_newerDays.Value is > 0)
            c.NewerThan = DateTime.Now.AddDays(-(double)_newerDays.Value.Value);
        if (_hidden.IsChecked == true)
            c.AttributesSet |= FileAttributes.Hidden;
        if (_system.IsChecked == true)
            c.AttributesSet |= FileAttributes.System;
        if (_readOnly.IsChecked == true)
            c.AttributesSet |= FileAttributes.ReadOnly;
        if (_compressed.IsChecked == true)
            c.AttributesSet |= FileAttributes.Compressed;
        return c;
    }

    private async Task StartAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            return;
        }
        var c = Read();
        _duplicates = c.Template == SearchTemplate.Duplicates;
        var paths = (_paths.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (paths.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Add at least one drive or folder to search in.", MessageKind.Warning, "File search");
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _start.Content = "Stop";
        _summary.Text = "Searching…";
        var progress = new Progress<SearchProgress>(p => _summary.Text = p.Total > 0 ? $"{p.Phase}: {p.Done:N0} of {p.Total:N0}" : p.Phase);
        try
        {
            var results = await Task.Run(async () =>
            {
                // paths already scanned are searched in memory; the rest is scanned first
                var toScan = new List<string>();
                var found = new List<SearchResult>();
                var owners = new Dictionary<Item, ScanSession>();
                foreach (var p in paths)
                {
                    var norm = ScanSession.NormalizeRoot(p);
                    var scan = _scans.FirstOrDefault(s => s.State == ScanState.Done && norm.StartsWith(s.Root.FullPath, StringComparison.OrdinalIgnoreCase));
                    var node = scan == null ? null : norm.Length <= scan.Root.FullPath.Length ? scan.Root : scan.Root.FindRelative(norm[scan.Root.FullPath.Length..]);
                    if (scan != null && node != null)
                    {
                        var r = SearchEngine.Search(scan, node, c, ct);
                        foreach (var x in r)
                            owners[x.Item] = scan;
                        found.AddRange(r);
                    }
                    else
                        toScan.Add(p);
                }
                if (toScan.Count > 0)
                    found.AddRange(await SearchEngine.SearchPathsAsync(toScan, c, _settings.Scan, progress, ct));
                if (_duplicates)
                    found = SearchEngine.FindDuplicates(found, c, progress, ct);
                else if (c.SortBySizeDesc)
                    found.Sort((a, b) => b.Size.CompareTo(a.Size));
                return found.Select(r => new ItemRow(r.Item, owners.GetValueOrDefault(r.Item), _settings, r.FullPath) { Group = r.Group, Hash = r.Hash ?? "", Detail = r.Detail }).ToList();
            }, ct);
            ShowResults(results);
        }
        catch (OperationCanceledException)
        {
            _summary.Text = "Stopped.";
        }
        catch (ArgumentException e)
        {
            _summary.Text = e.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _start.Content = "Search";
        }
    }

    private void ShowResults(List<ItemRow> rows)
    {
        _rows = rows;
        _grid.Columns.Clear();
        var cols = _duplicates
            ? new[] { "Group", "Name", "Size", "Modified", "Folder", "Hash" }
            : _criteria.Template == SearchTemplate.Permissions
                ? new[] { "Name", "Detail", "Inherited", "Size", "Path" }
                : new[] { "Name", "Size", "Allocated", "Modified", "Accessed", "Owner", "PathLength", "Folder" };
        foreach (var k in cols)
            _grid.Columns.Add(GridFactory.Column(GridFactory.ItemColumns[k]));
        _grid.ItemsSource = rows;
        var total = rows.Sum(r => r.Size);
        if (_duplicates)
        {
            var groups = rows.GroupBy(r => r.Group).ToList();
            long wasted = 0;
            foreach (var g in groups)
                wasted += g.Skip(1).Sum(r => r.Size);
            _summary.Text = $"{rows.Count:N0} duplicate files in {groups.Count:N0} groups  ·  {_settings.Size(wasted)} could be freed";
        }
        else
            _summary.Text = $"{rows.Count:N0} results  ·  {_settings.Size(total)}";
    }

    private List<ItemRow> Selected()
    {
        var s = _grid.SelectedItems.OfType<ItemRow>().ToList();
        return s;
    }

    private ContextMenu ResultMenu()
    {
        var m = new ContextMenu();
        void Add(string h, Action a)
        {
            var mi = new MenuItem { Header = h };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        Add("Open", () =>
        {
            foreach (var r in Selected().Take(10))
                Shell.Open(r.Path);
        });
        Add("Show in Explorer", () =>
        {
            if (Selected().FirstOrDefault() is { } r)
                Shell.Reveal(r.Path);
        });
        Add("Show in tree", () =>
        {
            if (Selected().FirstOrDefault() is { } r)
                ShowItem?.Invoke(this, r.Item);
        });
        Add("Properties", () =>
        {
            if (Selected().FirstOrDefault() is { } r)
                Shell.Properties(r.Path);
        });
        Add("Delete (recycle bin)", () => _ = DeleteAsync(true));
        Add("Delete for good", () => _ = DeleteAsync(false));
        return m;
    }

    private void SelectDuplicates()
    {
        _grid.SelectedItems.Clear();
        foreach (var g in _rows.GroupBy(r => r.Group))
            foreach (var r in g.Skip(1))
                _grid.SelectedItems.Add(r);
    }

    private async Task DeleteAsync(bool recycle)
    {
        var sel = Selected();
        if (sel.Count == 0)
            return;
        if (_duplicates && _rows.GroupBy(r => r.Group).Any(g => g.All(sel.Contains)))
        {
            if (!await MessageBox.ConfirmAsync(this, "Every copy of at least one group is selected: nothing of those files would be left. Delete anyway?", "Delete all", "File search"))
                return;
        }
        if (!await MessageBox.ConfirmAsync(this, $"{(recycle ? "Move" : "Delete")} {sel.Count} files ({_settings.Size(sel.Sum(r => r.Size))}){(recycle ? " to the recycle bin" : " for good")}?", recycle ? "Move to recycle bin" : "Delete", "File search"))
            return;
        var r = await Task.Run(() => FileOperations.Delete([.. sel.Select(x => x.Path)], recycle), CancellationToken.None);
        Done(r, "Deleted", sel);
    }

    private async Task MoveCopyAsync()
    {
        var sel = Selected();
        if (sel.Count == 0)
            return;
        var dlg = new MoveCopyDialog(sel.Count, _settings.Size(sel.Sum(r => r.Size)));
        if (!await dlg.ShowDialog<bool>(this))
            return;
        var r = await Task.Run(() => FileOperations.MoveOrCopy([.. sel.Select(x => x.Path)], dlg.Target, dlg.Move, dlg.KeepStructure, null, dlg.Collision, null, CancellationToken.None), CancellationToken.None);
        Done(r, dlg.Move ? "Moved" : "Copied", dlg.Move ? sel : []);
    }

    private async Task ArchiveAsync()
    {
        var sel = Selected().Where(r => r.Item is FileItem).ToList();
        if (sel.Count == 0)
            return;
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Archive to",
            SuggestedFileName = "archive.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Zip archive") { Patterns = ["*.zip"] }],
        });
        if (f?.TryGetLocalPath() is not { } zip)
            return;
        var delete = await MessageBox.ConfirmAsync(this, "Delete the original files once they are in the archive?", "Delete originals", "Archive");
        var r = await Task.Run(() => FileOperations.Archive([.. sel.Select(x => x.Path)], zip, null, delete, CancellationToken.None), CancellationToken.None);
        Done(r, "Archived", delete ? sel : []);
    }

    private async Task HardlinkAsync()
    {
        if (!_duplicates || _rows.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Run a duplicate search first.", MessageKind.Info, "Hard links");
            return;
        }
        var groups = _rows.GroupBy(r => r.Group).ToList();
        var copies = groups.Sum(g => g.Count() - 1);
        if (!await MessageBox.ConfirmAsync(this,
            $"Replace {copies} copies in {groups.Count} groups with hard links to the first file of each group?\n\nAll names stay, the content is stored once. Changing one of the files later changes them all. Only works on NTFS, within one drive.",
            "Replace", "Hard links"))
            return;
        var result = await Task.Run(() =>
        {
            int done = 0, failed = 0;
            var errors = new List<string>();
            foreach (var g in groups)
            {
                var list = g.ToList();
                var r = FileOperations.ReplaceWithHardlinks(list[0].Path, [.. list.Skip(1).Select(x => x.Path)]);
                done += r.Done;
                failed += r.Failed;
                errors.AddRange(r.Errors);
            }
            return new OperationResult(done, failed, errors);
        }, CancellationToken.None);
        Done(result, "Replaced with hard links", []);
    }

    private void Done(OperationResult r, string verb, List<ItemRow> gone)
    {
        _summary.Text = $"{verb}: {r.Done}{(r.Failed > 0 ? $", {r.Failed} failed" : "")}.";
        if (gone.Count > 0)
        {
            var left = _rows.Where(x => !gone.Contains(x) || File.Exists(x.Path)).ToList();
            ShowResults(left);
        }
        if (r.Failed > 0)
            _ = MessageBox.ShowAsync(this, string.Join("\n", r.Errors.Take(25)), MessageKind.Warning, verb);
        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExportAsync()
    {
        if (_rows.Count == 0)
            return;
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export results",
            SuggestedFileName = "search-results.xlsx",
            DefaultExtension = "xlsx",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Excel") { Patterns = ["*.xlsx"] },
                new Avalonia.Platform.Storage.FilePickerFileType("CSV") { Patterns = ["*.csv"] },
                new Avalonia.Platform.Storage.FilePickerFileType("HTML") { Patterns = ["*.html"] },
                new Avalonia.Platform.Storage.FilePickerFileType("PDF") { Patterns = ["*.pdf"] },
                new Avalonia.Platform.Storage.FilePickerFileType("Text") { Patterns = ["*.txt"] },
            ],
        });
        if (f?.TryGetLocalPath() is not { } path)
            return;
        var rows = Selected().Count > 1 ? Selected() : _rows;
        var t = ExportBuilder.Items(_criteria.Name, rows.Select(r => r.Item), null,
            [TreeColumn.Name, TreeColumn.Size, TreeColumn.Allocated, TreeColumn.Modified, TreeColumn.Accessed],
            i =>
            {
                var r = rows.First(x => ReferenceEquals(x.Item, i));
                return _duplicates ? [r.Path, (long)r.Group, r.Hash] : [r.Path, r.Owner];
            },
            _duplicates
                ? [new ExportColumn("Path", ColumnKind.Text), new ExportColumn("Group", ColumnKind.Number), new ExportColumn("Checksum", ColumnKind.Text)]
                : [new ExportColumn("Path", ColumnKind.Text), new ExportColumn("Owner", ColumnKind.Text)]);
        t.Subtitle.Add(_summary.Text ?? "");
        var o = _settings.Export.Clone();
        o.Unit = _settings.Unit;
        await Task.Run(() => Exporters.Write(t, path, o), CancellationToken.None);
        _summary.Text = "Exported: " + path;
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
