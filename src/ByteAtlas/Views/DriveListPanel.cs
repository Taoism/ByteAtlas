using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ByteAtlas.Dialogs;
using ByteAtlas.Model;

namespace ByteAtlas.Views;

/// <summary>All drives (and added paths) with used / free space; click to scan.</summary>
public sealed class DriveListPanel : UserControl
{
    private readonly AppSettings _settings;
    private readonly StackPanel _list = new() { Spacing = 2 };

    public event EventHandler<string>? ScanRequested;

    public DriveListPanel(AppSettings settings)
    {
        _settings = settings;
        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        buttons.Children.Add(Small("+", AddPathAsync, "Add a drive, folder or UNC path to this list"));
        buttons.Children.Add(Small("Shares", AddSharesAsync, "Add all shares of a server"));
        buttons.Children.Add(Small("Copy", CopyAsync, "Copy the drive list (path, size, free space)"));
        buttons.Children.Add(Small("↻", () =>
        {
            Refresh();
            return Task.CompletedTask;
        }, "Refresh"));
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new TextBlock { Text = "Drives", Classes = { "header" }, VerticalAlignment = VerticalAlignment.Center });
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(new ScrollViewer { Content = _list });
        Content = new Border { Classes = { "panel" }, Padding = new Thickness(6), Child = dock };
        Refresh();
    }

    private static Button Small(string text, Func<Task> action, string tip)
    {
        var b = new Button { Content = text, Classes = { "small" }, Padding = new Thickness(6, 1), MinHeight = 22, FontSize = 11.5 };
        b.Click += async (_, _) => await action();
        ToolTip.SetTip(b, tip);
        return b;
    }

    public static List<DriveInfo> ReadyDrives()
    {
        var list = new List<DriveInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.TotalSize > 0)
                    list.Add(d);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // drive went away
            }
        }
        return list;
    }

    public void Refresh()
    {
        _list.Children.Clear();
        foreach (var d in ReadyDrives())
        {
            if (_settings.HiddenDrives.Contains(d.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase))
                continue;
            string label;
            try
            {
                label = _settings.ShowDriveNames && d.VolumeLabel.Length > 0 ? $"{d.RootDirectory.FullName.TrimEnd('\\')}  {d.VolumeLabel}" : d.RootDirectory.FullName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                label = d.RootDirectory.FullName;
            }
            _list.Children.Add(Row(d.RootDirectory.FullName, label, d.TotalSize, d.TotalFreeSpace, d.DriveFormat));
        }
        foreach (var p in _settings.ExtraDrives)
        {
            long total = 0, free = 0;
            try
            {
                var v = Scanning.VolumeInfo.Get(p);
                total = v.Total;
                free = v.Free;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // offline
            }
            _list.Children.Add(Row(p, p, total, free, "", removable: true));
        }
    }

    private Button Row(string path, string label, long total, long free, string fs, bool removable = false)
    {
        var used = total - free;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(2, 1) };
        g.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis });
        var info = new TextBlock { Text = total > 0 ? $"{_settings.Size(free)} free of {_settings.Size(total)}" : "offline", Classes = { "muted" }, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(info, 1);
        g.Children.Add(info);
        var bar = new PercentBar { Value = total > 0 ? (double)used / total : 0, Height = 14, Margin = new Thickness(0, 2, 0, 2) };
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 2);
        g.Children.Add(bar);
        var btn = new Button
        {
            Content = g,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2),
        };
        ToolTip.SetTip(btn, $"Scan {path}{(fs.Length > 0 ? "  ·  " + fs : "")}");
        btn.Click += (_, _) => ScanRequested?.Invoke(this, path);
        var menu = new ContextMenu();
        var scan = new MenuItem { Header = "Scan" };
        scan.Click += (_, _) => ScanRequested?.Invoke(this, path);
        menu.Items.Add(scan);
        var open = new MenuItem { Header = "Open in Explorer" };
        open.Click += (_, _) => Shell.Open(path);
        menu.Items.Add(open);
        var props = new MenuItem { Header = "Properties" };
        props.Click += (_, _) => Shell.Properties(path);
        menu.Items.Add(props);
        var remove = new MenuItem { Header = removable ? "Remove from list" : "Hide this drive" };
        remove.Click += (_, _) =>
        {
            if (removable)
                _settings.ExtraDrives.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            else
                _settings.HiddenDrives.Add(path);
            Refresh();
        };
        menu.Items.Add(remove);
        if (_settings.HiddenDrives.Count > 0)
        {
            var show = new MenuItem { Header = "Show hidden drives" };
            show.Click += (_, _) =>
            {
                _settings.HiddenDrives.Clear();
                Refresh();
            };
            menu.Items.Add(show);
        }
        btn.ContextMenu = menu;
        return btn;
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async Task AddPathAsync()
    {
        if (Owner is not { } w)
            return;
        var p = await InputDialog.AskAsync(w, "Add to drive list", "Drive, folder or UNC path (\\\\server\\share):", "");
        if (string.IsNullOrWhiteSpace(p))
            return;
        if (!_settings.ExtraDrives.Contains(p, StringComparer.OrdinalIgnoreCase))
            _settings.ExtraDrives.Add(p.Trim());
        Refresh();
    }

    private async Task AddSharesAsync()
    {
        if (Owner is not { } w)
            return;
        var server = await InputDialog.AskAsync(w, "Add all shares of a server", "Server name (e.g. nas or \\\\nas):", "");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var shares = await Task.Run(() => Native.Platform.ServerShares(server.Trim()));
        if (shares.Count == 0)
        {
            await MessageBox.ShowAsync(w, $"No shares found on {server} (or the server cannot be reached).", MessageKind.Warning, "Add shares");
            return;
        }
        foreach (var s in shares)
            if (!_settings.ExtraDrives.Contains(s, StringComparer.OrdinalIgnoreCase))
                _settings.ExtraDrives.Add(s);
        Refresh();
    }

    private async Task CopyAsync()
    {
        var sb = new System.Text.StringBuilder("Path\tTotal\tUsed\tFree\tFree %\r\n");
        foreach (var d in ReadyDrives())
            sb.Append($"{d.RootDirectory.FullName}\t{SizeFormat.Format(d.TotalSize)}\t{SizeFormat.Format(d.TotalSize - d.TotalFreeSpace)}\t{SizeFormat.Format(d.TotalFreeSpace)}\t{SizeFormat.Percent((double)d.TotalFreeSpace / d.TotalSize)}\r\n");
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, sb.ToString());
    }
}
