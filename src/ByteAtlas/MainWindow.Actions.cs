using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using ByteAtlas.Analysis;
using ByteAtlas.Controls;
using ByteAtlas.Dialogs;
using ByteAtlas.Export;
using ByteAtlas.Model;
using ByteAtlas.Operations;
using ByteAtlas.Scanning;
using ByteAtlas.Search;
using ByteAtlas.Storage;
using ByteAtlas.Views;

namespace ByteAtlas;

public sealed partial class MainWindow
{
    // ---------------- context menus ----------------

    private ContextMenu TreeContextMenu()
    {
        var m = ItemContextMenu(() => _tree.SelectedRows.Where(r => r.Kind != RowKind.FilesGroup).Select(r => r.Item).ToList());
        m.Items.Insert(0, new Separator());
        m.Items.Insert(0, Item("Refresh this folder", () => RefreshSelected(true)));
        m.Items.Insert(0, Sub("Expand",
            Item("To level 1", () => ExpandSelected(1)),
            Item("To level 2", () => ExpandSelected(2)),
            Item("To level 3", () => ExpandSelected(3)),
            Item("Full expand", () => ExpandSelected(0)),
            Item("Collapse", () =>
            {
                if (SelectedFolder is { } d)
                    _tree.Collapse(d);
            })));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Set title…", SetTitleAsync));
        m.Items.Add(Item("Remove scan", RemoveSelectedScan));
        return m;
    }

