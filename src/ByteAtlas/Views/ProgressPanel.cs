using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ByteAtlas.Controls;
using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas.Views;

/// <summary>One line per running scan: what it does, how far it is, time left, pause / stop.</summary>
public sealed class ProgressPanel : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly Dictionary<ScanSession, Row> _byScan = [];

    public event EventHandler<ScanSession>? StopRequested;
    public event EventHandler<ScanSession>? PauseRequested;

    private sealed class Row
    {
        public required Grid Grid { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Detail { get; init; }
        public required ProgressBar Bar { get; init; }
        public required TextBlock Eta { get; init; }
        public required Button Pause { get; init; }
    }

    public ProgressPanel()
    {
        Content = new Border { Classes = { "strip" }, BorderThickness = new Thickness(0, 1, 0, 0), Child = _rows };
        IsVisible = false;
    }

    /// <summary>Updates the lines from the running scans (called by the window timer).</summary>
    public void Update(IReadOnlyCollection<ScanSession> running, AppSettings settings)
    {
        foreach (var gone in _byScan.Keys.Where(k => !running.Contains(k)).ToList())
        {
            _rows.Children.Remove(_byScan[gone].Grid);
            _byScan.Remove(gone);
        }
        foreach (var s in running)
        {
            if (!_byScan.TryGetValue(s, out var row))
                _byScan[s] = row = Add(s);
            var p = s.Progress ?? s; // a background scan replacing a cached one reports through Progress
            var elapsed = DateTime.Now - p.Started;
            var fraction = Fraction(p);
            row.Title.Text = s.DisplayName + (s.Progress != null ? "  (refreshing the cached scan)" : "");
            row.Detail.Text = p.FoldersDone + p.FilesDone == 0 ? p.CurrentPath : $"{SizeFormat.Count(p.FoldersDone)} folders · {SizeFormat.Count(p.FilesDone)} files · {settings.Size(p.Root.Size)}"
                + (elapsed.TotalSeconds > 1 ? $" · {SizeFormat.Count((long)(p.FilesDone / Math.Max(1, elapsed.TotalSeconds)))} files/s" : "")
                + (p.CurrentPath.Length > 0 ? "  ·  " + p.CurrentPath : "");
            row.Bar.IsIndeterminate = fraction < 0;
            if (fraction >= 0)
                row.Bar.Value = fraction * 100;
            row.Eta.Text = p.State == ScanState.Paused ? "paused"
                : fraction > 0.02 ? $"{Format(elapsed)} · about {Format(TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - fraction) / fraction))} left"
                : Format(elapsed);
            row.Pause.Content = Icons.Content(p.State == ScanState.Paused || s.State == ScanState.Paused ? Icons.Play : Icons.Pause, null);
        }
        IsVisible = running.Count > 0;
    }

    /// <summary>0..1 when it can be told (master file table, whole drives), -1 otherwise.</summary>
    private static double Fraction(ScanSession s)
    {
        if (s.Fraction >= 0)
            return s.Fraction;
        var used = s.VolumeTotal - s.VolumeFree;
        var isDrive = s.Root.Parent == null && Path.GetPathRoot(s.Root.FullPath) is { } r
            && string.Equals(r.TrimEnd('\\', '/'), s.Root.FullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        return isDrive && used > 0 && s.Root.Allocated > 0 ? Math.Min(0.99, (double)s.Root.Allocated / used) : -1;
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private Row Add(ScanSession s)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,220,170,Auto,Auto"), ColumnSpacing = 10 };
        var text = new StackPanel();
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { Classes = { "muted" }, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Children.Add(title);
        text.Children.Add(detail);
        g.Children.Add(text);
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(bar, 1);
        g.Children.Add(bar);
        var eta = new TextBlock { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(eta, 2);
        g.Children.Add(eta);
        var pause = MainWindow.IconButton(Icons.Pause, null, () => PauseRequested?.Invoke(this, s), "Pause / resume");
        Grid.SetColumn(pause, 3);
        g.Children.Add(pause);
        var stop = MainWindow.IconButton(Icons.Stop, null, () => StopRequested?.Invoke(this, s), "Stop");
        Grid.SetColumn(stop, 4);
        g.Children.Add(stop);
        _rows.Children.Add(g);
        return new Row { Grid = g, Title = title, Detail = detail, Bar = bar, Eta = eta, Pause = pause };
    }
}
