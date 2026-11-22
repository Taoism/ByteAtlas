using Avalonia.Controls;
using Avalonia.Layout;
using ByteAtlas.Analysis;

namespace ByteAtlas.Dialogs;

/// <summary>Protected and ignored folders of the duplicate tabs, and the remembered checksums.</summary>
public sealed class DuplicateRulesDialog : DialogWindow
{
    private readonly TextBox _protected;
    private readonly TextBox _ignored;
    private readonly CheckBox _remember;

    private DuplicateRulesDialog(AppSettings s, Action forget) : base("Duplicate settings", 560)
    {
        static TextBox Lines(IEnumerable<string> lines, string name)
        {
            var t = new TextBox { Text = string.Join(Environment.NewLine, lines), AcceptsReturn = true, Height = 130, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
            Avalonia.Automation.AutomationProperties.SetName(t, name);
            return t;
        }

        _protected = Lines(s.DuplicateProtected, "Protected folders");
        _ignored = Lines(s.DuplicateIgnored, "Ignored folders");
        _remember = new CheckBox { Content = "Remember checksums (a new search only reads files that changed)", IsChecked = s.DuplicateRememberChecksums };
        var forgetButton = new Button { Content = "Forget checksums" };
        forgetButton.Click += (_, _) =>
        {
            forget();
            forgetButton.Content = "Forgotten";
            forgetButton.IsEnabled = false;
        };
        var defaults = new Button { Content = "Defaults" };
        defaults.Click += (_, _) =>
        {
            _protected.Text = string.Join(Environment.NewLine, FolderRules.DefaultProtected);
            _ignored.Text = string.Join(Environment.NewLine, FolderRules.DefaultIgnored);
        };

        SetBody(Column(8,
            Label("Protected folders: copies in them are never selected, deleted, linked or moved (one per line).", true),
            Label(OperatingSystem.IsWindows()
                ? @"A path (C:\Backup\, %WINDIR%\, wildcards) means the folder and everything in it; a name (.git) any folder with that name."
                : "A path (/usr/, wildcards) means the folder and everything in it; a name (.git) any folder with that name.", true),
            _protected,
            Label("Ignored folders: left out of the search completely.", true),
            _ignored,
            Rule(),
            _remember,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { forgetButton, defaults } }));
    }

    private static List<string> Split(string? text) =>
        [.. (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Shows the dialog; true when the settings were changed.</summary>
    public static async Task<bool> EditAsync(Window owner, AppSettings s, Action forget)
    {
        var d = new DuplicateRulesDialog(s, forget);
        if (!await d.ShowDialog<bool>(owner))
            return false;
        s.DuplicateProtected = Split(d._protected.Text);
        s.DuplicateIgnored = Split(d._ignored.Text);
        s.DuplicateRememberChecksums = d._remember.IsChecked == true;
        return true;
    }
}