    private ContextMenu ItemContextMenu(Func<List<Item>> selection)
    {
        List<Item> items() => LocalOnly(selection());
        List<string> Paths() => [.. items().Select(i => i.FullPath)];
        var m = new ContextMenu();
        if (OperatingSystem.IsWindows())
        {
            // Shift + right click: the Windows Explorer menu straight away
            m.Opening += (_, e) =>
            {
                if (OperatingSystem.IsWindows() && ShellMenu.ShiftDown && Paths() is { Count: > 0 } p)
                {
                    e.Cancel = true;
                    ShowExplorerMenu(p);
                }
            };
            m.Items.Add(Item("Explorer menu…", () => ShowExplorerMenu(Paths())));
            m.Items.Add(new Separator());
        }
        m.Items.Add(Item("Open", () =>
        {
            foreach (var p in Paths().Take(10))
                Shell.OpenItem(p);
        }));
        m.Items.Add(Item("Show in Explorer", () =>
        {
            if (Paths().FirstOrDefault() is { } p)
                Shell.Reveal(p);
        }));
        m.Items.Add(Item("Properties", () =>
        {
            if (Paths().FirstOrDefault() is { } p)
                Shell.Properties(p);
        }));
        m.Items.Add(Item("Open command prompt here", () =>
        {
            if (items().FirstOrDefault() is { } i)
                Shell.CommandPrompt(i is DirNode ? i.FullPath : i.Parent?.FullPath ?? "");
        }));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Copy path", () => CopyTextAsync(string.Join(Environment.NewLine, Paths()))));
        m.Items.Add(Item("Copy list of files", () => CopyTextAsync(string.Join(Environment.NewLine, items().SelectMany(i => i is DirNode d ? d.AllFiles().Select(f => f.FullPath) : [i.FullPath])))));
        m.Items.Add(Item("Search here…", () =>
        {
            if (items().FirstOrDefault() is DirNode d)
                OpenSearch([d.FullPath], null);
        }));
        m.Items.Add(new Separator());
        m.Items.Add(Item($"Delete ({Shell.TrashName})", () => DeleteItemsAsync(items(), true)));
        m.Items.Add(Item("Delete for good…", () => DeleteItemsAsync(items(), false)));
        m.Items.Add(Item("Move / copy…", () => MoveCopyAsync(items())));
        m.Items.Add(Item("Rename…", () => RenameAsync(items().FirstOrDefault())));
        m.Items.Add(Item("Bulk rename…", () => OpenRenamer(Paths())));
        m.Items.Add(Item("New folder…", () => NewFolderAsync(items().FirstOrDefault())));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Permissions…", () =>
        {
            if (items().FirstOrDefault() is { } i && OperatingSystem.IsWindows())
                _ = new PermissionsDialog(i.FullPath).ShowDialog(this);
        }));
        m.Items.Add(CompressMenu(items));
        m.Items.Add(Item("Filter by this extension", () =>
        {
            var exts = items().OfType<FileItem>().Select(f => f.Extension).Distinct().ToList();
            if (exts.Count > 0)
                ApplyFilter(ViewFilter.ForExtensions(exts));
        }));
        m.Items.Add(Item("Filter by this owner", () =>
        {
            if (items().FirstOrDefault() is { OwnerId: >= 0 } i && SelectedScan is { } s)
                ApplyFilter(ViewFilter.ForOwners([i.OwnerId], s.Owners.Name(i.OwnerId)));
        }));
        return m;
    }

    private void ShowExplorerMenu(List<string> paths)
    {
        if (!OperatingSystem.IsWindows() || paths.Count == 0 || TryGetPlatformHandle()?.Handle is not { } hwnd)
            return;
        // after our own menu has closed
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (OperatingSystem.IsWindows())
                ShellMenu.Show(hwnd, paths);
            // the command may have deleted, renamed or added files: look again where it happened
            if (SelectedScan is { IsGroup: false, IndexFile: null } s && SelectedFolder is { } d && !_running.ContainsKey(s))
                Run(s, (sess, ct) => Scanner.RefreshAsync(sess, d, false, ct));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private List<string> SelectedPaths() => [.. SelectedItemsForAction().Select(i => i.FullPath)];

    /// <summary>The selected files / folders on this computer (items of remote scans cannot be changed from here).</summary>
    private List<Item> SelectedItemsForAction()
    {
        List<Item> items = _tabs.SelectedIndex == 1 && _details.SelectedItems.Count > 0
            ? [.. _details.SelectedItems.OfType<ItemRow>().Select(r => r.Item)]
            : [.. _tree.SelectedRows.Where(r => r.Kind != RowKind.FilesGroup).Select(r => r.Item)];
        return LocalOnly(items);
    }

    private List<Item> LocalOnly(List<Item> items)
    {
        var local = items.Where(i => !Shell.IsRemote(i.FullPath)).ToList();
        if (local.Count < items.Count)
            SetStatus("Items of remote sources (SSH, S3, Azure, WebDAV) are read-only here: scans, statistics and reports work for them.");
        return local;
    }

    // ---------------- file actions ----------------

    private Task DeleteSelectedAsync(bool recycle) => DeleteItemsAsync(SelectedItemsForAction(), recycle);

    private async Task EmptyTrashAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows asks itself (with the size)
            Native.Platform.EmptyRecycleBin(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
            _drives.Refresh();
            return;
        }
        if (!OperatingSystem.IsLinux())
            return;
        var (items, bytes) = await Task.Run(Operations.Trash.HomeTrashSize, CancellationToken.None);
        if (items == 0)
        {
            await MessageBox.ShowAsync(this, "The trash is empty.", MessageKind.Info, "Empty trash");
            return;
        }
        if (!await MessageBox.ConfirmAsync(this, $"Delete the {items} item{(items == 1 ? "" : "s")} in the trash ({_settings.Size(bytes)}) for good? This cannot be undone.", "Empty trash", "Empty trash"))
            return;
        var r = await Task.Run(Operations.Trash.EmptyHome, CancellationToken.None);
        _drives.Refresh();
        await ReportAsync(r, "Emptied the trash");
    }

    /// <summary>Takes deleted / moved items out of the tree and updates the totals.</summary>
    private void RemoveGoneFromTree(List<Item> items)
    {
        foreach (var i in items)
            if (i.Parent != null && !File.Exists(i.FullPath) && !Directory.Exists(i.FullPath))
                i.Parent.RemoveChild(i);
        foreach (var s in _tree.Scans)
            s.Root.Recalculate(s.Options.FolderDateFromFilesOnly);
        _tree.Rebuild();
        UpdateViews(true);
        _drives.Refresh();
    }

    private async Task DeleteItemsAsync(List<Item> items, bool recycle)
    {
        items = [.. items.Where(i => i.Parent != null)];
        if (items.Count == 0)
            return;
        var total = items.Sum(i => i.Size);
        var what = items.Count == 1 ? $"\"{items[0].FullPath}\"" : $"{items.Count} items";
        var text = recycle
            ? $"Move {what} ({_settings.Size(total)}) to the {Shell.TrashName}?"
            : $"Delete {what} ({_settings.Size(total)}) for good? This cannot be undone.";
        if (!await MessageBox.ConfirmAsync(this, text, recycle ? "Move to " + Shell.TrashName : "Delete", "ByteAtlas"))
            return;
        var r = await Task.Run(() => FileOperations.Delete([.. items.Select(i => i.FullPath)], recycle), CancellationToken.None);
        RemoveGoneFromTree(items);
        await ReportAsync(r, recycle ? "Moved to the " + Shell.TrashName : "Deleted");
    }

    private async Task ReportAsync(OperationResult r, string verb)
    {
        SetStatus($"{verb}: {r.Done} item(s){(r.Failed > 0 ? $", {r.Failed} failed" : "")}.");
        if (r.Failed > 0)
            await MessageBox.ShowAsync(this, $"{verb}: {r.Done}. Failed: {r.Failed}\n\n" + string.Join("\n", r.Errors.Take(20)), MessageKind.Warning, "ByteAtlas");
    }

    private async Task CopyPathsAsync()
    {
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        var paths = SelectedPaths();
        if (paths.Count > 0)
            await CopyTextAsync(string.Join(Environment.NewLine, paths));
    }

    private async Task CopyTextAsync(string text)
    {
        if (Clipboard != null)
        {
            await Clipboard.SetTextAsync(text);
            SetStatus("Copied to the clipboard.");
        }
    }

    private MenuItem CompressMenu(Func<List<Item>> items)
    {
        var m = new MenuItem { Header = "Compress" };
        foreach (var f in new[] { Native.CompressionFormat.Ntfs, Native.CompressionFormat.Xpress4K, Native.CompressionFormat.Xpress8K, Native.CompressionFormat.Xpress16K, Native.CompressionFormat.Lzx })
        {
            var format = f;
            var label = format == Native.CompressionFormat.Ntfs ? "NTFS (LZNT1, stays compressed when written)" : Native.Compression.Name(format) + (format == Native.CompressionFormat.Lzx ? " (smallest, slowest)" : "");
            m.Items.Add(Item(label, () => CompressItemsAsync(items(), format)));
        }
        m.Items.Add(new Separator());
        m.Items.Add(Item("Uncompress", () => CompressItemsAsync(items(), Native.CompressionFormat.None)));
        ToolTip.SetTip(m, "XPRESS and LZX (Windows compact compression) save more than NTFS compression; a file written to later is stored uncompressed again.");
        return m;
    }

    private Task CompressSelectedAsync(Native.CompressionFormat format) => CompressItemsAsync(SelectedItemsForAction(), format);

    private async Task CompressItemsAsync(List<Item> items, Native.CompressionFormat format)
    {
        if (items.Count == 0)
            return;
        var on = format != Native.CompressionFormat.None;
        SetStatus(on ? $"Compressing ({Native.Compression.Name(format)})…" : "Uncompressing…");
        var progress = new Progress<string>(p => SetStatus((on ? "Compressing " : "Uncompressing ") + p));
        var r = await Task.Run(() => FileOperations.Compress([.. items.Select(i => i.FullPath)], format, progress, CancellationToken.None), CancellationToken.None);
        await ReportAsync(r, on ? "Compressed" : "Uncompressed");
        // sizes on disk changed
        var s = SelectedScan;
        if (s != null && items.FirstOrDefault() is { } first)
        {
            var folder = first as DirNode ?? first.Parent;
            if (folder != null)
                Run(s, (sess, ct) => Scanner.RefreshAsync(sess, folder, true, ct));
        }
    }

    private async Task MoveCopyAsync(List<Item> items)
    {
        if (items.Count == 0)
            return;
        var dlg = new MoveCopyDialog(items.Count, _settings.Size(items.Sum(i => i.Size)));
        if (!await dlg.ShowDialog<bool>(this) || string.IsNullOrWhiteSpace(dlg.Target))
            return;
        var baseFolder = items[0].Parent?.FullPath;
        var r = await Task.Run(() => FileOperations.MoveOrCopy([.. items.Select(i => i.FullPath)], dlg.Target, dlg.Move, dlg.KeepStructure, baseFolder, dlg.Collision, null, CancellationToken.None), CancellationToken.None);
        await ReportAsync(r, dlg.Move ? "Moved" : "Copied");
        if (dlg.Move)
            RefreshSelected(true);
    }

    private async Task RenameAsync(Item? item)
    {
        if (item?.Parent == null)
            return;
        var name = await InputDialog.AskAsync(this, "Rename", "New name:", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name)
            return;
        var target = Path.Combine(item.Parent.FullPath, name);
        try
        {
            if (item is DirNode)
                Directory.Move(item.FullPath, target);
            else
                File.Move(item.FullPath, target);
            item.Name = name;
            _tree.Rebuild();
            UpdateViews(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await MessageBox.ShowAsync(this, e.Message, MessageKind.Error, "Rename");
        }
    }

    private async Task NewFolderAsync(Item? at)
    {
        var folder = at as DirNode ?? at?.Parent ?? SelectedFolder;
        if (folder == null)
            return;
        var name = await InputDialog.AskAsync(this, "New folder", $"Name of the new folder in {folder.FullPath}:", "New folder");
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder.FullPath, name));
            if (SelectedScan is { } s)
                Run(s, (sess, ct) => Scanner.RefreshAsync(sess, folder, false, ct));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await MessageBox.ShowAsync(this, e.Message, MessageKind.Error, "New folder");
        }
    }

    private void OpenRenamer(List<string> paths)
    {
        var w = new RenameWindow(_settings, paths);
        w.Closed += (_, _) =>
        {
            if (w.Changed)
                RefreshSelected(true);
        };
        w.Show(this);
    }

    // ---------------- search ----------------

    private void OpenSearch(List<string>? paths, SearchTemplate? template)
    {
        var scans = _tree.Scans.ToList();
        var start = paths ?? (SelectedFolder is { } d ? [d.FullPath] : _tree.Scans.Select(s => s.Path).ToList());
        var w = new SearchWindow(_settings, scans, start, template);
        w.ShowItem += (_, item) => ShowInTree(item);
        w.FilesChanged += (_, _) => RefreshSelected(true);
        w.Show(this);
    }

    private void OpenSearchFor(Action<SearchCriteria> setup)
    {
        var c = new SearchCriteria();
        setup(c);
        var start = SelectedFolder is { } d ? new List<string> { d.FullPath } : [];
        var w = new SearchWindow(_settings, [.. _tree.Scans], start, null, c);
        w.ShowItem += (_, item) => ShowInTree(item);
        w.Show(this);
    }

    private async Task CopyFilesWhereAsync(Func<FileItem, bool> predicate)
    {
        if (SelectedFolder is not { } d)
            return;
        var list = await Task.Run(() => string.Join(Environment.NewLine, d.AllFiles().Where(predicate).Select(f => f.FullPath)), CancellationToken.None);
        await CopyTextAsync(list);
    }

    // ---------------- options ----------------

    private void OpenOptions() => _ = OpenOptions(null);

    private async Task OpenOptions(string? page)
    {
        var dlg = new OptionsDialog(_settings, page);
        if (!await dlg.ShowDialog<bool>(this))
            return;
        _settings.Save();
        App.ApplyTheme(_settings.Theme);
        ShowDriveList(_settings.ShowDriveList);
        _statusBar.IsVisible = _settings.ShowStatusBar;
        ApplyChartSettings();
        RebuildDetailColumns();
        RefreshAll();
    }

    // ---------------- index files / compare ----------------

    private static readonly (string, string)[] IndexTypes = [("ByteAtlas index (SQLite)", "*.badb;*.sqlite"), ("XML index", "*.xml")];

    private async Task SaveIndexAsync()
    {
        if (RealScan() is not { } s)
            return;
        var name = MakeFileName(s.Title ?? s.Root.FullPath) + DateTime.Now.ToString("_yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".badb";
        var path = await SaveFileAsync("Save scan to index file", name, IndexTypes);
        if (path == null)
            return;
        SetStatus("Saving index…");
        try
        {
            await Task.Run(() => IndexFile.Save(s, path), CancellationToken.None);
            SetStatus("Saved: " + path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            await MessageBox.ShowAsync(this, e.Message, MessageKind.Error, "Save index");
        }
    }

    private async Task LoadIndexDialogAsync()
    {
        var path = await OpenFileAsync("Load scan from index file", IndexTypes);
        if (path != null)
            await LoadIndexAsync(path);
    }

    private async Task LoadIndexAsync(string path)
    {
        SetStatus("Loading " + path + "…");
        try
        {
            var s = await Task.Run(() => IndexFile.Load(path), CancellationToken.None);
            _tree.AddScan(s);
            UpdateGroup();
            _tree.Rebuild();
            SetStatus(ScanSummary(s));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            await MessageBox.ShowAsync(this, "Cannot load the index file: " + e.Message, MessageKind.Error, "Load index");
        }
    }

    private async Task CompareWithIndexAsync()
    {
        if (RealScan() is not { } s)
            return;
        var path = await OpenFileAsync("Compare with saved scan", IndexTypes);
        if (path == null)
            return;
        try
        {
            var old = await Task.Run(() => IndexFile.Load(path), CancellationToken.None);
            await ShowComparisonAsync(s, old, $"{Path.GetFileName(path)} ({old.ScanDate:g})");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException or Microsoft.Data.Sqlite.SqliteException)
        {
            await MessageBox.ShowAsync(this, e.Message, MessageKind.Error, "Compare");
        }
    }

    private async Task CompareWithPathAsync()
    {
        if (RealScan() is not { } s)
            return;
        var p = await PickFolderAsync("Folder to compare with " + s.Path);
        if (p == null)
            return;
        SetStatus("Scanning " + p + " for the comparison…");
        var other = new ScanSession(p, s.Options.Clone());
        await Scanner.ScanAsync(other, CancellationToken.None);
        await ShowComparisonAsync(s, other, p);
    }

    private async Task CompareWithSnapshotAsync()
    {
        if (RealScan() is not { } s || !OperatingSystem.IsWindows())
            return;
        List<ShadowCopy> copies;
        try
        {
            copies = await Task.Run(() => OperatingSystem.IsWindows() ? ShadowCopies.List(s.Root.FullPath) : [], CancellationToken.None);
        }
        catch (Exception e) when (e is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            await MessageBox.ShowAsync(this, "Snapshots cannot be listed: " + e.Message + "\n\nListing snapshots needs administrator rights.", MessageKind.Warning, "Snapshots");
            return;
        }
        if (copies.Count == 0)
        {
            await MessageBox.ShowAsync(this, $"There are no snapshots of the drive of {s.Root.FullPath}. Use File › Create snapshot (administrator) to make one.", MessageKind.Info, "Snapshots");
            return;
        }
        var pick = await ListDialog.PickAsync(this, "Compare with snapshot", "Choose a snapshot of " + Path.GetPathRoot(s.Root.FullPath) + ":", copies.Select(c => c.ToString()).ToList());
        if (pick < 0)
            return;
        var copy = copies[pick];
        var path = copy.MapPath(s.Root.FullPath);
        SetStatus($"Scanning the snapshot from {copy.Created:g}…");
        var o = s.Options.Clone();
        o.Method = ScanMethod.FileSystem;
        var other = new ScanSession(path, o);
        await Scanner.ScanAsync(other, CancellationToken.None);
        other.ScanDate = copy.Created;
        await ShowComparisonAsync(s, other, $"snapshot of {copy.Created:g}");
    }

    private async Task CreateSnapshotAsync()
    {
        if (RealScan() is not { } s || !OperatingSystem.IsWindows())
            return;
        if (!Shell.IsAdmin)
        {
            await MessageBox.ShowAsync(this, "Creating a snapshot needs administrator rights (Scan › Start as administrator).", MessageKind.Warning, "Snapshots");
            return;
        }
        try
        {
            SetStatus("Creating snapshot…");
            var id = await Task.Run(() => OperatingSystem.IsWindows() ? ShadowCopies.Create(s.Root.FullPath) : "", CancellationToken.None);
            SetStatus("Snapshot created: " + id);
        }
        catch (Exception e) when (e is IOException or System.Management.ManagementException or UnauthorizedAccessException)
        {
            await MessageBox.ShowAsync(this, e.Message, MessageKind.Error, "Snapshots");
        }
    }

    private async Task ShowComparisonAsync(ScanSession current, ScanSession old, string description)
    {
        var r = Comparison.Compare(current, old, description);
        _tree.ShowChanges = true;
        _tree.Rebuild();
        UpdateViews(true);
        var sb = new StringBuilder();
        sb.AppendLine($"Compared with {description}");
        sb.AppendLine($"Then: {_settings.Size(r.OldSize)}   Now: {_settings.Size(r.NewSize)}   Change: {SizeFormat.FormatChange(r.Change, _settings.Unit, _settings.Decimals)}");
        sb.AppendLine();
        if (r.Changed.Count > 0)
        {
            sb.AppendLine("Biggest changes:");
            foreach (var c in r.Changed.Take(10))
                sb.AppendLine($"  {SizeFormat.FormatChange(c.Change, _settings.Unit, _settings.Decimals),12}  {c.Path}");
        }
        if (r.Added.Count > 0)
        {
            sb.AppendLine($"New folders ({r.Added.Count}):");
            foreach (var c in r.Added.Take(5))
                sb.AppendLine($"  {_settings.Size(c.NewSize),12}  {c.Path}");
        }
        if (r.Removed.Count > 0)
        {
            sb.AppendLine($"Removed folders ({r.Removed.Count}):");
            foreach (var c in r.Removed.Take(5))
                sb.AppendLine($"  {_settings.Size(c.OldSize),12}  {c.Path}");
        }
        sb.AppendLine();
        sb.Append("The tree now shows the change next to each folder (View › Show size changes).");
        await MessageBox.ShowAsync(this, sb.ToString(), MessageKind.Info, "Comparison");
    }

    private void ClearComparison()
    {
        foreach (var s in _tree.Scans)
            Comparison.Clear(s);
        _tree.ShowChanges = false;
        _tree.Rebuild();
        UpdateViews(true);
    }

    // ---------------- export / print / mail ----------------

    private ExportTable? BranchTable()
    {
        if (SelectedScan is not { } s || SelectedFolder is not { } d)
            return null;
        var o = _settings.Export.Clone();
        o.Unit = _settings.Unit;
        o.Decimals = _settings.Decimals;
        o.DateFormat = _settings.DateFormat;
        if (s.ComparedWith != null && !o.Columns.Contains(TreeColumn.SizeChange))
            o.Columns.Add(TreeColumn.SizeChange);
        return ExportBuilder.Tree(s, d, o);
    }

    private async Task ExportTreeAsync(ExportFormat? format)
    {
        var t = BranchTable();
        if (t == null)
            return;
        var f = format ?? ExportFormat.Excel;
        var (desc, ext) = f switch
        {
            ExportFormat.Csv => ("CSV", "csv"),
            ExportFormat.Html => ("HTML", "html"),
            ExportFormat.Pdf => ("PDF", "pdf"),
            ExportFormat.Text => ("Text", "txt"),
            _ => ("Excel", "xlsx"),
        };
        (string, string)[] types = format == null
            ? [("Excel", "*.xlsx"), ("PDF", "*.pdf"), ("HTML", "*.html"), ("CSV", "*.csv"), ("Text", "*.txt")]
            : [(desc, "*." + ext)];
        var path = await SaveFileAsync("Export", MakeFileName(SelectedFolder!.Name) + "." + ext, types);
        if (path == null)
            return;
        await WriteExportAsync(t, path);
    }

    private async Task WriteExportAsync(ExportTable t, string path)
    {
        try
        {
            var o = _settings.Export.Clone();
            o.Unit = _settings.Unit;
            o.Decimals = _settings.Decimals;
            await Task.Run(() => Exporters.Write(t, path, o), CancellationToken.None);
            SetStatus("Exported: " + path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await MessageBox.ShowAsync(this, e.Message, MessageKind.Error, "Export");
        }
    }

    private async Task ExportListAsync(DataGrid grid, string title)
    {
        if (grid.ItemsSource is not IEnumerable<ItemRow> rows)
            return;
        var list = grid.SelectedItems.Count > 1 ? grid.SelectedItems.OfType<ItemRow>().ToList() : rows.ToList();
        var cols = new List<TreeColumn> { TreeColumn.Name, TreeColumn.Size, TreeColumn.Allocated, TreeColumn.PercentOfParent, TreeColumn.Files, TreeColumn.Modified, TreeColumn.Accessed, TreeColumn.Owner, TreeColumn.FullPath };
        var t = ExportBuilder.Items(title, list.Select(r => r.Item), SelectedScan, cols);
        if (SelectedScan is { } s && SelectedFolder is { } d)
            ExportBuilder.AddHeader(t, s, d);
        var path = await SaveFileAsync("Export list", MakeFileName(title) + ".xlsx", [("Excel", "*.xlsx"), ("PDF", "*.pdf"), ("HTML", "*.html"), ("CSV", "*.csv"), ("Text", "*.txt")]);
        if (path != null)
            await WriteExportAsync(t, path);
    }

    private async Task ExportStatsAsync(DataGrid grid, string title)
    {
        if (grid.ItemsSource is not IEnumerable<StatItem> rows || SelectedScan is not { } s || SelectedFolder is not { } d)
            return;
        var t = ExportBuilder.Stats($"{title}: {d.FullPath}", title == "Extensions" ? "Extension" : title == "Users" ? "Owner" : "Interval", rows.Select(r => r.Row), s, d, title == "Extensions");
        var path = await SaveFileAsync("Export list", MakeFileName(title) + ".xlsx", [("Excel", "*.xlsx"), ("PDF", "*.pdf"), ("HTML", "*.html"), ("CSV", "*.csv"), ("Text", "*.txt")]);
        if (path != null)
            await WriteExportAsync(t, path);
    }

    private async Task ExportAllFilesAsync()
    {
        if (SelectedScan is not { } s || SelectedFolder is not { } d)
            return;
        var path = await SaveFileAsync("List of all files", MakeFileName(d.Name) + "-files.csv", [("CSV", "*.csv")]);
        if (path == null)
            return;
        await Task.Run(() =>
        {
            var t = ExportBuilder.Items("All files in " + d.FullPath, d.AllFiles(), s,
                [TreeColumn.FullPath, TreeColumn.Size, TreeColumn.Allocated, TreeColumn.Modified, TreeColumn.Accessed, TreeColumn.Created, TreeColumn.Owner, TreeColumn.Attributes, TreeColumn.Extension]);
            Exporters.Write(t, path, new ExportSettings { RawNumbers = true, CsvSeparator = _settings.Export.CsvSeparator });
        }, CancellationToken.None);
        SetStatus("Exported: " + path);
    }

    private async Task CopyBranchAsync()
    {
        var t = BranchTable();
        if (t != null)
            await CopyTextAsync(Exporters.Text(t, _settings.Export));
    }

    private async Task CopyFileListAsync()
    {
        if (SelectedFolder is { } d)
            await CopyTextAsync(string.Join(Environment.NewLine, d.AllFiles().Select(f => f.FullPath)));
    }

    /// <summary>The report designer for the selected folder: sections, look, preview, PDF and printing.</summary>
    private Task PrintReportAsync()
    {
        if (SelectedScan is not { } s || SelectedFolder is not { } d)
            return Task.CompletedTask;
        var chart = ReportChartPng(d);
        var w = new ReportWindow(_settings, d.FullPath, () =>
        {
            // the folder can be gone after a refresh: then there is nothing to report on
            if (!_tree.Scans.Contains(s) && !ReferenceEquals(s, _group))
                throw new InvalidOperationException("The scan was removed.");
            return new Export.ReportInput
            {
                Scan = s,
                Folder = d,
                Table = _settings.Export,
                Groups = _settings.FileGroupSet,
                Ages = _settings.AgeIntervalSet,
                AgeBasis = _settings.AgeBasis,
                ChartPng = chart,
                TypeColor = ext =>
                {
                    var c = Palette.ForExtension(ext, _settings.FileGroupSet);
                    return (uint)(c.R << 16 | c.G << 8 | c.B);
                },
            };
        });
        w.Show(this);
        return Task.CompletedTask;
    }

    /// <summary>The treemap of a folder as a PNG for the report (light colours: it goes on paper).</summary>
    private byte[]? ReportChartPng(DirNode d)
    {
        var map = new TreemapChart
        {
            Root = d,
            ShowHierarchy = _settings.TreemapHierarchy,
            ShowFiles = _settings.TreemapFiles,
            ColorByType = _settings.TreemapColorByType,
            ShowLabels = _settings.ChartLabels,
            MaxDepth = _settings.TreemapDepth,
            Groups = _settings.FileGroupSet,
            Format = v => _settings.Size(v),
        };
        map.PrepareLegend();
        // light colours: it goes on paper
        var scope = new ThemeVariantScope { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light, Child = map };
        try
        {
            var size = new Avalonia.Size(1600, 900);
            scope.Measure(size);
            scope.Arrange(new Avalonia.Rect(size));
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1600, 900));
            bmp.Render(scope);
            using var ms = new MemoryStream();
#pragma warning disable CS0618 // a plain PNG needs no encoder options
            bmp.Save(ms);
#pragma warning restore CS0618
            return ms.ToArray();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task EmailReportAsync()
    {
        var t = BranchTable();
        if (t == null)
            return;
        var file = Path.Combine(Path.GetTempPath(), MakeFileName(t.Title) + ".xlsx");
        await WriteExportAsync(t, file);
        var body = _settings.MailBody ? Exporters.Text(t, _settings.Export) : "";
        if (!Mail.Send(t.Title, body, _settings.MailAttach ? file : null))
        {
            await CopyTextAsync(Exporters.Text(t, _settings.Export));
            await MessageBox.ShowAsync(this, $"No mail program answered. The report was copied to the clipboard and saved as\n{file}", MessageKind.Info, "Send by email");
        }
    }

    // ---------------- scheduled scans ----------------

    private async Task ScheduleScanAsync()
    {
        if (RealScan() is not { } s)
            return;
        if (s.Remote != null || _mountedImages.ContainsKey(s))
        {
            await MessageBox.ShowAsync(this, "Scheduled scans are for drives and folders of this computer.", MessageKind.Info, "Schedule scan");
            return;
        }
        if (!File.Exists(Shell.CliPath))
        {
            await MessageBox.ShowAsync(this, $"Scheduled scans run the command line tool, which is not next to the app:\n{Shell.CliPath}\n\nInstall ByteAtlas with its installer or package (they include byteatlas-cli).", MessageKind.Warning, "Schedule scan");
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            await ScheduleScanLinuxAsync(s);
            return;
        }
        if (!OperatingSystem.IsWindows())
            return;
        var dlg = new ScheduleDialog(s.Path, _settings);
        if (!await dlg.ShowDialog<bool>(this))
            return;
        // schtasks reads the definition from a file (UTF-16, as the XML declares)
        var xml = Path.Combine(Path.GetTempPath(), $"byteatlas-task-{Guid.NewGuid():N}.xml");
        bool ok;
        string output;
        try
        {
            await File.WriteAllTextAsync(xml, dlg.TaskXml(), Encoding.Unicode, CancellationToken.None);
            (ok, output) = Shell.SchTasks("/Create", "/F", "/TN", dlg.TaskName, "/XML", xml);
        }
        finally
        {
            File.Delete(xml);
        }
        await MessageBox.ShowAsync(this, ok ? $"Scheduled task \"{dlg.TaskName}\" created.\n\nIt runs:\n{dlg.Command}" : "The task could not be created:\n" + output, ok ? MessageKind.Info : MessageKind.Error, "Schedule scan");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private async Task ScheduleScanLinuxAsync(ScanSession s)
    {
        var backend = await Task.Run(() => Operations.UnixScheduler.Backend, CancellationToken.None);
        if (backend == null)
        {
            await MessageBox.ShowAsync(this, "Neither systemd user timers nor cron are available on this system: install cron (cronie) to schedule scans.", MessageKind.Warning, "Schedule scan");
            return;
        }
        var dlg = new ScheduleDialog(s.Path, _settings, backend);
        if (!await dlg.ShowDialog<bool>(this))
            return;
        var day = DateTime.Now.DayOfWeek;
        try
        {
            var used = await Task.Run(() => Operations.UnixScheduler.Create(dlg.ScanName, dlg.Arguments, dlg.Frequency, dlg.At, day), CancellationToken.None);
            var where = used == "systemd" ? $"systemd user timer byteatlas-{Operations.UnixScheduler.Slug(dlg.ScanName)}.timer" : "your crontab";
            await MessageBox.ShowAsync(this, $"Scheduled scan \"{Operations.UnixScheduler.Slug(dlg.ScanName)}\" created ({where}).\n\nIt runs {Operations.UnixScheduler.Describe(dlg.Frequency, dlg.At, day)}:\n{string.Join(' ', dlg.Arguments)}", MessageKind.Info, "Schedule scan");
        }
        catch (IOException e)
        {
            await MessageBox.ShowAsync(this, "The schedule could not be created:\n" + e.Message, MessageKind.Error, "Schedule scan");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private async Task ManageSchedulesLinuxAsync()
    {
        var tasks = await Task.Run(Operations.UnixScheduler.List, CancellationToken.None);
        if (tasks.Count == 0)
        {
            await MessageBox.ShowAsync(this, "There are no scheduled ByteAtlas scans. Use Scan › Schedule this scan to create one.", MessageKind.Info, "Scheduled scans");
            return;
        }
        var rows = tasks.Select(t => $"{t.Name}  ·  {t.When}  ·  {t.Backend}  ·  {t.Command}").ToList();
        var pick = await ListDialog.PickAsync(this, "Scheduled scans", "Choose a scheduled scan to delete (systemd user timers byteatlas-*.timer, or lines in your crontab):", rows, "Delete");
        if (pick < 0)
            return;
        if (!await MessageBox.ConfirmAsync(this, $"Delete the scheduled scan {tasks[pick].Name}?", "Delete", "Scheduled scans"))
            return;
        try
        {
            await Task.Run(() => Operations.UnixScheduler.Delete(tasks[pick].Name), CancellationToken.None);
            SetStatus("Scheduled scan deleted.");
        }
        catch (IOException e)
        {
            SetStatus(e.Message);
        }
    }

    private async Task ManageSchedulesAsync()
    {
        if (OperatingSystem.IsLinux())
        {
            await ManageSchedulesLinuxAsync();
            return;
        }
        var (_, output) = Shell.SchTasks("/Query", "/FO", "LIST", "/TN", @"\ByteAtlas\");
        var tasks = output.Split('\n').Where(l => l.StartsWith("TaskName:", StringComparison.OrdinalIgnoreCase)).Select(l => l["TaskName:".Length..].Trim()).Distinct().ToList();
        if (tasks.Count == 0)
        {
            await MessageBox.ShowAsync(this, "There are no scheduled ByteAtlas scans. Use Scan › Schedule this scan to create one.", MessageKind.Info, "Scheduled scans");
            return;
        }
        var pick = await ListDialog.PickAsync(this, "Scheduled scans", "Choose a task to delete (they live in the Task Scheduler under \\ByteAtlas):", tasks, "Delete");
        if (pick < 0)
            return;
        if (!await MessageBox.ConfirmAsync(this, $"Delete the scheduled task {tasks[pick]}?", "Delete", "Scheduled scans"))
            return;
        var (ok, msg) = Shell.SchTasks("/Delete", "/F", "/TN", tasks[pick]);
        SetStatus(ok ? "Task deleted." : msg);
    }

    private async Task ShowErrorsAsync()
    {
        if (SelectedScan is not { } s)
            return;
        var lines = s.Errors.Take(5000).Select(e => $"{e.Path}\t{e.Message}").ToList();
        await new TextViewDialog($"Errors of {s.Path} ({s.Errors.Count})", lines.Count == 0 ? "No errors." : string.Join(Environment.NewLine, lines)).ShowDialog(this);
    }

    // ---------------- file pickers ----------------

    public async Task<string?> SaveFileAsync(string title, string suggested, params (string Name, string Patterns)[] types)
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested,
            DefaultExtension = Path.GetExtension(suggested).TrimStart('.'),
            FileTypeChoices = [.. types.Select(t => new FilePickerFileType(t.Name) { Patterns = t.Patterns.Split(';') })],
            ShowOverwritePrompt = true,
        });
        return f?.TryGetLocalPath();
    }

    public async Task<string?> OpenFileAsync(string title, params (string Name, string Patterns)[] types)
    {
        var f = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [.. types.Select(t => new FilePickerFileType(t.Name) { Patterns = t.Patterns.Split(';') }), FilePickerFileTypes.All],
        });
        return f.Count > 0 ? f[0].TryGetLocalPath() : null;
    }

    private static string MakeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) || c == ':' ? '_' : c).ToArray()).Trim('_', ' ', '.');
        return clean.Length == 0 ? "ByteAtlas" : clean.Length > 80 ? clean[..80] : clean;
    }
}
