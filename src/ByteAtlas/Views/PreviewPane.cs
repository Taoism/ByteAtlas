using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ByteAtlas.Controls;
using ByteAtlas.Model;

namespace ByteAtlas.Views;

/// <summary>
/// Shows the selected file: pictures, the start of text files, Explorer thumbnails for everything else; with a pinned
/// "reference" file, both side by side for comparing.
/// </summary>
public sealed class PreviewPane : UserControl
{
    private readonly AppSettings _settings;
    private readonly Grid _grid = new() { ColumnDefinitions = new ColumnDefinitions("*") };
    private readonly ContentControl _current = new();
    private readonly ContentControl _reference = new();
    private readonly Button _pin;
    private Item? _item;
    private Item? _pinned;
    private int _version;

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "jfif", "bmp", "gif", "webp", "ico" };

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "log", "md", "csv", "tsv", "ini", "cfg", "conf", "json", "xml", "yaml", "yml", "toml", "cs", "c", "h", "cpp", "hpp", "py", "js", "ts",
        "html", "htm", "css", "bat", "cmd", "ps1", "sh", "sql", "nfo", "srt", "ass", "cue", "m3u", "sfv", "md5", "reg", "properties", "gitignore", "editorconfig",
    };

    public PreviewPane(AppSettings settings)
    {
        _settings = settings;
        _pin = MainWindow.ToolButton("Pin as reference", TogglePin, "Keep this file on the left; the next files you select appear next to it");
        var compare = new CheckBox { Content = "Compare two files", VerticalAlignment = VerticalAlignment.Center };
        compare.IsCheckedChanged += (_, _) =>
        {
            if (compare.IsChecked == true && _item is FileItem)
                Pin(_item);
            else if (compare.IsChecked != true)
                Pin(null);
        };
        _compare = compare;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(compare);
        bar.Children.Add(_pin);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        _grid.Children.Add(_current);
        dock.Children.Add(_grid);
        Content = new Border { Classes = { "panel" }, Padding = new Thickness(8), Child = dock };
        _current.Content = Hint("Select a file to preview it.");
    }

    private readonly CheckBox _compare;

    private void TogglePin() => Pin(_pinned == null ? _item : null);

    private void Pin(Item? item)
    {
        _pinned = item;
        _compare.IsChecked = item != null;
        _pin.Content = item == null ? "Pin as reference" : "Unpin";
        _grid.Children.Clear();
        if (item == null)
        {
            _grid.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetColumn(_current, 0);
            _grid.Children.Add(_current);
            return;
        }
        _grid.ColumnDefinitions = new ColumnDefinitions("*,8,*");
        _grid.Children.Add(_reference);
        Grid.SetColumn(_current, 2);
        _grid.Children.Add(_current);
        _ = FillAsync(_reference, item, ++_version);
    }

    public void Show(Item? item)
    {
        if (ReferenceEquals(item, _item))
            return;
        _item = item;
        _ = FillAsync(_current, item, ++_version);
    }

    private static TextBlock Hint(string text) => new() { Text = text, Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private async Task FillAsync(ContentControl target, Item? item, int version)
    {
        if (item == null)
        {
            target.Content = Hint("Select a file to preview it.");
            return;
        }
        var path = item.FullPath;
        var head = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new SelectableTextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var info = item is DirNode d
            ? $"{_settings.Size(d.Size)} · {SizeFormat.Count(d.FileCount)} files · {SizeFormat.Count(d.FolderCount)} folders"
            : $"{_settings.Size(item.Size)} ({_settings.Size(item.Allocated)} on disk) · modified {SizeFormat.Date(item.Modified, _settings.DateFormat)}";
        head.Children.Add(new TextBlock { Text = info, Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        var body = new ContentControl();
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(body);
        target.Content = dock;
        body.Content = Hint("Loading…");

        Control? view = null;
        var ext = item.Extension;
        try
        {
            if (item is FileItem && ImageExt.Contains(ext) && item.Size < 200L << 20)
            {
                var bmp = await Task.Run(() =>
                {
                    using var fs = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(fs, 1600);
                });
                view = new Image { Source = bmp, Stretch = Stretch.Uniform };
            }
            else if (item is FileItem && (TextExt.Contains(ext) || await Task.Run(() => LooksLikeText(path))))
            {
                var text = await Task.Run(() => ReadStart(path, 256 * 1024));
                view = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Classes = { "mono" } };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            view = null;
        }
        if (view == null)
        {
            var thumb = await ShellThumbnails.GetAsync(path, 512);
            view = thumb != null ? new Image { Source = thumb, Stretch = Stretch.Uniform, MaxWidth = thumb.Size.Width * 1.5 } : Hint("No preview for this file type.");
        }
        if (version == _version || ReferenceEquals(target, _reference))
            body.Content = view;
    }

    private static bool LooksLikeText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[4096];
            var n = fs.Read(buf, 0, buf.Length);
            if (n == 0)
                return false;
            var ctrl = 0;
            for (var i = 0; i < n; i++)
            {
                if (buf[i] == 0)
                    return false;
                if (buf[i] < 9 || (buf[i] > 13 && buf[i] < 32))
                    ctrl++;
            }
            return ctrl < n / 50;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ReadStart(string path, int max)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
        var buf = new char[max];
        var n = r.ReadBlock(buf, 0, max);
        var s = new string(buf, 0, n);
        return fs.Length > max ? s + "\n\n[… first " + SizeFormat.Format(max) + " shown]" : s;
    }
}
