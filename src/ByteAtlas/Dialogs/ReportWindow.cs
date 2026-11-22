using Avalonia.Platform.Storage;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ByteAtlas.Controls;
using ByteAtlas.Export;

namespace ByteAtlas.Dialogs;

/// <summary>
/// Report designer: what goes into the report (sections, order, rows), how it looks (title, notes, logo, footer,
/// paper) and a live preview of the pages; saves the PDF or prints it. Settings are kept as templates.
/// </summary>
public sealed class ReportWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<ReportInput> _input;
    private ReportDefinition _def;
    private readonly ReportPreview _preview = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _pageInfo = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
    private readonly StackPanel _sections = new() { Spacing = 4 };
    private readonly AccessibleComboBox _templates = new() { MinWidth = 160 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private List<ReportPage> _pages = [];
    private int _layoutRun;
    private bool _filling;

    private readonly TextBox _title = new();
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90 };
    private readonly TextBox _footer = new();
    private readonly TextBlock _logo = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170 };
    private readonly AccessibleComboBox _paper = new() { ItemsSource = new[] { "A4", "Letter" }, MinWidth = 90 };
    private readonly AccessibleComboBox _orientation = new() { ItemsSource = new[] { "Landscape", "Portrait" }, MinWidth = 110 };
    private readonly NumericUpDown _depth = new() { Minimum = 0, Maximum = 20, Width = 130, FormatString = "0" };

    public ReportWindow(AppSettings settings, string folder, Func<ReportInput> input)
    {
        _settings = settings;
        _input = input;
        _def = settings.Report.Clone();
        Title = "Report: " + folder;
        Icon = MainWindow.AppIcon;
        Resizing.Remember(this, "report", 1280, 860, 900, 560);

        foreach (var (box, name) in new[] { (_title, "Report title"), (_notes, "Notes"), (_footer, "Footer") })
        {
            Avalonia.Automation.AutomationProperties.SetName(box, name);
            box.TextChanged += (_, _) => Changed();
        }
        Avalonia.Automation.AutomationProperties.SetName(_paper, "Paper");
        Avalonia.Automation.AutomationProperties.SetName(_orientation, "Orientation");
        Avalonia.Automation.AutomationProperties.SetName(_depth, "Folder levels");
        Avalonia.Automation.AutomationProperties.SetName(_templates, "Template");
        _paper.SelectionChanged += (_, _) => Changed();
        _orientation.SelectionChanged += (_, _) => Changed();
        _depth.ValueChanged += (_, _) => Changed();
        _templates.SelectionChanged += (_, _) =>
        {
            if (!_filling && _templates.SelectedItem is string name && _settings.ReportTemplates.Find(t => t.Name == name) is { } t)
            {
                _def = t.Clone();
                Fill();
            }
        };

        var chooseLogo = MainWindow.ToolButton("Choose…", ChooseLogoAsync, "A picture for the top right corner (PNG, JPEG, BMP)");
        var noLogo = MainWindow.ToolButton("None", () =>
        {
            _def.LogoPath = "";
            _logo.Text = "(none)";
            Changed();
        });
        var left = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 12, 0),
            Children =
            {
                Header("Template"),
                Line(_templates, MainWindow.ToolButton("Save as…", SaveTemplateAsync), MainWindow.ToolButton("Delete", DeleteTemplate)),
                Header("Page"),
                Label("Title"), _title,
                Label("Notes (below the title)"), _notes,
                Label("Footer (every page)"), _footer,
                Label("Logo"), Line(_logo, chooseLogo, noLogo),
                Line(Label("Paper"), _paper, _orientation),
                Header("Sections"),
                new TextBlock { Text = "Tick what goes in; the arrows change the order; the number is the rows of a table.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                _sections,
                Line(Label("Folder levels"), _depth),
            },
        };

        _scroll = new ScrollViewer { Content = _preview, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _scroll.SizeChanged += (_, _) =>
        {
            _preview.FitWidth = _scroll.Viewport.Width;
            _preview.InvalidateMeasure();
        };
        _scroll.ScrollChanged += (_, _) => UpdatePageInfo();
        Avalonia.Automation.AutomationProperties.SetName(_preview, "Report preview");

        var zoom = new AccessibleComboBox { ItemsSource = new[] { "Fit width", "50 %", "75 %", "100 %", "150 %" }, SelectedIndex = 0, MinWidth = 110 };
        Avalonia.Automation.AutomationProperties.SetName(zoom, "Zoom");
        zoom.SelectionChanged += (_, _) => _preview.Zoom = zoom.SelectedIndex switch { 1 => 0.5, 2 => 0.75, 3 => 1, 4 => 1.5, _ => 0 };
        var save = MainWindow.ToolButton("Save PDF…", SavePdfAsync);
        save.Classes.Add("accent");
        var bar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            Margin = new Thickness(0, 0, 0, 8),
            Children = { zoom, _pageInfo, new Border { Width = 16 }, save, MainWindow.ToolButton("Print…", PrintAsync, "Sends the PDF to the printer (the PDF program's print command)") },
        };
        var right = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        right.Children.Add(bar);
        right.Children.Add(new Border { Classes = { "panel" }, Child = _scroll });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("380,6,*"), Margin = new Thickness(14) };
        grid.Children.Add(new ScrollViewer { Content = left });
        Resizing.Split(grid, 1, 0, columns: true, "report.panel");
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        Content = grid;

        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = LayoutAsync();
        };
        Fill();
        Closing += (_, _) =>
        {
            Read();
            _settings.Report = _def.Clone();
            _settings.Save();
        };
    }

    private static TextBlock Header(string t) => new() { Text = t, Classes = { "header" }, Margin = new Thickness(0, 8, 0, 0) };

    private static TextBlock Label(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Line(params Control[] c)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var x in c)
            sp.Children.Add(x);
        return sp;
    }

    /// <summary>Puts the definition into the fields.</summary>
    private void Fill()
    {
        _filling = true;
        _title.Text = _def.Title;
        _notes.Text = _def.Notes;
        _footer.Text = _def.Footer;
        _logo.Text = _def.LogoPath.Length == 0 ? "(none)" : Path.GetFileName(_def.LogoPath);
        _paper.SelectedIndex = _def.Paper == "Letter" ? 1 : 0;
        _orientation.SelectedIndex = _def.Landscape ? 0 : 1;
        _depth.Value = _def.Depth;
        var names = _settings.ReportTemplates.Select(t => t.Name).ToList();
        _templates.ItemsSource = names;
        _templates.SelectedItem = names.Contains(_def.Name) ? _def.Name : null;
        FillSections();
        _filling = false;
        Changed();
    }

    private void FillSections()
    {
        _sections.Children.Clear();
        for (var i = 0; i < _def.Sections.Count; i++)
        {
            var sec = _def.Sections[i];
            var index = i;
            var name = ReportSection.Name(sec.Kind);
            var check = new CheckBox { Content = name, IsChecked = sec.Enabled, MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
            check.IsCheckedChanged += (_, _) =>
            {
                sec.Enabled = check.IsChecked == true;
                Changed();
            };
            var line = Line(check);
            if (sec.Kind is ReportSectionKind.FileTypes or ReportSectionKind.Owners or ReportSectionKind.TopFiles)
            {
                var rows = new NumericUpDown { Minimum = 1, Maximum = 1000, Value = sec.Rows, Width = 118, FormatString = "0" };
                Avalonia.Automation.AutomationProperties.SetName(rows, "Rows of " + name);
                rows.ValueChanged += (_, _) =>
                {
                    sec.Rows = (int)(rows.Value ?? 25);
                    Changed();
                };
                line.Children.Add(rows);
            }
            var up = new Button { Content = "↑", IsEnabled = i > 0 };
            var down = new Button { Content = "↓", IsEnabled = i < _def.Sections.Count - 1 };
            Avalonia.Automation.AutomationProperties.SetName(up, "Move up: " + name);
            Avalonia.Automation.AutomationProperties.SetName(down, "Move down: " + name);
            up.Click += (_, _) => Move(index, -1);
            down.Click += (_, _) => Move(index, 1);
            line.Children.Add(up);
            line.Children.Add(down);
            _sections.Children.Add(line);
        }
    }

    private void Move(int index, int by)
    {
        var to = index + by;
        if (to < 0 || to >= _def.Sections.Count)
            return;
        (_def.Sections[index], _def.Sections[to]) = (_def.Sections[to], _def.Sections[index]);
        FillSections();
        Changed();
    }

    /// <summary>Takes the fields into the definition.</summary>
    private void Read()
    {
        _def.Title = _title.Text ?? "";
        _def.Notes = _notes.Text ?? "";
        _def.Footer = _footer.Text ?? "";
        _def.Paper = _paper.SelectedIndex == 1 ? "Letter" : "A4";
        _def.Landscape = _orientation.SelectedIndex != 1;
        _def.Depth = (int)(_depth.Value ?? 2);
    }

    private void Changed()
    {
        if (_filling)
            return;
        _timer.Stop();
        _timer.Start();
    }

    private async Task LayoutAsync()
    {
        Read();
        var def = _def.Clone();
        var run = ++_layoutRun;
        ReportInput input;
        try
        {
            input = _input();
        }
        catch (InvalidOperationException)
        {
            return; // nothing to report on any more (scan removed)
        }
        try
        {
            var pages = await Task.Run(() => Report.Layout(def, input));
            if (run != _layoutRun)
                return;
            _pages = pages;
            _preview.Pages = pages;
            UpdatePageInfo();
        }
        catch (IOException e)
        {
            _pageInfo.Text = e.Message;
        }
    }

    private void UpdatePageInfo()
    {
        if (_pages.Count == 0)
        {
            _pageInfo.Text = "";
            return;
        }
        var current = 1;
        for (var i = 0; i < _pages.Count; i++)
            if (_preview.PageTop(i) <= _scroll.Offset.Y + _scroll.Viewport.Height / 3)
                current = i + 1;
        _pageInfo.Text = $"Page {current} of {_pages.Count}";
    }

    private async Task ChooseLogoAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Logo for the report",
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;
        try
        {
            // kept as PNG with the settings: the report places PNG pictures, and the original may move
            using var bmp = new Bitmap(path);
            var copy = Path.Combine(Storage.AppPaths.Roaming, "report-logo-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..10] + ".png");
            Directory.CreateDirectory(Storage.AppPaths.Roaming);
#pragma warning disable CS0618 // a plain PNG needs no encoder options
            bmp.Save(copy);
#pragma warning restore CS0618
            _def.LogoPath = copy;
            _logo.Text = Path.GetFileName(path);
            Changed();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await MessageBox.ShowAsync(this, "This picture cannot be read: " + e.Message, MessageKind.Warning, "Logo");
        }
    }

    private async Task SaveTemplateAsync()
    {
        Read();
        var name = await InputDialog.AskAsync(this, "Save template", "Name of the template:", _def.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;
        _def.Name = name.Trim();
        _settings.ReportTemplates.RemoveAll(t => t.Name == _def.Name);
        _settings.ReportTemplates.Add(_def.Clone());
        _settings.Save();
        Fill();
    }

    private void DeleteTemplate()
    {
        if (_templates.SelectedItem is not string name)
            return;
        _settings.ReportTemplates.RemoveAll(t => t.Name == name);
        _settings.Save();
        Fill();
    }

    private async Task<string?> WriteAsync(string path)
    {
        Read();
        try
        {
            var def = _def.Clone();
            var input = _input();
            await Task.Run(() => Report.WritePdf(Report.Layout(def, input), path, def.Title));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await MessageBox.ShowAsync(this, "The report could not be written: " + e.Message, MessageKind.Error, "Report");
            return null;
        }
    }

    private async Task SavePdfAsync()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save report",
            SuggestedFileName = string.Concat((_title.Text ?? "report").Split(Path.GetInvalidFileNameChars())) + ".pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        if (f?.TryGetLocalPath() is not { } path || await WriteAsync(path) == null)
            return;
        _pageInfo.Text = "Saved: " + path;
        Shell.Open(path);
    }

    /// <summary>Prints through the system: the PDF program's print command (Windows), lp (Linux); else it opens.</summary>
    private async Task PrintAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "ByteAtlas-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".pdf");
        if (await WriteAsync(path) == null)
            return;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print" })?.Dispose();
            else if (File.Exists("/usr/bin/lp"))
                Process.Start(new ProcessStartInfo("/usr/bin/lp", [path]) { UseShellExecute = false })?.Dispose();
            else
                Shell.Open(path);
            _pageInfo.Text = "Sent to the printer.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no print command for PDF files: open it, the viewer prints
            Shell.Open(path);
            _pageInfo.Text = "Opened in the PDF viewer: print from there.";
        }
    }
}
