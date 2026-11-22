using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ByteAtlas.Operations;

namespace ByteAtlas.Dialogs;

/// <summary>Asks for one line of text.</summary>
public sealed class InputDialog : DialogWindow
{
    private readonly TextBox _box;

    private InputDialog(string title, string prompt, string value) : base(title, 460)
    {
        _box = new TextBox { Text = value };
        SetBody(Column(8, Label(prompt), _box));
        Opened += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    public static async Task<string?> AskAsync(Window owner, string title, string prompt, string value)
    {
        var d = new InputDialog(title, prompt, value);
        return await d.ShowDialog<bool>(owner) ? d._box.Text ?? "" : null;
    }
}

/// <summary>Lets the user pick one entry of a list.</summary>
public sealed class ListDialog : DialogWindow
{
    private readonly ListBox _list;

    private ListDialog(string title, string prompt, List<string> items, string ok) : base(title, 560)
    {
        _list = new ListBox { ItemsSource = items, SelectedIndex = 0, MaxHeight = 360 };
        _list.DoubleTapped += (_, _) => Close(true);
        SetBody(Column(8, Label(prompt), _list), ok);
    }

    public static async Task<int> PickAsync(Window owner, string title, string prompt, List<string> items, string ok = "OK")
    {
        var d = new ListDialog(title, prompt, items, ok);
        return await d.ShowDialog<bool>(owner) ? d._list.SelectedIndex : -1;
    }
}

/// <summary>Shows a long text (errors, logs) with copy support.</summary>
public sealed class TextViewDialog : DialogWindow
{
    public TextViewDialog(string title, string text) : base(title, 820)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 440,
            Classes = { "mono" },
        };
        SetBody(box, "Close", null);
    }
}

public sealed class AboutDialog : DialogWindow
{
    public AboutDialog() : base("About ByteAtlas", 480)
    {
        var logo = new Image { Width = 96, Height = 96, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top };
        try
        {
            logo.Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ByteAtlas/Assets/logo-256.png")));
        }
        catch (FileNotFoundException)
        {
            logo.IsVisible = false;
        }
        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "";
        var text = Column(6,
            new TextBlock { Text = "ByteAtlas " + version, Classes = { "h2" } },
            Label("Maps where your disk space goes: fast NTFS scans, size tree, treemap and charts, file types, owners and ages, " +
                  "duplicate and large-file search, cleanup tools, exports and history."),
            Label("Open source under the MIT license.", true),
            Label("Copyright © 2026 Taoism", true));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(logo);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        SetBody(grid, "Close", null);
    }
}

/// <summary>Move / copy options.</summary>
public sealed class MoveCopyDialog : DialogWindow
{
    private readonly TextBox _target = new();
    private readonly RadioButton _move = new() { Content = "Move", GroupName = "op" };
    private readonly RadioButton _copy = new() { Content = "Copy", GroupName = "op", IsChecked = true };
    private readonly CheckBox _keep = new() { Content = "Keep the folder structure", IsChecked = true };
    private readonly ComboBox _collision = new() { ItemsSource = new[] { "Skip existing files", "Overwrite existing files", "Rename (add a number)" }, SelectedIndex = 2 };

    public MoveCopyDialog(int count, string size) : base("Move / copy", 520)
    {
        var path = PathRow(_target, async () =>
        {
            var r = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Target folder" });
            if (r.Count > 0)
                _target.Text = r[0].TryGetLocalPath();
        });
        SetBody(Column(10,
            Label($"{count} item(s), {size}"),
            Row(16, _copy, _move),
            Label("Target folder:"),
            path,
            _keep,
            Row(8, Label("When a file exists:"), _collision)), "Start");
    }

    public string Target => _target.Text?.Trim() ?? "";
    public bool Move => _move.IsChecked == true;
    public bool KeepStructure => _keep.IsChecked == true;
    public CollisionMode Collision => (CollisionMode)Math.Max(0, _collision.SelectedIndex);

    protected override async Task<bool> ValidateAsync()
    {
        if (Target.Length > 0)
            return true;
        await MessageBox.ShowAsync(this, "Choose a target folder.", MessageKind.Warning, "Move / copy");
        return false;
    }
}

/// <summary>Creates a Windows scheduled task that runs byteatlas-cli for this path.</summary>
public sealed class ScheduleDialog : DialogWindow
{
    private readonly string _path;
    private readonly TextBox _name;
    private readonly ComboBox _freq = new() { ItemsSource = new[] { "Daily", "Weekly", "Monthly", "At log on" }, SelectedIndex = 1 };
    private readonly TextBox _time = new() { Text = "03:00", Width = 80 };
    private readonly TextBox _export = new();
    private readonly CheckBox _history = new() { Content = "Add the result to the history", IsChecked = true };
    private readonly CheckBox _index = new() { Content = "Save an index file next to the export (for comparisons)" };

    public ScheduleDialog(string path, AppSettings settings) : base("Schedule scan", 560)
    {
        _path = path;
        var safe = new string(path.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        _name = new TextBox { Text = "Scan " + safe };
        var folder = string.IsNullOrEmpty(settings.ExportFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : settings.ExportFolder;
        _export.Text = Path.Combine(folder, "ByteAtlas", safe + "_{date}.xlsx");
        SetBody(Column(10,
            Label($"Scan {path} on a schedule with the command line tool (byteatlas-cli) and write a report."),
            Row(8, Label("Task name:"), _name),
            Row(8, Label("When:"), _freq, Label("at"), _time),
            Label("Report file ({date} becomes the date; .xlsx, .pdf, .html, .csv or .txt):"),
            _export,
            _history,
            _index,
            Label("The task runs as the current user. It shows up in the Task Scheduler under \\ByteAtlas.", true)), "Create");
    }

    public string TaskName => @"\ByteAtlas\" + (_name.Text ?? "Scan").Replace("\\", "_");

    public string Command
    {
        get
        {
            var args = $"scan \"{_path}\" --export \"{_export.Text}\"";
            if (_history.IsChecked == true)
                args += " --history";
            if (_index.IsChecked == true)
                args += " --save-index \"" + Path.ChangeExtension(_export.Text ?? "scan", ".badb") + "\"";
            return $"\"{Shell.CliPath}\" {args}";
        }
    }

    public string[] SchTasksArgs()
    {
        var list = new List<string> { "/Create", "/F", "/TN", TaskName, "/TR", Command };
        switch (_freq.SelectedIndex)
        {
            case 0:
                list.AddRange(["/SC", "DAILY", "/ST", _time.Text ?? "03:00"]);
                break;
            case 2:
                list.AddRange(["/SC", "MONTHLY", "/ST", _time.Text ?? "03:00"]);
                break;
            case 3:
                list.AddRange(["/SC", "ONLOGON"]);
                break;
            default:
                list.AddRange(["/SC", "WEEKLY", "/ST", _time.Text ?? "03:00"]);
                break;
        }
        return [.. list];
    }
}
