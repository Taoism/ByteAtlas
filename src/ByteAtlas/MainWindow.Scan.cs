using System.Collections.Concurrent;
using Avalonia.Threading;
using ByteAtlas.Controls;
using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas;

public sealed partial class MainWindow
{
    private readonly ConcurrentDictionary<ScanSession, CancellationTokenSource> _running = new();
    private readonly Dictionary<ScanSession, FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, byte> _changedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Item> _back = [];
    private readonly List<Item> _forward = [];
    private bool _navigating;

    public ScanSession? SelectedScan => _tree.Selected?.Scan ?? _tree.Group ?? _tree.Scans.FirstOrDefault();

    private ScanSession? _group;

    /// <summary>The selected scan when it is a real one (not the "All scans" group): saving, comparing, scheduling.</summary>
    private ScanSession? RealScan()
    {
        if (SelectedScan is { IsGroup: false } s)
            return s;
        SetStatus("Select a scan (not \"All scans\") for this.");
        return null;
    }

    /// <summary>Shows or updates the "All scans" row (View › Group scans, two or more scans).</summary>
    private void UpdateGroup()
    {
        if (_settings.GroupScans && _tree.Scans.Count >= 2)
        {
            if (_group == null)
                _group = ScanGroup.Build(_tree.Scans);
            else
                ScanGroup.Refresh(_group, _tree.Scans);
            _tree.Group = _group;
        }
        else
        {
            _group = null;
            _tree.Group = null;
        }
    }

    /// <summary>The folder the right pane shows: the selected folder, or the folder of a selected file / [Files] row.</summary>
    public DirNode? SelectedFolder => _tree.Selected switch
    {
        { Kind: RowKind.Folder } r => (DirNode)r.Item,
        { Kind: RowKind.FilesGroup } r => (DirNode)r.Item,
        { } r => r.Item.Parent,
        null => SelectedScan?.Root,
    };

