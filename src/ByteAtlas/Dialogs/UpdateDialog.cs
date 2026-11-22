using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ByteAtlas.Dialogs;

public enum UpdateChoice { Later, Download, Skip }

/// <summary>A newer release: its notes (Markdown made plain) and what to do.</summary>
public sealed partial class UpdateDialog : Window
{
    private UpdateChoice _choice;

    private UpdateDialog(Operations.ReleaseInfo r)
    {
        Title = "Update available";
        Icon = MainWindow.AppIcon;
        Width = 600;
        Height = 480;
        MinWidth = 420;
        MinHeight = 300;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var head = new TextBlock
        {
            Text = $"ByteAtlas {r.Version.ToString(3)} is available. You have {Operations.UpdateCheck.Current.ToString(3)}."
                + (r.Published != default ? $" Released {r.Published.ToLocalTime():d}." : ""),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var notes = new SelectableTextBlock { Text = Plain(r.Notes), TextWrapping = TextWrapping.Wrap };
        var scroll = new ScrollViewer { Content = notes, Padding = new Thickness(0, 0, 10, 0) };

        Button B(string text, UpdateChoice c, bool accent = false)
        {
            var b = new Button { Content = text, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (accent)
                b.Classes.Add("accent");
            b.Click += (_, _) =>
            {
                _choice = c;
                Close();
            };
            return b;
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { B("Open the download page", UpdateChoice.Download, true), B("Skip this version", UpdateChoice.Skip), B("Later", UpdateChoice.Later) },
        };
        var footer = new Border { Classes = { "footer" }, Padding = new Thickness(16, 12), Child = buttons };
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(head, Dock.Top);
        var body = new DockPanel { Margin = new Thickness(18, 16) };
        body.Children.Add(head);
        body.Children.Add(scroll);
        var dock = new DockPanel();
        dock.Children.Add(footer);
        dock.Children.Add(body);
        Content = dock;
    }

    public static async Task<UpdateChoice> AskAsync(Window owner, Operations.ReleaseInfo r)
    {
        var d = new UpdateDialog(r);
        await d.ShowDialog(owner);
        return d._choice;
    }

    /// <summary>Release notes in Markdown as plain text: no tables, emphasis, code marks or link syntax.</summary>
    internal static string Plain(string markdown)
    {
        var sb = new StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith('|'))
            {
                // table row: cells joined, the |---| line left out
                var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.All(c => c.Trim('-', ':', ' ').Length == 0))
                    continue;
                line = "  " + string.Join("  —  ", cells);
            }
            line = Heading().Replace(line, "");
            line = Link().Replace(line, "$1");
            line = line.Replace("**", "").Replace("`", "");
            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.TrimStart().StartsWith("* ", StringComparison.Ordinal))
                line = new string(' ', line.Length - line.TrimStart().Length) + "• " + line.TrimStart()[2..];
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }

    [GeneratedRegex(@"^\s*#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();
}
