using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ByteAtlas.Analysis;
using ByteAtlas.Model;

namespace ByteAtlas.Controls;

public sealed class ChartSlice
{
    public string Label { get; init; } = "";
    public long Value { get; init; }
    public string? ValueText { get; init; }
    public object? Tag { get; init; }
    public Color? Color { get; set; }
    public bool IsFree { get; init; }
}

internal static class Palette
{
    public static readonly Color[] Colors =
    [
        Color.FromRgb(0x3D, 0x7F, 0xD6), Color.FromRgb(0x2B, 0xB3, 0xA3), Color.FromRgb(0xE0, 0xA3, 0x3B), Color.FromRgb(0x8E, 0x6C, 0xE0),
        Color.FromRgb(0xE0, 0x65, 0x7A), Color.FromRgb(0x6B, 0xBE, 0x5A), Color.FromRgb(0x3C, 0xB4, 0xE5), Color.FromRgb(0xE0, 0x7B, 0x39),
        Color.FromRgb(0xC7, 0x60, 0xB5), Color.FromRgb(0x9A, 0xA5, 0x3A), Color.FromRgb(0x5D, 0x8A, 0xA8), Color.FromRgb(0xB0, 0x7A, 0x55),
    ];

    public static readonly Color Other = Color.FromRgb(0x7F, 0x8A, 0x99);
    public static readonly Color Free = Color.FromRgb(0x4A, 0x4E, 0x56);

    public static Color At(int i) => Colors[((i % Colors.Length) + Colors.Length) % Colors.Length];

    private static readonly Dictionary<string, Color> GroupColors = new()
    {
        ["Video files"] = Colors[0],
        ["Audio files"] = Colors[1],
        ["Image files"] = Colors[2],
        ["Archives"] = Colors[3],
        ["Documents"] = Colors[4],
        ["Executables"] = Colors[5],
        ["Disk images"] = Colors[6],
        ["Source code"] = Colors[7],
        ["Databases"] = Colors[8],
        ["Temporary files"] = Colors[9],
        ["Fonts"] = Colors[10],
        [FileGroups.Other] = Color.FromRgb(0x6E, 0x78, 0x86),
    };

    public static Color ForGroup(string group) => GroupColors.TryGetValue(group, out var c) ? c : At(group.GetHashCode());

    public static Color Shade(Color c, double f) =>
        f >= 0
            ? Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f))
            : Color.FromRgb((byte)(c.R * (1 + f)), (byte)(c.G * (1 + f)), (byte)(c.B * (1 + f)));
}

/// <summary>Base for the drawn charts: text helpers, hover tracking, tooltips.</summary>
public abstract class ChartBase : Control
{
    protected static readonly Typeface Regular = new(FontFamily.Default);
    protected static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    public event EventHandler<object?>? ItemClicked;
    public event EventHandler<object?>? ItemDoubleClicked;
    public event EventHandler<object?>? ItemRightClicked;

    protected FormattedText Text(string s, double size = 12, IBrush? brush = null, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Regular, size, brush ?? this.ThemeBrush("TextBrush", Brushes.White));

    protected IBrush Muted => this.ThemeBrush("MutedTextBrush", Brushes.Gray);

    protected void Click(object? tag) => ItemClicked?.Invoke(this, tag);

    protected void DoubleClick(object? tag) => ItemDoubleClicked?.Invoke(this, tag);

    protected abstract (object? Tag, string? Tip) HitTest(Point p);

    private object? _hover;

