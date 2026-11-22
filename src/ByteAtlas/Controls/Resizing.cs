using Avalonia;
using Avalonia.Controls;

namespace ByteAtlas.Controls;

/// <summary>Remembers window sizes and splitter positions between runs (AppSettings.Layout).</summary>
internal static class Resizing
{
    private static Dictionary<string, double> Store => AppSettings.Current.Layout;

    /// <summary>Makes <paramref name="w"/> resizable, restores its last size and saves it when it closes.</summary>
    public static void Remember(Window w, string key, double width, double height, double minWidth = 480, double minHeight = 320)
    {
        w.CanResize = true;
        w.SizeToContent = SizeToContent.Manual;
        w.MinWidth = minWidth;
        w.MinHeight = minHeight;
        w.Width = Store.TryGetValue(key + ".w", out var sw) && sw >= minWidth ? sw : width;
        w.Height = Store.TryGetValue(key + ".h", out var sh) && sh >= minHeight ? sh : height;
        w.Closing += (_, _) =>
        {
            if (w.WindowState != WindowState.Normal)
                return;
            Store[key + ".w"] = w.Width;
            Store[key + ".h"] = w.Height;
        };
    }

    /// <summary>
    /// A splitter between two cells of <paramref name="grid"/>: definition <paramref name="index"/> gets its saved
    /// pixel size back, and the size is saved whenever the splitter is dragged.
    /// </summary>
    public static GridSplitter Split(Grid grid, int splitterIndex, int sizedIndex, bool columns, string key)
    {
        var s = new GridSplitter
        {
            ResizeDirection = columns ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            Background = Avalonia.Media.Brushes.Transparent,
        };
        if (columns)
            Grid.SetColumn(s, splitterIndex);
        else
            Grid.SetRow(s, splitterIndex);
        if (columns)
        {
            Grid.SetRowSpan(s, Math.Max(1, grid.RowDefinitions.Count));
            if (Store.TryGetValue(key, out var v) && v > 20)
                grid.ColumnDefinitions[sizedIndex].Width = new GridLength(v);
        }
        else
        {
            Grid.SetColumnSpan(s, Math.Max(1, grid.ColumnDefinitions.Count));
            if (Store.TryGetValue(key, out var v) && v > 20)
                grid.RowDefinitions[sizedIndex].Height = new GridLength(v);
        }
        s.DragCompleted += (_, _) =>
        {
            var size = columns ? grid.ColumnDefinitions[sizedIndex].ActualWidth : grid.RowDefinitions[sizedIndex].ActualHeight;
            if (size > 20)
            {
                Store[key] = size;
                // keep the saved definition in pixels from now on (a star size would scale with the window)
                if (columns)
                    grid.ColumnDefinitions[sizedIndex].Width = new GridLength(size);
                else
                    grid.RowDefinitions[sizedIndex].Height = new GridLength(size);
            }
        };
        grid.Children.Add(s);
        return s;
    }

    /// <summary>Remembers the column widths of a data grid by header.</summary>
    public static void RememberColumns(DataGrid g, string key)
    {
        void Apply()
        {
            foreach (var c in g.Columns)
                if (c.Header is string h && Store.TryGetValue($"{key}.{h}", out var w) && w > 16)
                    c.Width = new DataGridLength(w);
        }
        Apply();
        g.Columns.CollectionChanged += (_, _) => Apply();
        g.LayoutUpdated += (_, _) =>
        {
            foreach (var c in g.Columns)
                if (c.Header is string h && h.Length > 0 && c.ActualWidth > 16)
                    Store[$"{key}.{h}"] = c.ActualWidth;
        };
    }
}
