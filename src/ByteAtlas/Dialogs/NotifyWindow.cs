using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ByteAtlas.Dialogs;

/// <summary>
/// A small note in the corner of the screen, shown by "ByteAtlas --notify TITLE MESSAGE [PATH]" (the command
/// line tool starts it, e.g. when a scheduled scan finds a drive running out of space). "Open" starts ByteAtlas on
/// the path.
/// </summary>
public sealed class NotifyWindow : Window
{
    private readonly PixelPoint? _near;

    /// <param name="near">Where the ByteAtlas window was last: the note goes on that screen.</param>
    public NotifyWindow(string title, string message, string? path, PixelPoint? near = null)
    {
        _near = near;
        Title = "ByteAtlas: " + title;
        Icon = MainWindow.AppIcon;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var open = new Button { Content = path == null ? "Open ByteAtlas" : "Open in ByteAtlas", MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center };
        open.Classes.Add("accent");
        open.Click += (_, _) =>
        {
            try
            {
                var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ByteAtlas.exe" : "ByteAtlas");
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                if (path != null)
                    psi.ArgumentList.Add(path);
                Process.Start(psi)?.Dispose();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
            {
                // nothing to start: just close
            }
            Close();
        };
        var dismiss = new Button { Content = "Dismiss", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        dismiss.Click += (_, _) => Close();

        Content = new Border
        {
            Padding = new Thickness(18, 14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { open, dismiss } },
                },
            },
        };
        Opened += (_, _) => PlaceInCorner();
    }

    private void PlaceInCorner()
    {
        // bottom right of the screen ByteAtlas was last used on (else the main one), above the task bar
        var screen = (_near is { } p ? Screens.ScreenFromPoint(p) : null) ?? Screens.Primary;
        if (screen == null)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var size = FrameSize ?? Bounds.Size;
        Position = new PixelPoint(area.Right - (int)((size.Width + 16) * scale), area.Bottom - (int)((size.Height + 16) * scale));
    }
}
