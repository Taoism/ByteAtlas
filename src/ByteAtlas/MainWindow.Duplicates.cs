using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Layout;
using ByteAtlas.Analysis;
using ByteAtlas.Controls;
using ByteAtlas.Dialogs;
using ByteAtlas.Export;
using ByteAtlas.Model;
using ByteAtlas.Operations;
using ByteAtlas.Scanning;
using ByteAtlas.Views;

namespace ByteAtlas;

/// <summary>The Duplicate Files and Duplicate Folders tabs: grouped results for the selected folder.</summary>
public sealed partial class MainWindow
{
    private const int DupFilesTab = 6;
    private const int DupFoldersTab = 7;

    private static readonly string[] KeepRules = ["Oldest", "Newest", "Shortest path", "Longest path", "Inside the tree selection", "First in the list"];

    /// <summary>State of one duplicate tab (files or folders).</summary>
    private sealed class DupView
    {
        public required bool Folders { get; init; }
        public DataGrid Grid { get; set; } = null!;
        public TextBlock Summary { get; } = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        public Button Find { get; set; } = null!;
        public List<ItemRow> Rows { get; set; } = [];
        public ScanSession? Scan { get; set; }

        /// <summary>State of the scan the results were found in: a refresh changes it.</summary>
        public (DateTime, long, long) Stamp { get; set; }
        public DirNode? Scope { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public string Freed { get; set; } = "";
    }

    private DupView? _dupFiles;
    private DupView? _dupFolders;

    private DupView DupOf(int tab) => tab == DupFilesTab ? _dupFiles! : _dupFolders!;