    protected object? Hover => _hover;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var (tag, tip) = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(tag, _hover))
        {
            _hover = tag;
            ToolTip.SetTip(this, tip);
            ToolTip.SetIsOpen(this, tip != null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    protected void RightClick(object? tag) => ItemRightClicked?.Invoke(this, tag);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            RightClick(HitTest(e.GetPosition(this)).Tag);
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var (tag, _) = HitTest(e.GetPosition(this));
        if (e.ClickCount >= 2)
            DoubleClick(tag);
        else
            Click(tag);
    }

    /// <summary>Renders the chart into a PNG file (export / clipboard).</summary>
    public void SavePng(string path, int width = 1200, int height = 800)
    {
        var size = new Size(width, height);
        Measure(size);
        Arrange(new Rect(size));
        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(width, height));
        bmp.Render(this);
#pragma warning disable CS0618 // the encoder-options overload is not needed for a plain PNG
        bmp.Save(path);
#pragma warning restore CS0618
        InvalidateMeasure();
    }
}

/// <summary>Pie chart with a legend: biggest slices, small ones merged into "Other".</summary>
public sealed class PieChart : ChartBase
{
    private List<ChartSlice> _slices = [];
    private readonly List<(ChartSlice Slice, double Start, double Sweep)> _arcs = [];
    private readonly List<(ChartSlice Slice, Rect Rect)> _legend = [];
    private Point _center;
    private double _radius;

    public string Title { get; set; } = "";
    public bool ShowLegend { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public double OtherPercent { get; set; } = 2;
    public Func<long, string> Format { get; set; } = v => SizeFormat.Format(v);

    public void SetData(IEnumerable<ChartSlice> slices)
    {
        var list = slices.Where(s => s.Value > 0).OrderByDescending(s => s.IsFree ? -1 : s.Value).ToList();
        var total = list.Sum(s => s.Value);
        var keep = new List<ChartSlice>();
        long other = 0;
        foreach (var s in list)
        {
            if (!s.IsFree && (keep.Count >= 14 || (total > 0 && s.Value * 100.0 / total < OtherPercent)))
                other += s.Value;
            else
                keep.Add(s);
        }
        if (other > 0)
            keep.Add(new ChartSlice { Label = "Other", Value = other, Color = Palette.Other });
        var i = 0;
        foreach (var s in keep)
            s.Color ??= s.IsFree ? Palette.Free : Palette.At(i++);
        _slices = keep;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        _arcs.Clear();
        _legend.Clear();
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 40 || h < 40)
            return;
        var top = 0.0;
        if (Title.Length > 0)
        {
            var t = Text(Title, 13, bold: true);
            ctx.DrawText(t, new Point(12, 8));
            top = t.Height + 14;
        }
        var total = _slices.Sum(s => s.Value);
        if (total <= 0)
        {
            var e = Text("Nothing to show", 13, Muted);
            ctx.DrawText(e, new Point((w - e.Width) / 2, (h - e.Height) / 2));
            return;
        }
        var legendW = ShowLegend ? Math.Min(320, w * 0.42) : 0;
        var areaW = w - legendW;
        _radius = Math.Max(10, Math.Min(areaW, h - top) / 2 - 24);
        _center = new Point(areaW / 2, top + (h - top) / 2);
        var start = -90.0;
        var edge = new Pen(this.ThemeBrush("AppBackgroundBrush", Brushes.Black), 1.5);
        foreach (var s in _slices)
        {
            var sweep = 360.0 * s.Value / total;
            var color = ReferenceEquals(Hover, s) ? Palette.Shade(s.Color!.Value, 0.18) : s.Color!.Value;
            var geo = Slice(_center, _radius, start, sweep);
            ctx.DrawGeometry(new SolidColorBrush(color), edge, geo);
            _arcs.Add((s, start, sweep));
            if (ShowLabels && sweep > 14)
            {
                var mid = (start + sweep / 2) * Math.PI / 180;
                var lp = new Point(_center.X + Math.Cos(mid) * _radius * 0.66, _center.Y + Math.Sin(mid) * _radius * 0.66);
                var pct = Text((100.0 * s.Value / total).ToString("0.#", CultureInfo.CurrentCulture) + " %", 11.5, Brushes.White, true);
                ctx.DrawText(pct, new Point(lp.X - pct.Width / 2, lp.Y - pct.Height / 2));
            }
            start += sweep;
        }
        if (!ShowLegend)
            return;
        var y = Math.Max(top, _center.Y - _slices.Count * 22 / 2.0);
        foreach (var s in _slices)
        {
            var x = areaW + 4;
            ctx.FillRectangle(new SolidColorBrush(s.Color!.Value), new Rect(x, y + 4, 12, 12), 2);
            var name = Text(s.Label, 12, bold: ReferenceEquals(Hover, s));
            name.MaxTextWidth = legendW - 110;
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            ctx.DrawText(name, new Point(x + 18, y + 2));
            var val = Text(s.ValueText ?? Format(s.Value), 12, Muted);
            ctx.DrawText(val, new Point(w - 8 - val.Width, y + 2));
            _legend.Add((s, new Rect(x, y, legendW - 8, 20)));
            y += 22;
        }
    }

