using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using ByteAtlas.Operations;

namespace ByteAtlas.Dialogs;

/// <summary>Bulk rename: an ordered list of rules, live preview, execute and undo.</summary>
public sealed class RenameWindow : Window
{
    private readonly BulkRenamer _renamer = new();
    private readonly List<string> _paths;
    private readonly ListBox _rules = new() { Height = 170 };
    private readonly DataGrid _preview;
    private readonly ContentControl _editor = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };

    public bool Changed { get; private set; }

    public RenameWindow(AppSettings settings, List<string> paths)
    {
        _ = settings;
        _paths = paths;
        Title = "Bulk rename";
        Icon = MainWindow.AppIcon;
        Width = 1100;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _preview = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserSortColumns = true };
        _preview.Columns.Add(new DataGridTextColumn { Header = "Original name", Binding = new Binding(nameof(RenamePreview.OriginalName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _preview.Columns.Add(new DataGridTextColumn { Header = "New name", Binding = new Binding(nameof(RenamePreview.NewName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _preview.Columns.Add(new DataGridTextColumn { Header = "Problem", Binding = new Binding(nameof(RenamePreview.Problem)), Width = new DataGridLength(140) });

        _rules.SelectionChanged += (_, _) => ShowEditor();

        var add = new Button { Content = "Add rule ▾" };
        var addMenu = new ContextMenu();
        foreach (var (label, type) in new[] { ("Replace", RenameRuleType.Replace), ("Insert", RenameRuleType.Insert), ("Serialize (numbers)", RenameRuleType.Serialize), ("Delete", RenameRuleType.Delete), ("Upper / lower case", RenameRuleType.Case) })
        {
            var mi = new MenuItem { Header = label };
            var t = type;
            mi.Click += (_, _) =>
            {
                _renamer.Rules.Add(new RenameRule { Type = t });
                RefreshRules(_renamer.Rules.Count - 1);
            };
            addMenu.Items.Add(mi);
        }
        add.Click += (_, _) => addMenu.Open(add);

        var buttons = MainWindow.DialogRow(6,
            add,
            MainWindow.ToolButton("Delete rule", () =>
            {
                if (_rules.SelectedIndex >= 0)
                {
                    _renamer.Rules.RemoveAt(_rules.SelectedIndex);
                    RefreshRules(Math.Min(_rules.SelectedIndex, _renamer.Rules.Count - 1));
                }
            }),
            MainWindow.ToolButton("Up", () => MoveRule(-1)),
            MainWindow.ToolButton("Down", () => MoveRule(1)),
            MainWindow.ToolButton("Save rules…", SaveRulesAsync),
            MainWindow.ToolButton("Load rules…", LoadRulesAsync));

        var execute = new Button { Content = "Rename", Classes = { "accent" }, MinWidth = 110 };
        execute.Click += async (_, _) => await ExecuteAsync();
        var undo = MainWindow.ToolButton("Undo", () =>
        {
            var r = _renamer.Undo();
            _status.Text = $"Undone: {r.Done}{(r.Failed > 0 ? $", {r.Failed} failed" : "")}.";
            for (var i = 0; i < _paths.Count; i++)
                _paths[i] = _lastMap.GetValueOrDefault(_paths[i], _paths[i]);
            Changed = true;
            UpdatePreview();
        });

        var left = new StackPanel { Spacing = 8, Width = 420 };
        left.Children.Add(new TextBlock { Text = "Rules (applied top to bottom; untick to skip)", Classes = { "header" } });
        left.Children.Add(new Border { Classes = { "panel" }, Child = _rules });
        left.Children.Add(buttons);
        left.Children.Add(new Border { Classes = { "card" }, Child = _editor });

        var right = new DockPanel();
        var bottom = MainWindow.DialogRow(8, execute, undo, _status);
        bottom.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(bottom, Dock.Bottom);
        right.Children.Add(bottom);
        var head = new TextBlock { Text = $"{paths.Count} files and folders", Classes = { "header" } };
        DockPanel.SetDock(head, Dock.Top);
        right.Children.Add(head);
        right.Children.Add(_preview);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,14,*"), Margin = new Thickness(12) };
        grid.Children.Add(left);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        Content = grid;

        _renamer.Rules.Add(new RenameRule { Type = RenameRuleType.Replace, Search = " ", ReplaceWith = "_" });
        RefreshRules(0);
    }

    private Dictionary<string, string> _lastMap = [];

    private void RefreshRules(int select)
    {
        _rules.ItemsSource = _renamer.Rules.Select((r, i) =>
        {
            var cb = new CheckBox { Content = $"{i + 1}. {r.Summary}", IsChecked = r.Active };
            cb.IsCheckedChanged += (_, _) =>
            {
                r.Active = cb.IsChecked == true;
                UpdatePreview();
            };
            return cb;
        }).ToList();
        _rules.SelectedIndex = select;
        UpdatePreview();
    }

    private void MoveRule(int d)
    {
        var i = _rules.SelectedIndex;
        var j = i + d;
        if (i < 0 || j < 0 || j >= _renamer.Rules.Count)
            return;
        (_renamer.Rules[i], _renamer.Rules[j]) = (_renamer.Rules[j], _renamer.Rules[i]);
        RefreshRules(j);
    }

    private void UpdatePreview()
    {
        var p = _renamer.Preview(_paths);
        _preview.ItemsSource = p;
        var changed = p.Count(x => x.Changed && x.Problem == null);
        var problems = p.Count(x => x.Problem != null);
        _status.Text = $"{changed} will be renamed{(problems > 0 ? $", {problems} with problems (skipped)" : "")}.";
    }

    private void RuleChanged()
    {
        var i = _rules.SelectedIndex;
        if (_rules.ItemsSource is List<CheckBox> boxes && i >= 0 && i < boxes.Count)
            boxes[i].Content = $"{i + 1}. {_renamer.Rules[i].Summary}";
        UpdatePreview();
    }

    private void ShowEditor()
    {
        var i = _rules.SelectedIndex;
        if (i < 0 || i >= _renamer.Rules.Count)
        {
            _editor.Content = null;
            return;
        }
        var r = _renamer.Rules[i];
        var sp = new StackPanel { Spacing = 8 };
        TextBox Text(string label, string value, Action<string> set)
        {
            var tb = new TextBox { Text = value };
            tb.TextChanged += (_, _) =>
            {
                set(tb.Text ?? "");
                RuleChanged();
            };
            sp.Children.Add(MainWindow.DialogRow(8, new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center }, tb));
            tb.Width = 250;
            return tb;
        }
        void Number(string label, int value, Action<int> set)
        {
            var n = new NumericUpDown { Value = value, Minimum = -100000, Maximum = 1000000, FormatString = "0", Width = 150 };
            n.ValueChanged += (_, _) =>
            {
                set((int)(n.Value ?? 0));
                RuleChanged();
            };
            sp.Children.Add(MainWindow.DialogRow(8, new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center }, n));
        }
        void Choice<T>(string label, string[] items, T value, Action<T> set) where T : struct, Enum
        {
            var c = new ComboBox { ItemsSource = items, SelectedIndex = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture), Width = 250 };
            c.SelectionChanged += (_, _) =>
            {
                set((T)Enum.ToObject(typeof(T), Math.Max(0, c.SelectedIndex)));
                RuleChanged();
            };
            sp.Children.Add(MainWindow.DialogRow(8, new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center }, c));
        }
        void Check(string label, bool value, Action<bool> set)
        {
            var c = new CheckBox { Content = label, IsChecked = value };
            c.IsCheckedChanged += (_, _) =>
            {
                set(c.IsChecked == true);
                RuleChanged();
            };
            sp.Children.Add(c);
        }
        string[] where = ["At the start (prefix)", "At the end (suffix)", "At a position"];
        switch (r.Type)
        {
            case RenameRuleType.Replace:
                Text("Search for:", r.Search, v => r.Search = v);
                Text("Replace with:", r.ReplaceWith, v => r.ReplaceWith = v);
                Check("Regular expression", r.UseRegex, v => r.UseRegex = v);
                break;
            case RenameRuleType.Insert:
                Text("Text:", r.Text, v => r.Text = v);
                Choice("Where:", where, r.Where, v => r.Where = v);
                Number("Position:", r.Position, v => r.Position = v);
                Check("Count the position from the right", r.FromRight, v => r.FromRight = v);
                break;
            case RenameRuleType.Serialize:
                Number("Start value:", r.StartValue, v => r.StartValue = v);
                Number("Pad to length:", r.PadLength, v => r.PadLength = v);
                Choice("Where:", where, r.Where, v => r.Where = v);
                Number("Position:", r.Position, v => r.Position = v);
                Check("Count the position from the right", r.FromRight, v => r.FromRight = v);
                break;
            case RenameRuleType.Delete:
                Text("Text:", r.Text, v => r.Text = v);
                sp.Children.Add(new TextBlock { Text = "…or, with no text, characters at a position:", Classes = { "muted" } });
                Number("From position:", r.Position, v => r.Position = v);
                Number("Characters:", r.Count, v => r.Count = v);
                Check("Count the position from the right", r.FromRight, v => r.FromRight = v);
                break;
            default:
                Choice("Change to:", ["UPPER CASE", "lower case", "First letter upper case"], r.Case, v => r.Case = v);
                break;
        }
        Choice("Apply to:", ["File name without extension", "Extension only", "Full name"], r.Scope, v => r.Scope = v);
        _editor.Content = sp;
    }

    private async Task ExecuteAsync()
    {
        var preview = _renamer.Preview(_paths);
        var n = preview.Count(p => p.Changed && p.Problem == null);
        if (n == 0 || !await MessageBox.ConfirmAsync(this, $"Rename {n} files and folders?", "Rename", "Bulk rename"))
            return;
        var r = _renamer.Execute(preview);
        _lastMap = preview.Where(p => p.Changed && p.Problem == null).ToDictionary(p => p.NewPath, p => p.OriginalPath, StringComparer.OrdinalIgnoreCase);
        var map = preview.ToDictionary(p => p.OriginalPath, p => p.Changed && p.Problem == null ? p.NewPath : p.OriginalPath, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _paths.Count; i++)
            if (map.TryGetValue(_paths[i], out var np) && (File.Exists(np) || Directory.Exists(np)))
                _paths[i] = np;
        Changed = true;
        UpdatePreview();
        _status.Text = $"Renamed: {r.Done}{(r.Failed > 0 ? $", {r.Failed} failed" : "")}.";
        if (r.Failed > 0)
            await MessageBox.ShowAsync(this, string.Join("\n", r.Errors.Take(20)), MessageKind.Warning, "Bulk rename");
    }

    private async Task SaveRulesAsync()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save rules",
            SuggestedFileName = "rename-rules.json",
            DefaultExtension = "json",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Rule set") { Patterns = ["*.json"] }],
        });
        if (f?.TryGetLocalPath() is { } p)
            _renamer.SaveRules(p);
    }

    private async Task LoadRulesAsync()
    {
        var f = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Load rules",
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Rule set") { Patterns = ["*.json"] }],
        });
        if (f.Count > 0 && f[0].TryGetLocalPath() is { } p)
        {
            try
            {
                _renamer.LoadRules(p);
                RefreshRules(0);
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
                await MessageBox.ShowAsync(this, e.Message, MessageKind.Error, "Load rules");
            }
        }
    }
}