    private DockPanel DuplicateTab(bool folders)
    {
        var v = new DupView { Folders = folders };
        if (folders)
            _dupFolders = v;
        else
            _dupFiles = v;

        string[] cols = folders
            ? ["Name", "Detail", "Size", "Files", "Folders", "Modified", "Path"]
            : ["Name", "Size", "Modified", "Created", "Folder", "Hash"];
        v.Grid = GridFactory.Create(cols.Select(k => k == "Detail" ? GridFactory.ItemColumns[k] with { Header = "Role", Width = 200 } : GridFactory.ItemColumns[k]));
        Avalonia.Automation.AutomationProperties.SetName(v.Grid, folders ? "Duplicate folders" : "Duplicate files");
        Resizing.RememberColumns(v.Grid, folders ? "dupfolders" : "dupfiles");
        DragOut.Attach(v.Grid, () => [.. v.Grid.SelectedItems.OfType<ItemRow>().Select(r => r.Path)]);
        v.Grid.ContextMenu = DupMenu(v);
        v.Grid.DoubleTapped += (_, _) =>
        {
            if (v.Grid.SelectedItem is ItemRow r)
            {
                if (r.Item is FileItem)
                    Shell.OpenItem(r.Path);
                else
                    ShowInTree(r.Item);
            }
        };
        v.Grid.SelectionChanged += (_, _) => DupSelectionChanged(v);

        // toolbar: what to compare, then what to do with the copies
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4 };
        v.Find = ToolButton("Find", () => FindDuplicatesAsync(v), "Look for duplicates below the folder selected in the tree");
        bar.Children.Add(v.Find);
        var match = new ComboBox
        {
            ItemsSource = new[] { "Same contents", "Same name and contents", "Same name and size (fast)" },
            SelectedIndex = (int)_settings.DuplicateMatch,
            MinWidth = 190,
        };
        ToolTip.SetTip(match, folders
            ? "Folders match when every file and sub folder has the same name, size and (unless fast) contents"
            : "Contents: a quick checksum of start and end first, then of the whole file");
        match.SelectionChanged += (_, _) => _settings.DuplicateMatch = (DuplicateMatch)Math.Max(0, match.SelectedIndex);
        bar.Children.Add(match);
        var hash = new ComboBox { ItemsSource = new[] { "MD5", "SHA-256" }, SelectedIndex = _settings.DuplicateHash == Search.HashKind.Sha256 ? 1 : 0, MinWidth = 90 };
        ToolTip.SetTip(hash, "Checksum used to compare whole files");
        hash.SelectionChanged += (_, _) => _settings.DuplicateHash = hash.SelectedIndex == 1 ? Search.HashKind.Sha256 : Search.HashKind.Md5;
        bar.Children.Add(hash);
        bar.Children.Add(new TextBlock { Text = "at least", VerticalAlignment = VerticalAlignment.Center });
        var min = new TextBox { Text = SizeFormat.Format(_settings.DuplicateMinSize), Width = 80, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(min, "Minimum size");
        ToolTip.SetTip(min, "Smaller files (or folders) are left out, e.g. 1 MB, 100 KB, 0");
        min.LostFocus += (_, _) =>
        {
            if (SizeFormat.TryParse(min.Text ?? "", out var b))
                _settings.DuplicateMinSize = Math.Max(0, b);
            min.Text = SizeFormat.Format(_settings.DuplicateMinSize);
        };
        bar.Children.Add(min);
        if (folders)
        {
            var subsets = new CheckBox { Content = "Folders contained in others", IsChecked = _settings.DuplicateSubsets, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(subsets, "Also list folders whose files are all found at the same places inside a bigger folder");
            subsets.IsCheckedChanged += (_, _) => _settings.DuplicateSubsets = subsets.IsChecked == true;
            bar.Children.Add(subsets);
        }
        bar.Children.Add(new Border { Width = 10 });
        bar.Children.Add(new TextBlock { Text = "Keep", VerticalAlignment = VerticalAlignment.Center });
        var keep = new ComboBox { ItemsSource = KeepRules, SelectedItem = KeepRules.Contains(_settings.DuplicateKeep) ? _settings.DuplicateKeep : KeepRules[0], MinWidth = 150 };
        Avalonia.Automation.AutomationProperties.SetName(keep, "Copy to keep");
        keep.SelectionChanged += (_, _) =>
        {
            if (keep.SelectedItem is string k)
                _settings.DuplicateKeep = k;
        };
        bar.Children.Add(keep);
        bar.Children.Add(ToolButton("Select the other copies", () => SelectCopies(v), "Selects every copy of each group except the one to keep"));
        bar.Children.Add(ToolButton($"Delete selected ({Shell.TrashName})", () => DeleteDuplicatesAsync(v, true)));
        if (!folders && OperatingSystem.IsWindows())
            bar.Children.Add(ToolButton("Hard-link selected…", () => HardlinkDuplicatesAsync(v), "The selected copies become hard links to the copy kept in their group (same NTFS drive)"));
        bar.Children.Add(ToolButton("Move selected…", () => MoveDuplicatesAsync(v)));
        bar.Children.Add(ToolButton("Show in chart", () => ShowGroupInChart(v), "Shows the folder holding the selected group in the treemap, the copies outlined"));
        bar.Children.Add(ToolButton("Export…", () => ExportDuplicatesAsync(v)));

        var host = new DockPanel();
        var sum = new Border { Padding = new Thickness(0, 0, 0, 4), Child = v.Summary };
        DockPanel.SetDock(sum, Dock.Top);
        host.Children.Add(sum);
        host.Children.Add(v.Grid);
        v.Summary.Text = folders
            ? "Folders with the same contents below the selected folder. Click Find."
            : "Files with the same contents below the selected folder. Click Find.";
        return WithOverview(host, bar);
    }

    private bool _fillingDup;

    private static void SetFindLabel(DupView v, DirNode d)
    {
        var name = d.Parent == null ? d.FullPath.TrimEnd('\\', '/') : d.Name;
        var shortName = Path.GetFileName(name);
        v.Find.Content = "Find in " + (shortName.Length == 0 ? name : shortName);
        ToolTip.SetTip(v.Find, "Look for duplicates below " + d.FullPath);
    }

    /// <summary>Called when a duplicate tab is shown: starts a search once for a new scan / folder, keeps results otherwise.</summary>
    private void UpdateDuplicates(int tab, ScanSession s, DirNode d)
    {
        var v = DupOf(tab);
        SetFindLabel(v, d);
        if (v.Cts != null)
            return;
        // a new scan, a removed one, or the same scan refreshed (change journal, F5): look again
        var stale = v.Scan == null || !_tree.Scans.Contains(v.Scan) && !ReferenceEquals(v.Scan, _group) || Stamp(v.Scan) != v.Stamp;
        if (stale && s.State is ScanState.Done)
            _ = FindDuplicatesAsync(v);
    }

    private async Task FindDuplicatesAsync(DupView v)
    {
        if (v.Cts != null)
        {
            await v.Cts.CancelAsync();
            return;
        }
        if (SelectedScan is not { } s || SelectedFolder is not { } d)
            return;
        if (s.State is ScanState.Scanning or ScanState.Paused)
        {
            v.Summary.Text = "Wait for the scan to finish.";
            return;
        }
        var o = new DuplicateOptions
        {
            Match = _settings.DuplicateMatch,
            Hash = _settings.DuplicateHash,
            MinSize = _settings.DuplicateMinSize,
            Subsets = _settings.DuplicateSubsets,
            CanRead = CanReadFiles(s),
        };
        var cts = new CancellationTokenSource();
        v.Cts = cts;
        v.Find.Content = "Stop";
        v.Summary.Text = "Looking for duplicates in " + d.FullPath + "…";
        var progress = new Progress<DuplicateProgress>(p =>
        {
            if (!cts.IsCancellationRequested)
                v.Summary.Text = p.Total > 0 ? $"{p.Phase}: {p.Done:N0} of {p.Total:N0} files" : p.Phase + "…";
        });
        var started = DateTime.Now;
        // taken before looking: a refresh finishing meanwhile (change journal after a cached start) makes the results stale
        var stamp = Stamp(s);
        v.Scan = s;
        v.Stamp = stamp;
        try
        {
            var rows = await Task.Run(() => v.Folders ? FolderRows(d, o, progress, cts.Token) : FileRows(s, d, o, progress, cts.Token), cts.Token);
            v.Scope = d;
            ShowDuplicateRows(v, rows.Rows, rows.Groups, rows.Freed);
            var how = o.ReadsContents ? "contents compared" : "names and sizes only" + (o.CanRead ? "" : " (files of this scan cannot be read)");
            v.Summary.Text = DupSummary(v) + $"  ·  {how}  ·  {d.FullPath}  ·  {(DateTime.Now - started).TotalSeconds:N1} s";
        }
        catch (OperationCanceledException)
        {
            v.Summary.Text = "Stopped.";
        }
        catch (InvalidOperationException)
        {
            v.Summary.Text = "The tree changed while looking: try again.";
        }
        finally
        {
            var stopped = cts.IsCancellationRequested;
            v.Cts = null;
            cts.Dispose();
            SetFindLabel(v, d);
            // the scan changed while looking and the tab is still shown: look again
            if (!stopped && Stamp(s) != stamp && s.State == ScanState.Done && _tabs.SelectedIndex == (v.Folders ? DupFoldersTab : DupFilesTab))
                UpdateViews(force: true);
        }
    }

    private static (DateTime, long, long) Stamp(ScanSession s) => (s.Finished, s.Root.FileCount, s.Root.Size);

    private static bool CanReadFiles(ScanSession s) =>
        s.Remote == null && s.IndexFile == null && (!s.IsGroup || s.Members.All(m => m.Remote == null && m.IndexFile == null));

    private sealed record DupResult(List<ItemRow> Rows, int Groups, long Freed);

    private DupResult FileRows(ScanSession s, DirNode d, DuplicateOptions o, IProgress<DuplicateProgress> progress, CancellationToken ct)
    {
        var groups = Duplicates.FindFiles(d, o, progress, ct);
        var rows = new List<ItemRow>();
        foreach (var g in groups)
        {
            var links = g.DistinctCopies < g.Files.Count ? $", {g.Files.Count - g.DistinctCopies} already hard-linked" : "";
            var title = $"{g.Number}.  {g.Files.Count} × {_settings.Size(g.Size)}  ·  {_settings.Size(g.Wasted)} could be freed{links}";
            foreach (var f in g.Files)
                rows.Add(new ItemRow(f, ScanOf(s, f), _settings) { Group = g.Number, Hash = g.Hash, GroupTitle = title });
        }
        return new DupResult(rows, groups.Count, groups.Sum(g => g.Wasted));
    }

    private DupResult FolderRows(DirNode d, DuplicateOptions o, IProgress<DuplicateProgress> progress, CancellationToken ct)
    {
        var groups = Duplicates.FindFolders(d, o, progress, ct);
        var rows = new List<ItemRow>();
        foreach (var g in groups)
        {
            var title = g.IsSubset
                ? $"{g.Number}.  {g.Folders.Count - 1} folder(s) contained in {g.Folders[0].Name}  ·  {_settings.Size(g.Wasted)} could be freed"
                : $"{g.Number}.  {g.Folders.Count} × {_settings.Size(g.Folders[0].Size)} ({SizeFormat.Count(g.Folders[0].FileCount)} files)  ·  {_settings.Size(g.Wasted)} could be freed";
            for (var i = 0; i < g.Folders.Count; i++)
            {
                var role = !g.IsSubset ? "Same contents" : i == 0 ? "Holds all files of the others" : "Contained in " + g.Folders[0].Name;
                rows.Add(new ItemRow(g.Folders[i], SelectedScan, _settings) { Group = g.Number, Detail = role, GroupTitle = title });
            }
        }
        return new DupResult(rows, groups.Count, groups.Sum(g => g.Wasted));
    }

    /// <summary>The member scan an item belongs to (group scans: for its owner names).</summary>
    private static ScanSession ScanOf(ScanSession s, Item i)
    {
        if (!s.IsGroup)
            return s;
        var root = i;
        while (root.Parent != null)
            root = root.Parent;
        return s.Members.FirstOrDefault(m => ReferenceEquals(m.Root, root)) ?? s;
    }

    private void ShowDuplicateRows(DupView v, List<ItemRow> rows, int groups, long freed)
    {
        v.Rows = rows;
        v.Freed = $"{groups:N0} groups  ·  {rows.Count:N0} {(v.Folders ? "folders" : "files")}  ·  {_settings.Size(freed)} could be freed";
        var view = new DataGridCollectionView(rows);
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(ItemRow.GroupTitle)));
        _fillingDup = true;
        v.Grid.ItemsSource = view;
        v.Grid.SelectedItem = null;
        _fillingDup = false;
        if (rows.Count == 0)
            v.Freed = v.Folders ? "No duplicate folders found." : "No duplicate files found.";
    }