    internal static Geometry Slice(Point c, double r, double startDeg, double sweepDeg)
    {
        if (sweepDeg >= 359.99)
            return new EllipseGeometry(new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));
        var a0 = startDeg * Math.PI / 180;
        var a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(c, true);
            ctx.LineTo(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)));
            ctx.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        return g;
    }

    protected override (object? Tag, string? Tip) HitTest(Point p)
    {
        foreach (var (s, rect) in _legend)
            if (rect.Contains(p))
                return (s, Tip(s));
        var dx = p.X - _center.X;
        var dy = p.Y - _center.Y;
        if (dx * dx + dy * dy > _radius * _radius)
            return (null, null);
        var ang = Math.Atan2(dy, dx) * 180 / Math.PI;
        foreach (var (s, start, sweep) in _arcs)
        {
            var a = ang;
            while (a < start)
                a += 360;
            if (a <= start + sweep)
                return (s, Tip(s));
        }
        return (null, null);
    }

    private string Tip(ChartSlice s)
    {
        var total = _slices.Sum(x => x.Value);
        return $"{s.Label}\n{s.ValueText ?? Format(s.Value)}  ({SizeFormat.Percent(total <= 0 ? 0 : (double)s.Value / total)})";
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var (tag, _) = HitTest(e.GetPosition(this));
        if (tag is ChartSlice s)
        {
            if (e.ClickCount >= 2)
                DoubleClick(s.Tag);
            else
                Click(s.Tag);
        }
    }
}

/// <summary>Horizontal bar chart (largest first) with values at the bar ends.</summary>
public sealed class BarChart : ChartBase
{
    private List<ChartSlice> _bars = [];
    private readonly List<(ChartSlice Bar, Rect Rect)> _hits = [];

    public string Title { get; set; } = "";
    public bool ShowGrid { get; set; } = true;
    public bool KeepOrder { get; set; }
    public Func<long, string> Format { get; set; } = v => SizeFormat.Format(v);
    public int MaxBars { get; set; } = 25;

