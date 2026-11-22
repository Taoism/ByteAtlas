using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ByteAtlas.Export;

namespace ByteAtlas.Controls;

/// <summary>
/// The pages of a report as they will be printed: white sheets below each other, drawn from the same layout the
/// PDF is written from. Put it in a ScrollViewer; <see cref="Zoom"/> 0 fits the page width.
/// </summary>
public sealed class ReportPreview : Control
{
    private const double Gap = 18;
    private IReadOnlyList<ReportPage> _pages = [];
    private readonly Dictionary<byte[], Bitmap> _images = new(ReferenceEqualityComparer.Instance);
    private static readonly FontFamily PageFont = OperatingSystem.IsWindows() ? new FontFamily("Segoe UI") : FontFamily.Default;

    public IReadOnlyList<ReportPage> Pages
    {
        get => _pages;
        set
        {
            foreach (var b in _images.Values)
                b.Dispose();
            _images.Clear();
            _pages = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private double _zoom;

    /// <summary>Scale of the pages (1 = 100 %, at 96 dpi a point is 1.33 px); 0 = fit the width.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>The available width the "fit" zoom uses (the scroll viewer's viewport).</summary>
    public double FitWidth { get; set; } = 800;

    public double Scale => _pages.Count == 0 ? 1 : _zoom > 0 ? _zoom * 96 / 72 : Math.Max(0.1, (FitWidth - 2 * Gap) / _pages.Max(p => p.Width));

    /// <summary>Top of a page in this control (to scroll to it).</summary>
    public double PageTop(int index)
    {
        var y = Gap;
        for (var i = 0; i < index && i < _pages.Count; i++)
            y += _pages[i].Height * Scale + Gap;
        return y;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_pages.Count == 0)
            return new Size(0, 0);
        var s = Scale;
        return new Size(_pages.Max(p => p.Width) * s + 2 * Gap, _pages.Sum(p => p.Height * s + Gap) + Gap);
    }

    public override void Render(DrawingContext ctx)
    {
        var s = Scale;
        var y = Gap;
        var shadow = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0));
        var clip = new Rect(Bounds.Size);
        foreach (var page in _pages)
        {
            var w = page.Width * s;
            var h = page.Height * s;
            var x = Math.Max(Gap, (Bounds.Width - w) / 2);
            var rect = new Rect(x, y, w, h);
            // only pages in view are drawn element by element
            if (rect.Intersects(clip))
            {
                ctx.FillRectangle(shadow, rect.Translate(new Vector(3, 3)));
                ctx.FillRectangle(Brushes.White, rect);
                using (ctx.PushClip(rect))
                using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(x, y)))
                {
                    foreach (var e in page.Elements)
                        Draw(ctx, e);
                }
            }
            y += h + Gap;
        }
    }

    private void Draw(DrawingContext ctx, ReportElement e)
    {
        switch (e)
        {
            case ReportBox b:
                ctx.FillRectangle(new SolidColorBrush(Rgb(b.Fill)), new Rect(b.X, b.Y, b.W, b.H));
                break;
            case ReportText t:
                var ft = new FormattedText(t.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(PageFont, FontStyle.Normal, t.Bold ? FontWeight.Bold : FontWeight.Normal), t.Size, new SolidColorBrush(Rgb(t.Color)));
                var tx = t.Align switch
                {
                    ReportAlign.Right => t.X + t.W - ft.Width,
                    ReportAlign.Center => t.X + (t.W - ft.Width) / 2,
                    _ => t.X,
                };
                ctx.DrawText(ft, new Point(tx, t.Y + (t.H - ft.Height) / 2));
                break;
            case ReportImage im:
                if (!_images.TryGetValue(im.Png, out var bmp))
                    _images[im.Png] = bmp = new Bitmap(new MemoryStream(im.Png));
                ctx.DrawImage(bmp, new Rect(im.X, im.Y, im.W, im.H));
                break;
        }
    }

    private static Color Rgb(uint c) => Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
}