    private static string DupSummary(DupView v) => v.Freed;

    private static List<ItemRow> GroupRows(DupView v, int group) => [.. v.Rows.Where(r => r.Group == group)];

    private void DupSelectionChanged(DupView v)
    {
        // one row (or rows of one group) selected: mark the group in the tree and the treemap
        var sel = v.Grid.SelectedItems.OfType<ItemRow>().ToList();
        if (_fillingDup || sel.Count == 0 || sel.Select(r => r.Group).Distinct().Count() != 1)
            return;
        var items = GroupRows(v, sel[0].Group).Select(r => r.Item).ToList();
        _tree.Mark([sel[0].Item, .. items.Where(i => !ReferenceEquals(i, sel[0].Item))], reveal: true);
        _treemap.Marked = items;
        SetStatus($"Group {sel[0].Group}: " + string.Join("  |  ", items.Take(4).Select(i => i.FullPath)) + (items.Count > 4 ? "  …" : ""));
    }

    private void ShowGroupInChart(DupView v)
    {
        if (v.Grid.SelectedItem is not ItemRow r)
            return;
        var items = GroupRows(v, r.Group).Select(x => x.Item).ToList();
        // the deepest folder holding every copy
        var common = items[0] as DirNode ?? items[0].Parent;
        while (common != null && !items.TrueForAll(i => IsInside(i, common)))
            common = common.Parent;
        if (common == null)
            return;
        _treemap.Marked = items;
        ShowInTree(common);
        _tabs.SelectedIndex = 0;
    }