    public void SetData(IEnumerable<ChartSlice> bars)
    {
        var list = KeepOrder ? bars.ToList() : bars.OrderByDescending(b => b.Value).ToList();
        if (list.Count > MaxBars)
        {
            var rest = list.Skip(MaxBars - 1).Sum(b => b.Value);
            list = [.. list.Take(MaxBars - 1), new ChartSlice { Label = "Other", Value = rest, Color = Palette.Other }];
        }
        var i = 0;
        foreach (var b in list)
            b.Color ??= b.IsFree ? Palette.Free : Palette.At(KeepOrder ? 0 : i++);
        _bars = list;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        _hits.Clear();
        var w = Bounds.Width;
        var h = Bounds.Height;
        var top = 8.0;
        if (Title.Length > 0)
        {
            var t = Text(Title, 13, bold: true);
            ctx.DrawText(t, new Point(12, 8));
            top = t.Height + 16;
        }
        if (_bars.Count == 0 || w < 80)
            return;
        var labelW = Math.Min(260, Math.Max(80, _bars.Max(b => Text(b.Label).Width) + 16));
        var valueW = _bars.Max(b => Text(b.ValueText ?? Format(b.Value)).Width) + 12;
        var plotX = 12 + labelW;
        var plotW = Math.Max(20, w - plotX - valueW - 12);
        var rowH = Math.Clamp((h - top - 8) / _bars.Count, 14, 30);
        var max = Math.Max(1, _bars.Max(b => b.Value));
        if (ShowGrid)
        {
            var grid = new Pen(this.ThemeBrush("ChartGridBrush", Brushes.DimGray), 1);
            for (var k = 0; k <= 4; k++)
            {
                var gx = plotX + plotW * k / 4;
                ctx.DrawLine(grid, new Point(gx, top), new Point(gx, top + rowH * _bars.Count));
            }
        }
        var y = top;
        foreach (var b in _bars)
        {
            var label = Text(b.Label, 12, bold: ReferenceEquals(Hover, b));
            label.MaxTextWidth = labelW - 8;
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            ctx.DrawText(label, new Point(12, y + (rowH - label.Height) / 2));
            var bw = plotW * b.Value / max;
            var color = ReferenceEquals(Hover, b) ? Palette.Shade(b.Color!.Value, 0.18) : b.Color!.Value;
            ctx.FillRectangle(new SolidColorBrush(color), new Rect(plotX, y + rowH * 0.18, Math.Max(1, bw), rowH * 0.64), 2);
            var v = Text(b.ValueText ?? Format(b.Value), 11.5, Muted);
            ctx.DrawText(v, new Point(plotX + bw + 6, y + (rowH - v.Height) / 2));
            _hits.Add((b, new Rect(0, y, w, rowH)));
            y += rowH;
        }
    }

    protected override (object? Tag, string? Tip) HitTest(Point p)
    {
        foreach (var (b, r) in _hits)
            if (r.Contains(p))
                return (b, $"{b.Label}\n{b.ValueText ?? Format(b.Value)}");
        return (null, null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var (tag, _) = HitTest(e.GetPosition(this));
        if (tag is ChartSlice s)
        {
            if (e.ClickCount >= 2)
                DoubleClick(s.Tag);
            else
                Click(s.Tag);
        }
    }
}

/// <summary>Squarified treemap of a folder: nested folders, single files as tiles, coloured by file type or level.</summary>
public sealed class TreemapChart : ChartBase
{
    private DirNode? _root;
    private readonly List<(Item Item, Rect Rect, int Depth)> _tiles = [];

    public bool ShowHierarchy { get; set; } = true;
    public bool ShowFiles { get; set; } = true;
    public bool ColorByType { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public int MaxDepth { get; set; } = 6;
    public FileGroups Groups { get; set; } = new();
    public Func<long, string> Format { get; set; } = v => SizeFormat.Format(v);

    public DirNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            InvalidateVisual();
        }
    }

    private Item? _highlight;

    /// <summary>The item selected in the tree: drawn with an accent outline when it is on the map.</summary>
    public Item? Highlight
    {
        get => _highlight;
        set
        {
            _highlight = value;
            InvalidateVisual();
        }
    }

    private const double HeaderH = 15;
    private const double MinTile = 3;

    public override void Render(DrawingContext ctx)
    {
        _tiles.Clear();
        var bounds = new Rect(Bounds.Size).Deflate(4);
        if (_root == null || _root.Size <= 0 || bounds.Width < 20 || bounds.Height < 20)
        {
            var e = Text(_root == null ? "Select a folder" : "Empty folder", 13, Muted);
            ctx.DrawText(e, new Point((Bounds.Width - e.Width) / 2, (Bounds.Height - e.Height) / 2));
            return;
        }
        var edge = new Pen(this.ThemeBrush("AppBackgroundBrush", Brushes.Black), 1);
        Layout(ctx, _root, bounds, 0, edge);
        if (_highlight != null && !ReferenceEquals(_highlight, _root))
        {
            var t = _tiles.FindLast(x => ReferenceEquals(x.Item, _highlight));
            if (t.Item == null)
            {
                // inside a tile too small to draw: outline the deepest drawn folder holding it
                for (var p = _highlight.Parent; p != null && t.Item == null; p = p.Parent)
                    t = _tiles.FindLast(x => ReferenceEquals(x.Item, p));
            }
            if (t.Item != null && !ReferenceEquals(t.Item, _root))
            {
                var accent = this.ThemeBrush("AccentBrush", Brushes.DodgerBlue);
                ctx.DrawRectangle(null, new Pen(Brushes.Black, 4), t.Rect.Deflate(1));
                ctx.DrawRectangle(null, new Pen(accent, 2.5), t.Rect.Deflate(1));
            }
        }
        if (Hover is Item hi)
        {
            var t = _tiles.FindLast(x => ReferenceEquals(x.Item, hi));
            if (t.Item != null)
                ctx.DrawRectangle(null, new Pen(Brushes.White, 2), t.Rect.Deflate(1));
        }
    }

