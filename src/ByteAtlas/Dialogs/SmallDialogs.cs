using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ByteAtlas.Operations;

using ByteAtlas.Controls;

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
        _list = new ListBox { ItemsSource = items, SelectedIndex = 0 };
        _list.DoubleTapped += (_, _) => Close(true);
        var body = new DockPanel();
        var p = Label(prompt);
        p.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(p, Dock.Top);
        body.Children.Add(p);
        body.Children.Add(_list);
        SetBody(body, ok);
        Resizing.Remember(this, "list", 560, 440, 360, 240);
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
            Classes = { "mono" },
        };
        SetBody(box, "Close", null);
        Resizing.Remember(this, "textview", 860, 600, 400, 260);
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
    private readonly Controls.AccessibleComboBox _collision = new() { ItemsSource = new[] { "Skip existing files", "Overwrite existing files", "Rename (add a number)" }, SelectedIndex = 2 };

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
    private readonly Controls.AccessibleComboBox _freq = new() { ItemsSource = new[] { "Daily", "Weekly", "Monthly", "At log on" }, SelectedIndex = 1 };
    private readonly TextBox _time = new() { Text = "03:00", Width = 80 };
    private readonly TextBox _export = new();
    private readonly CheckBox _history = new() { Content = "Add the result to the history", IsChecked = true };
    private readonly CheckBox _index = new() { Content = "Save an index file next to the export (for comparisons)" };
    private readonly CheckBox _alert = new() { Content = "Warn when the drive has less free space than", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _alertLimit = new() { Text = "10%", Width = 80 };

    public ScheduleDialog(string path, AppSettings settings, string? linuxBackend = null) : base("Schedule scan", 560)
    {
        _path = path;
        var safe = new string(path.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        _name = new TextBox { Text = "Scan " + (safe.Length == 0 ? "root" : safe) };
        var folder = string.IsNullOrEmpty(settings.ExportFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : settings.ExportFolder;
        _export.Text = Path.Combine(folder, "ByteAtlas", (safe.Length == 0 ? "root" : safe) + "_{date}.xlsx");
        var where = OperatingSystem.IsWindows()
            ? "The task runs as the current user. It shows up in the Task Scheduler under \\ByteAtlas."
            : OperatingSystem.IsLinux() && linuxBackend == "systemd"
                ? $"The scan runs as you from a systemd user timer ({Path.Combine(Operations.UnixScheduler.UnitFolder, "byteatlas-…")}). Timers run while you are logged in; after \"loginctl enable-linger\" also when you are not. Missed runs are made up at the next start."
                : "The scan runs as you from your crontab (crontab -l shows it). \"At log on\" runs when the computer starts.";
        SetBody(Column(10,
            Label($"Scan {path} on a schedule with the command line tool (byteatlas-cli) and write a report."),
            Row(8, Label("Task name:"), _name),
            Row(8, Label("When:"), _freq, Label("at"), _time),
            Label("Report file ({date} becomes the date; .xlsx, .pdf, .html, .csv or .txt):"),
            _export,
            _history,
            _index,
            Row(8, _alert, _alertLimit),
            Label("A percentage (10%) or a size (50 GB). The warning is a note in the corner of the screen.", true),
            Label(where, true)), "Create");
    }

    protected override async Task<bool> ValidateAsync()
    {
        if (_freq.SelectedIndex != 3 && !TimeOnly.TryParse(_time.Text, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            await MessageBox.ShowAsync(this, "Give the time as hours:minutes, e.g. 03:00.", MessageKind.Warning, Title ?? "");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_export.Text))
        {
            await MessageBox.ShowAsync(this, "Give a task name and a report file.", MessageKind.Warning, Title ?? "");
            return false;
        }
        var limit = (_alertLimit.Text ?? "").Replace(" ", "");
        var valid = limit.EndsWith('%')
            ? double.TryParse(limit.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct) && pct is > 0 and < 100
            : Model.SizeFormat.TryParse(limit, out var bytes) && bytes > 0;
        if (_alert.IsChecked == true && !valid)
        {
            await MessageBox.ShowAsync(this, "Give the free space limit as a percentage (10%) or a size (50 GB).", MessageKind.Warning, Title ?? "");
            return false;
        }
        return true;
    }

    public string ScanName => (_name.Text ?? "Scan").Trim();

    public string TaskName => @"\ByteAtlas\" + ScanName.Replace("\\", "_");

    public Operations.ScheduleFrequency Frequency => _freq.SelectedIndex switch
    {
        0 => Operations.ScheduleFrequency.Daily,
        2 => Operations.ScheduleFrequency.Monthly,
        3 => Operations.ScheduleFrequency.AtLogon,
        _ => Operations.ScheduleFrequency.Weekly,
    };

    public TimeOnly At => TimeOnly.TryParse(_time.Text, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(3, 0);

    /// <summary>The command line tool and its arguments.</summary>
    public List<string> Arguments
    {
        get
        {
            var export = _export.Text ?? "";
            var list = new List<string> { Shell.CliPath, "scan", _path, "--export", export };
            if (_history.IsChecked == true)
                list.Add("--history");
            if (_index.IsChecked == true)
                list.AddRange(["--save-index", Path.ChangeExtension(export.Length == 0 ? "scan" : export, ".badb")]);
            if (_alert.IsChecked == true && (_alertLimit.Text ?? "").Trim() is { Length: > 0 } limit)
                list.AddRange(["--alert-free", limit.Replace(" ", "")]);
            return list;
        }
    }

    /// <summary>The command as one Windows command line.</summary>
    public string Command => Operations.WindowsTask.CommandLine(Arguments);

    /// <summary>The Task Scheduler definition (weekly runs on today's weekday, like schtasks).</summary>
    public string TaskXml() => Operations.WindowsTask.Xml(Arguments, Frequency, At, DateTime.Now.DayOfWeek,
        Environment.UserDomainName + "\\" + Environment.UserName, DateTime.Now);
}