    private static bool IsInside(Item i, DirNode folder)
    {
        for (Item? p = i; p != null; p = p.Parent)
            if (ReferenceEquals(p, folder))
                return true;
        return false;
    }

    /// <summary>Selects every copy except the one the keep rule picks, in each group.</summary>
    private void SelectCopies(DupView v)
    {
        v.Grid.SelectedItems.Clear();
        var inside = SelectedFolder;
        foreach (var g in v.Rows.GroupBy(r => r.Group))
        {
            var list = g.ToList();
            ItemRow keep;
            if (list[0].Detail.StartsWith("Holds", StringComparison.Ordinal))
                keep = list[0]; // subsets: the folder holding everything stays
            else
            {
                keep = _settings.DuplicateKeep switch
                {
                    "Newest" => list.MaxBy(r => r.Item.ModifiedTicks)!,
                    "Shortest path" => list.MinBy(r => r.Path.Length)!,
                    "Longest path" => list.MaxBy(r => r.Path.Length)!,
                    "Inside the tree selection" => list.Find(r => inside != null && IsInside(r.Item, inside) && !ReferenceEquals(r.Item, inside)) ?? list[0],
                    "First in the list" => list[0],
                    _ => list.MinBy(r => r.Item.ModifiedTicks <= 0 ? long.MaxValue : r.Item.ModifiedTicks)!,
                };
            }
            foreach (var r in list.Where(r => !ReferenceEquals(r, keep)))
                v.Grid.SelectedItems.Add(r);
        }
        var n = v.Grid.SelectedItems.Count;
        SetStatus($"{n:N0} copies selected ({_settings.Size(v.Grid.SelectedItems.OfType<ItemRow>().Sum(r => r.Size))}); one of each group is kept ({_settings.DuplicateKeep.ToLowerInvariant()}).");
    }