    private void Layout(DrawingContext ctx, DirNode dir, Rect r, int depth, Pen edge)
    {
        var items = new List<Item>();
        foreach (var d in dir.Dirs)
            if (d.Size > 0)
                items.Add(d);
        if (ShowFiles)
        {
            foreach (var f in dir.Files)
                if (f.Size > 0)
                    items.Add(f);
        }
        long filesRest = ShowFiles ? 0 : dir.OwnFilesSize;
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        var values = items.Select(i => (double)i.Size).ToList();
        if (filesRest > 0)
            values.Add(filesRest);
        var total = values.Sum();
        if (total <= 0)
            return;
        var rects = Squarify(values, r);
        for (var i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];
            if (rect.Width < 1 || rect.Height < 1)
                continue;
            if (i >= items.Count)
            {
                // "[Files]" block when single files are hidden
                ctx.FillRectangle(new SolidColorBrush(Palette.Shade(Palette.Other, -0.2)), rect);
                ctx.DrawRectangle(null, edge, rect);
                continue;
            }
            var item = items[i];
            _tiles.Add((item, rect, depth));
            if (item is DirNode sub)
            {
                var deeper = ShowHierarchy && depth + 1 < MaxDepth && rect.Width > 24 && rect.Height > HeaderH + 10;
                var baseColor = ColorByType ? Color.FromRgb(0x3A, 0x3F, 0x48) : Palette.At(depth);
                ctx.FillRectangle(new SolidColorBrush(Palette.Shade(baseColor, -0.1 * Math.Min(depth, 4))), rect);
                if (deeper)
                {
                    if (ShowLabels && rect.Width > 40)
                        Label(ctx, sub.Name + "  " + Format(sub.Size), new Rect(rect.X + 3, rect.Y, rect.Width - 6, HeaderH), 10.5, true);
                    Layout(ctx, sub, new Rect(rect.X + 2, rect.Y + HeaderH, rect.Width - 4, rect.Height - HeaderH - 2), depth + 1, edge);
                }
                else
                {
                    if (!ShowHierarchy && depth + 1 < MaxDepth && rect.Width > MinTile * 4 && rect.Height > MinTile * 4)
                    {
                        Layout(ctx, sub, rect, depth + 1, edge);
                        continue;
                    }
                    var fill = ColorByType ? DominantColor(sub) : Palette.Shade(Palette.At(depth + 1), 0.1);
                    ctx.FillRectangle(new SolidColorBrush(fill), rect);
                    if (ShowLabels)
                        Label(ctx, sub.Name, rect.Deflate(3), 10.5, false);
                }
                ctx.DrawRectangle(null, edge, rect);
            }
            else
            {
                var color = ColorByType ? Palette.ForGroup(Groups.GroupOf(item.Extension)) : Palette.Shade(Palette.At(depth + 1), 0.25);
                ctx.FillRectangle(new SolidColorBrush(color), rect);
                ctx.DrawRectangle(null, edge, rect);
                if (ShowLabels)
                    Label(ctx, item.Name, rect.Deflate(3), 10, false);
            }
        }
    }

    private Color DominantColor(DirNode d)
    {
        // colour of the largest file group below (cheap: largest file only)
        FileItem? best = null;
        foreach (var f in d.Files)
            if (best == null || f.Size > best.Size)
                best = f;
        if (best == null)
            foreach (var c in d.Dirs.Take(3))
                foreach (var f in c.Files)
                    if (best == null || f.Size > best.Size)
                        best = f;
        return best == null ? Palette.Other : Palette.ForGroup(Groups.GroupOf(best.Extension));
    }

    private void Label(DrawingContext ctx, string s, Rect r, double size, bool bold)
    {
        if (r.Width < 28 || r.Height < 12)
            return;
        var t = Text(s, size, Brushes.White, bold);
        t.MaxTextWidth = r.Width;
        t.MaxLineCount = 1;
        t.Trimming = TextTrimming.CharacterEllipsis;
        using (ctx.PushClip(r))
            ctx.DrawText(t, r.TopLeft);
    }

    /// <summary>Squarified layout (Bruls, Huizing, van Wijk).</summary>
    internal static List<Rect> Squarify(List<double> values, Rect r)
    {
        var result = new List<Rect>(values.Count);
        var total = values.Sum();
        if (total <= 0)
            return result;
        var scale = r.Width * r.Height / total;
        var areas = values.Select(v => v * scale).ToList();
        var x = r.X;
        var y = r.Y;
        var w = r.Width;
        var h = r.Height;
        var i = 0;
        while (i < areas.Count)
        {
            var side = Math.Min(w, h);
            if (side <= 0)
            {
                while (result.Count < areas.Count)
                    result.Add(default);
                break;
            }
            var row = new List<double> { areas[i] };
            var j = i + 1;
            while (j < areas.Count && Worst(row, side) >= Worst([.. row, areas[j]], side))
            {
                row.Add(areas[j]);
                j++;
            }
            var sum = row.Sum();
            var thick = sum / side;
            var off = 0.0;
            foreach (var a in row)
            {
                var len = thick <= 0 ? 0 : a / thick;
                result.Add(w >= h ? new Rect(x, y + off, thick, len) : new Rect(x + off, y, len, thick));
                off += len;
            }
            if (w >= h)
            {
                x += thick;
                w -= thick;
            }
            else
            {
                y += thick;
                h -= thick;
            }
            i = j;
        }
        return result;
    }

    private static double Worst(List<double> row, double side)
    {
        var sum = row.Sum();
        if (sum <= 0)
            return double.MaxValue;
        var max = row.Max();
        var min = row.Min();
        var s2 = side * side;
        return Math.Max(s2 * max / (sum * sum), sum * sum / (s2 * Math.Max(min, 1e-9)));
    }

    protected override (object? Tag, string? Tip) HitTest(Point p)
    {
        for (var i = _tiles.Count - 1; i >= 0; i--)
        {
            var (item, rect, _) = _tiles[i];
            if (rect.Contains(p))
            {
                var share = _root == null || _root.Size <= 0 ? 0 : (double)item.Size / _root.Size;
                var kind = item is DirNode d ? $"{SizeFormat.Count(d.FileCount)} files" : Groups.GroupOf(item.Extension);
                return (item, $"{item.FullPath}\n{Format(item.Size)}  ({SizeFormat.Percent(share)})  ·  {kind}");
            }
        }
        return (null, null);
    }
}