    public void StartScan(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("Type a path or choose a drive first.");
            return;
        }
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!Directory.Exists(path))
        {
            _ = Dialogs.MessageBox.ShowAsync(this, $"The path \"{path}\" does not exist or cannot be reached.", Dialogs.MessageKind.Warning, "ByteAtlas");
            return;
        }
        // scanning the same path again refreshes it
        var existing = _tree.Scans.FirstOrDefault(s => string.Equals(s.Path, ScanSession.NormalizeRoot(path), StringComparison.OrdinalIgnoreCase) && s.IndexFile == null);
        if (existing != null)
        {
            _tree.Select(existing.Root);
            Rescan(existing);
            return;
        }
        var session = new ScanSession(path, _settings.Scan.Clone());
        _tree.AddScan(session);
        UpdateGroup();
        _tree.Rebuild();
        _pathBox.Text = session.Path;
        _settings.AddRecent(session.Path);
        RefreshRecentMenu();
        Run(session, Scanner.ScanAsync);
    }

    private void Rescan(ScanSession s)
    {
        if (_running.ContainsKey(s))
            return;
        Analysis.Comparison.Clear(s);
        Run(s, (sess, ct) => Scanner.RefreshAsync(sess, sess.Root, true, ct));
    }

    private void Run(ScanSession s, Func<ScanSession, CancellationToken, Task> work)
    {
        var cts = new CancellationTokenSource();
        _running[s] = cts;
        _progress.IsVisible = true;
        var root = s.Root;
        _ = Task.Run(async () =>
        {
            try
            {
                await work(s, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            finally
            {
                _running.TryRemove(s, out _);
                cts.Dispose();
                await Dispatcher.UIThread.InvokeAsync(() => ScanFinished(s, root));
            }
        }, CancellationToken.None);
    }

    private void ScanFinished(ScanSession s, DirNode oldRoot)
    {
        if (!ReferenceEquals(oldRoot, s.Root))
            _tree.RootReplaced(s);
        UpdateGroup();
        _tree.Rebuild();
        _progress.IsVisible = !_running.IsEmpty;
        if (s.State == ScanState.Done)
        {
            try
            {
                _history.Record(s);
            }
            catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
            {
                SetStatus("History not saved: " + e.Message);
            }
        }
        UpdateViews(force: true);
        _drives.Refresh();
        SetStatus(ScanSummary(s));
        UpdateWatchers();
        if (_settings.ShowErrorsAfterScan && !s.Errors.IsEmpty)
            _ = ShowErrorsAsync();
        if (_settings.NotifyWhenDone && !IsActive && s.State == ScanState.Done)
            Activate();
    }

    private string ScanSummary(ScanSession s)
    {
        var r = s.Root;
        var state = s.State switch
        {
            ScanState.Cancelled => "Stopped",
            ScanState.Failed => "Failed",
            ScanState.Scanning => "Scanning",
            ScanState.Paused => "Paused",
            _ => "Done",
        };
        var errors = s.Errors.IsEmpty ? "" : $"  ·  {s.Errors.Count} errors (Scan › Show all errors)";
        return $"{state}: {s.Path}  ·  {_settings.Size(r.Size)}  ·  {SizeFormat.Count(r.FileCount)} files  ·  {SizeFormat.Count(r.FolderCount)} folders  ·  {s.Duration.TotalSeconds:N1} s  ·  {s.MethodUsed}{errors}";
    }

    private void OnTimer()
    {
        if (!_running.IsEmpty)
        {
            var s = _running.Keys.FirstOrDefault(x => ReferenceEquals(x, SelectedScan)) ?? _running.Keys.First();
            SetStatus($"{(s.State == ScanState.Paused ? "Paused" : "Scanning")} {s.Path}: {SizeFormat.Count(s.FoldersDone)} folders, {SizeFormat.Count(s.FilesDone)} files  ·  {s.CurrentPath}");
            if (_settings.UpdateDuringScan)
            {
                UpdateGroup();
                _tree.Rebuild();
                UpdateViews(force: false, live: true);
            }
        }
        _statusRight.Text = _tree.Scans.Count == 0 ? "" : $"{_tree.Scans.Count} scan{(_tree.Scans.Count == 1 ? "" : "s")}" + (Shell.IsAdmin ? "  ·  Administrator" : "");
        if (!_changedFolders.IsEmpty && _running.IsEmpty)
            ApplyWatchedChanges();
    }

    private void StopSelected()
    {
        var s = SelectedScan;
        if (s != null && _running.TryGetValue(s, out var cts))
        {
            s.Resume();
            cts.Cancel();
        }
        else
            foreach (var c in _running.Values)
                c.Cancel();
    }

    private void PauseSelected()
    {
        var s = SelectedScan;
        if (s == null)
            return;
        if (s.State == ScanState.Paused)
            s.Resume();
        else
            s.Pause();
    }

    private void RefreshSelected(bool recursive)
    {
        var s = SelectedScan;
        var d = SelectedFolder;
        if (s == null || d == null)
        {
            if (!string.IsNullOrWhiteSpace(_pathBox.Text))
                StartScan(_pathBox.Text);
            return;
        }
        if (s.IsGroup)
        {
            RefreshAllScans();
            return;
        }
        if (_running.ContainsKey(s) || s.IndexFile != null)
            return;
        if (d.Parent == null && recursive)
        {
            Rescan(s);
            return;
        }
        Run(s, (sess, ct) => Scanner.RefreshAsync(sess, d, recursive, ct));
    }

    private void RefreshAllScans()
    {
        foreach (var s in _tree.Scans.Where(x => x.IndexFile == null).ToList())
            Rescan(s);
    }

    private void RemoveSelectedScan()
    {
        if (RealScan() is not { } s)
            return;
        if (_running.TryGetValue(s, out var cts))
            cts.Cancel();
        _tree.RemoveScan(s);
        UpdateGroup();
        _tree.Rebuild();
        if (_watchers.Remove(s, out var w))
            w.Dispose();
        UpdateViews(force: true);
    }

    private async Task SetTitleAsync()
    {
        if (RealScan() is not { } s)
            return;
        var t = await Dialogs.InputDialog.AskAsync(this, "Set title", "Title for this scan (used in the tree, reports and exports):", s.Title ?? "");
        if (t == null)
            return;
        s.Title = t.Length == 0 ? null : t;
        _tree.Rebuild();
    }

    private void RestartAsAdmin()
    {
        if (Shell.RestartAsAdmin(_tree.Scans.Where(s => s.IndexFile == null).Select(s => s.Path)))
            Close();
    }

    // ---------------- watching for changes ----------------

    private void UpdateWatchers()
    {
        foreach (var w in _watchers.Values)
            w.Dispose();
        _watchers.Clear();
        if (!_settings.WatchChanges)
            return;
        foreach (var s in _tree.Scans.Where(x => x.IndexFile == null && x.State == ScanState.Done))
        {
            try
            {
                var w = new FileSystemWatcher(s.Root.FullPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                void Changed(string? path)
                {
                    if (path != null)
                        _changedFolders.TryAdd(Path.GetDirectoryName(path) ?? path, 0);
                }
                w.Changed += (_, e) => Changed(e.FullPath);
                w.Created += (_, e) => Changed(e.FullPath);
                w.Deleted += (_, e) => Changed(e.FullPath);
                w.Renamed += (_, e) =>
                {
                    Changed(e.OldFullPath);
                    Changed(e.FullPath);
                };
                w.EnableRaisingEvents = true;
                _watchers[s] = w;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                SetStatus($"Cannot watch {s.Path}: {e.Message}");
            }
        }
    }

    private void ApplyWatchedChanges()
    {
        var folders = _changedFolders.Keys.ToList();
        _changedFolders.Clear();
        foreach (var s in _tree.Scans.Where(x => x.IndexFile == null))
        {
            var rootPath = s.Root.FullPath;
            var hits = folders.Where(f => f.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
                continue;
            // refresh the deepest existing folder that covers each change (at most a handful)
            var nodes = new HashSet<DirNode>();
            foreach (var f in hits.Take(20))
            {
                var rel = f.Length > rootPath.Length ? f[rootPath.Length..].TrimStart('\\', '/') : "";
                DirNode? node = s.Root;
                foreach (var part in rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = node.Dirs.FirstOrDefault(d => string.Equals(d.Name, part, StringComparison.OrdinalIgnoreCase));
                    if (next == null)
                        break;
                    node = next;
                }
                nodes.Add(node);
            }
            foreach (var n in nodes.Where(n => !nodes.Any(o => !ReferenceEquals(o, n) && IsAncestor(o, n))))
                Run(s, (sess, ct) => Scanner.RefreshAsync(sess, n, true, ct));
        }
    }

    private static bool IsAncestor(DirNode a, DirNode b)
    {
        for (var p = b.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, a))
                return true;
        return false;
    }

    // ---------------- navigation ----------------

    private void OnTreeSelection()
    {
        if (_tree.Selected is { } r)
        {
            if (!_navigating && (_back.Count == 0 || !ReferenceEquals(_back[^1], r.Item)))
            {
                _back.Add(r.Item);
                if (_back.Count > 200)
                    _back.RemoveAt(0);
                _forward.Clear();
            }
            _pathBox.Text = r.Item.FullPath;
        }
        UpdateViews(force: true);
    }

    private void Navigate(Item item)
    {
        _navigating = true;
        try
        {
            _tree.Reveal(item);
        }
        finally
        {
            _navigating = false;
        }
    }

    private void GoBack()
    {
        if (_back.Count < 2)
            return;
        _forward.Add(_back[^1]);
        _back.RemoveAt(_back.Count - 1);
        Navigate(_back[^1]);
    }

    private void GoForward()
    {
        if (_forward.Count == 0)
            return;
        var item = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(item);
        Navigate(item);
    }

    private void GoUp()
    {
        if (_tree.Selected?.Item.Parent is { } p)
            _tree.Reveal(p);
    }

    /// <summary>Selects a folder or file in the tree (from charts, details, search results).</summary>
    public void ShowInTree(Item item)
    {
        _tree.Reveal(item);
    }

    private void ExpandSelected(int levels)
    {
        if (SelectedFolder is { } d)
            _tree.ExpandToLevel(d, levels);
    }

    private void FindDeepest()
    {
        if (SelectedFolder is not { } d)
            return;
        var deepest = Analysis.Statistics.DeepestFolder(d);
        _tree.Reveal(deepest);
        SetStatus($"Deepest folder ({deepest.Level - d.Level} levels below): {deepest.FullPath}");
    }

    private async Task FindFolderAsync()
    {
        if (SelectedScan is not { } s)
            return;
        var text = await Dialogs.InputDialog.AskAsync(this, "Find folder", "Part of a folder name (wildcards * and ? work):", "");
        if (string.IsNullOrWhiteSpace(text))
            return;
        var pattern = text.Contains('*') || text.Contains('?') ? text : "*" + text + "*";
        var start = SelectedFolder ?? s.Root;
        // first match after the current position, then from the top
        var all = s.Root.AllFolders().ToList();
        var from = all.IndexOf(start);
        var hit = all.Skip(from + 1).Concat(all.Take(from + 1)).FirstOrDefault(d => Wildcard.IsMatch(d.Name, pattern));
        if (hit == null)
        {
            SetStatus($"No folder matches \"{text}\".");
            return;
        }
        _tree.Reveal(hit);
    }

    private async Task SetThresholdAsync()
    {
        var t = await Dialogs.InputDialog.AskAsync(this, "Size threshold", "Size used by \"Hide elements smaller than\" and \"Expand folders larger than\" (e.g. 100 MB):", SizeFormat.Format(_settings.SizeThreshold));
        if (t != null && SizeFormat.TryParse(t, out var v))
        {
            _settings.SizeThreshold = v;
            _tree.Rebuild();
        }
    }
}