    private static List<ItemRow> SelectedDupRows(DupView v) => [.. v.Grid.SelectedItems.OfType<ItemRow>()];

    private async Task<bool> ConfirmNotAllCopiesAsync(DupView v, List<ItemRow> sel, string title)
    {
        var set = sel.ToHashSet();
        var whole = v.Rows.GroupBy(r => r.Group).Count(g => g.All(set.Contains));
        if (whole == 0)
            return true;
        return await MessageBox.ConfirmAsync(this, $"Every copy of {whole} group(s) is selected: nothing of those would be left. Continue anyway?", "Continue", title);
    }

    private async Task DeleteDuplicatesAsync(DupView v, bool recycle)
    {
        var sel = SelectedDupRows(v);
        if (sel.Count == 0)
        {
            SetStatus("Select the copies to delete first (\"Select the other copies\" picks all but one of each group).");
            return;
        }
        if (!CanReadFiles(v.Scan!))
        {
            SetStatus("Items of remote scans and index files cannot be deleted from here.");
            return;
        }
        if (!await ConfirmNotAllCopiesAsync(v, sel, "Delete duplicates"))
            return;
        var what = $"{sel.Count:N0} {(v.Folders ? "folders" : "files")} ({_settings.Size(sel.Sum(r => r.Size))})";
        if (!await MessageBox.ConfirmAsync(this, recycle ? $"Move {what} to the {Shell.TrashName}?" : $"Delete {what} for good? This cannot be undone.", recycle ? "Move to " + Shell.TrashName : "Delete", "Delete duplicates"))
            return;
        var r = await Task.Run(() => FileOperations.Delete([.. sel.Select(x => x.Path)], recycle), CancellationToken.None);
        RemoveGoneFromTree(sel.Select(x => x.Item).ToList());
        DropGone(v);
        await ReportAsync(r, recycle ? "Moved to the " + Shell.TrashName : "Deleted");
    }

    private async Task MoveDuplicatesAsync(DupView v)
    {
        var sel = SelectedDupRows(v);
        if (sel.Count == 0 || !CanReadFiles(v.Scan!) || !await ConfirmNotAllCopiesAsync(v, sel, "Move duplicates"))
            return;
        await MoveCopyAsync([.. sel.Select(x => x.Item)]);
        RemoveGoneFromTree(sel.Select(x => x.Item).ToList());
        DropGone(v);
    }

    private async Task HardlinkDuplicatesAsync(DupView v)
    {
        var sel = SelectedDupRows(v).ToHashSet();
        if (sel.Count == 0 || !CanReadFiles(v.Scan!))
        {
            SetStatus("Select the copies to replace first (\"Select the other copies\" picks all but one of each group).");
            return;
        }
        // per group: the first unselected file is the one the others link to
        var plan = new List<(string Keep, List<string> Copies)>();
        foreach (var g in v.Rows.GroupBy(r => r.Group))
        {
            var keep = g.FirstOrDefault(r => !sel.Contains(r));
            var copies = g.Where(sel.Contains).Select(r => r.Path).ToList();
            if (keep != null && copies.Count > 0)
                plan.Add((keep.Path, copies));
        }
        if (plan.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Leave one file of each group unselected: the selected copies become hard links to it.", MessageKind.Info, "Hard links");
            return;
        }
        var count = plan.Sum(p => p.Copies.Count);
        if (!await MessageBox.ConfirmAsync(this,
            $"Replace {count:N0} copies in {plan.Count:N0} groups with hard links to the kept file of each group?\n\nAll names stay, the content is stored once. Changing one of the files later changes them all. Works within one drive only.",
            "Replace", "Hard links"))
            return;
        var result = await Task.Run(() =>
        {
            int done = 0, failed = 0;
            var errors = new List<string>();
            foreach (var (keep, copies) in plan)
            {
                var r = FileOperations.ReplaceWithHardlinks(keep, copies);
                done += r.Done;
                failed += r.Failed;
                errors.AddRange(r.Errors);
            }
            return new OperationResult(done, failed, errors);
        }, CancellationToken.None);
        // linked groups free nothing any more
        var linked = plan.Select(p => p.Keep).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = v.Rows.Where(r => linked.Contains(r.Path)).Select(r => r.Group).ToHashSet();
        var left = v.Rows.Where(r => !groups.Contains(r.Group)).ToList();
        ShowDuplicateRows(v, left, left.Select(r => r.Group).Distinct().Count(), FreedOf(left));
        v.Summary.Text = DupSummary(v);
        await ReportAsync(result, "Replaced with hard links");
        // allocated sizes changed: scan the folder again; the results are found again when it is done
        if (v.Scan is { IsGroup: false, IndexFile: null } s && v.Scope is { } scope && !_running.ContainsKey(s))
        {
            v.Scan = null;
            Run(s, (sess, ct) => Scanner.RefreshAsync(sess, scope, true, ct));
        }
    }