/// <summary>Line chart over time (history of a drive or folder).</summary>
public sealed class LineChart : ChartBase
{
    public sealed record Series(string Name, Color Color, List<(DateTime At, long Value)> Points);

    private List<Series> _series = [];
    private readonly List<(DateTime At, long Value, Point P, string Series)> _points = [];

    public string Title { get; set; } = "";
    public bool ShowPoints { get; set; } = true;
    public bool ZeroBased { get; set; }
    public Func<long, string> Format { get; set; } = v => SizeFormat.Format(v);

    public void SetData(List<Series> series)
    {
        _series = series;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        _points.Clear();
        var w = Bounds.Width;
        var h = Bounds.Height;
        var top = 10.0;
        if (Title.Length > 0)
        {
            var t = Text(Title, 13, bold: true);
            ctx.DrawText(t, new Point(12, 8));
            top = t.Height + 18;
        }
        var all = _series.SelectMany(s => s.Points).ToList();
        if (all.Count == 0)
        {
            var e = Text("No history yet: every finished scan adds a point.", 13, Muted);
            ctx.DrawText(e, new Point((w - e.Width) / 2, (h - e.Height) / 2));
            return;
        }
        var minT = all.Min(p => p.At).Ticks;
        var maxT = all.Max(p => p.At).Ticks;
        if (maxT == minT)
        {
            minT -= TimeSpan.TicksPerDay;
            maxT += TimeSpan.TicksPerDay;
        }
        var minV = ZeroBased ? 0 : all.Min(p => p.Value);
        var maxV = all.Max(p => p.Value);
        if (maxV == minV)
        {
            maxV += Math.Max(1, maxV / 20);
            minV = Math.Max(0, minV - Math.Max(1, minV / 20));
        }
        var axisW = 90.0;
        var plot = new Rect(axisW, top, Math.Max(20, w - axisW - 20), Math.Max(20, h - top - 40));
        var grid = new Pen(this.ThemeBrush("ChartGridBrush", Brushes.DimGray), 1);
        for (var k = 0; k <= 4; k++)
        {
            var gy = plot.Bottom - plot.Height * k / 4;
            ctx.DrawLine(grid, new Point(plot.Left, gy), new Point(plot.Right, gy));
            var label = Text(Format(minV + (maxV - minV) * k / 4), 11, Muted);
            ctx.DrawText(label, new Point(plot.Left - 8 - label.Width, gy - label.Height / 2));
        }
        for (var k = 0; k <= 4; k++)
        {
            var t = new DateTime(minT + (maxT - minT) * k / 4);
            var label = Text(t.ToString("d", CultureInfo.CurrentCulture), 11, Muted);
            var gx = plot.Left + plot.Width * k / 4;
            ctx.DrawText(label, new Point(Math.Clamp(gx - label.Width / 2, 0, w - label.Width), plot.Bottom + 8));
        }
        Point Map(DateTime at, long v) => new(
            plot.Left + plot.Width * (at.Ticks - minT) / (double)(maxT - minT),
            plot.Bottom - plot.Height * (v - minV) / (double)(maxV - minV));
        var ly = top - 4;
        var lx = plot.Right;
        foreach (var s in _series)
        {
            if (s.Points.Count == 0)
                continue;
            var pen = new Pen(new SolidColorBrush(s.Color), 2);
            var pts = s.Points.OrderBy(p => p.At).Select(p => Map(p.At, p.Value)).ToList();
            for (var i = 1; i < pts.Count; i++)
                ctx.DrawLine(pen, pts[i - 1], pts[i]);
            var idx = 0;
            foreach (var p in s.Points.OrderBy(p => p.At))
            {
                var pt = pts[idx++];
                if (ShowPoints)
                    ctx.DrawEllipse(new SolidColorBrush(s.Color), null, pt, 3.5, 3.5);
                _points.Add((p.At, p.Value, pt, s.Name));
            }
            var name = Text(s.Name, 11.5);
            lx -= name.Width + 26;
            ctx.FillRectangle(new SolidColorBrush(s.Color), new Rect(lx, ly - 10, 12, 4));
            ctx.DrawText(name, new Point(lx + 16, ly - 16));
        }
    }

    protected override (object? Tag, string? Tip) HitTest(Point p)
    {
        foreach (var q in _points)
            if (Math.Abs(q.P.X - p.X) < 6 && Math.Abs(q.P.Y - p.Y) < 6)
                return (q.At, $"{q.Series}\n{q.At:g}\n{Format(q.Value)}");
        return (null, null);
    }
}