    /// <summary>Takes rows whose file / folder is gone out of the results (and groups left with one copy).</summary>
    private void DropGone(DupView v)
    {
        var left = v.Rows.Where(r => r.Item is DirNode ? Directory.Exists(r.Path) : File.Exists(r.Path)).ToList();
        var keepGroups = left.GroupBy(r => r.Group).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        left = [.. left.Where(r => keepGroups.Contains(r.Group))];
        ShowDuplicateRows(v, left, keepGroups.Count, FreedOf(left));
        v.Summary.Text = DupSummary(v);
        _tree.Mark([], reveal: false);
        _treemap.Marked = [];
    }

    /// <summary>Everything but the first row of each group (subsets: the first row holds the others).</summary>
    private static long FreedOf(List<ItemRow> rows)
    {
        long freed = 0;
        var seen = new HashSet<int>();
        foreach (var r in rows)
            if (!seen.Add(r.Group))
                freed += r.Size;
        return freed;
    }

    private ContextMenu DupMenu(DupView v)
    {
        var m = ItemContextMenu(() => [.. SelectedDupRows(v).Select(r => r.Item)]);
        m.Items.Insert(0, new Separator());
        m.Items.Insert(0, Item("Select the whole group", () =>
        {
            if (v.Grid.SelectedItem is ItemRow r)
            {
                v.Grid.SelectedItems.Clear();
                foreach (var x in GroupRows(v, r.Group))
                    v.Grid.SelectedItems.Add(x);
            }
        }));
        m.Items.Insert(0, Item("Show in tree", () =>
        {
            if (v.Grid.SelectedItem is ItemRow r)
                ShowInTree(r.Item);
        }));
        return m;
    }

    private async Task ExportDuplicatesAsync(DupView v)
    {
        if (v.Rows.Count == 0)
            return;
        var rows = v.Grid.SelectedItems.Count > 1 ? SelectedDupRows(v) : v.Rows;
        var byItem = rows.ToDictionary(r => r.Item, r => r);
        var title = v.Folders ? "Duplicate folders" : "Duplicate files";
        var t = ExportBuilder.Items(title, rows.Select(r => r.Item), v.Scan,
            v.Folders ? [TreeColumn.Name, TreeColumn.Size, TreeColumn.Files, TreeColumn.Modified] : [TreeColumn.Name, TreeColumn.Size, TreeColumn.Modified],
            i => v.Folders ? [byItem[i].Path, (long)byItem[i].Group, byItem[i].Detail] : [byItem[i].Path, (long)byItem[i].Group, byItem[i].Hash],
            [new ExportColumn("Path", ColumnKind.Text), new ExportColumn("Group", ColumnKind.Number), new ExportColumn(v.Folders ? "Role" : "Checksum", ColumnKind.Text)]);
        t.Subtitle.Add(v.Freed);
        if (v.Scope != null)
            t.Subtitle.Add(v.Scope.FullPath);
        var path = await SaveFileAsync("Export " + title.ToLowerInvariant(), MakeFileName(title) + ".xlsx", [("Excel", "*.xlsx"), ("PDF", "*.pdf"), ("HTML", "*.html"), ("CSV", "*.csv"), ("Text", "*.txt")]);
        if (path != null)
            await WriteExportAsync(t, path);
    }
}
