using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace ByteAtlas.Export;

/// <summary>A formatted PDF report: title block, then the table with size bars (landscape A4, paged).</summary>
public static class PdfExporter
{
    private static bool _fontsReady;

    private static void InitFonts()
    {
        if (_fontsReady)
            return;
        _fontsReady = true;
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();
    }

    /// <summary>
    /// Uses Segoe UI on Windows, DejaVu / Liberation / Noto / Helvetica elsewhere (any family name maps to these);
    /// as a last resort any TrueType font in the font folders.
    /// </summary>
    private sealed class SystemFontResolver : IFontResolver
    {
        private static readonly string[] Regular =
        [
            @"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf", "/usr/share/fonts/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf", "/usr/share/fonts/liberation-sans-fonts/LiberationSans-Regular.ttf",
            "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf", "/usr/share/fonts/google-noto/NotoSans-Regular.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
        ];

        private static readonly string[] Bold =
        [
            @"C:\Windows\Fonts\segoeuib.ttf", @"C:\Windows\Fonts\arialbd.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans-Bold.ttf", "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/truetype/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf", "/usr/share/fonts/liberation-sans-fonts/LiberationSans-Bold.ttf",
            "/usr/share/fonts/truetype/noto/NotoSans-Bold.ttf", "/usr/share/fonts/google-noto/NotoSans-Bold.ttf",
            "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        ];

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
            new(isBold ? "ba-bold" : "ba-regular");

        public byte[]? GetFont(string faceName)
        {
            var file = (faceName == "ba-bold" ? Bold : Regular).FirstOrDefault(File.Exists)
                ?? Regular.FirstOrDefault(File.Exists)
                ?? AnyFont()
                ?? throw new IOException("No TrueType font found for the PDF report. Install a font package (fonts-dejavu-core, dejavu-sans-fonts, …).");
            return File.ReadAllBytes(file);
        }

        private static string? AnyFont()
        {
            string[] folders = ["/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/fonts")];
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            foreach (var dir in folders.Where(Directory.Exists))
            {
                // prefer a plain sans-serif regular face
                var all = Directory.EnumerateFiles(dir, "*.ttf", options).ToList();
                var pick = all.FirstOrDefault(f => Path.GetFileName(f).Contains("Sans-Regular", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("DejaVuSans.ttf", StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault();
                if (pick != null)
                    return pick;
            }
            return null;
        }
    }

    public static void Write(ExportTable t, string path, ExportSettings o)
    {
        InitFonts();
        using var doc = new PdfDocument();
        doc.Info.Title = t.Title;
        doc.Info.Creator = "ByteAtlas";

        const double Margin = 36;
        var font = new XFont("Segoe UI", 8);
        var bold = new XFont("Segoe UI", 8, XFontStyleEx.Bold);
        var title = new XFont("Segoe UI", 14, XFontStyleEx.Bold);
        var small = new XFont("Segoe UI", 8);
        var accent = new XSolidBrush(XColor.FromArgb(0x3D, 0x7F, 0xD6));
        var barBack = new XSolidBrush(XColor.FromArgb(0xDD, 0xE7, 0xF5));
        var rowBack = new XSolidBrush(XColor.FromArgb(0xF4, 0xF5, 0xF7));
        var muted = new XSolidBrush(XColor.FromArgb(0x6B, 0x6F, 0x76));
        var line = new XPen(XColor.FromArgb(0xD5, 0xD8, 0xDC), 0.5);
        const double RowH = 13;

        PdfPage? page = null;
        XGraphics? g = null;
        double y = 0;
        var pageNo = 0;
        double[] widths = [];
        double usable = 0;

        void NewPage()
        {
            g?.Dispose();
            page = doc.AddPage();
            page.Width = XUnit.FromMillimeter(297);
            page.Height = XUnit.FromMillimeter(210);
            g = XGraphics.FromPdfPage(page);
            pageNo++;
            y = Margin;
            usable = page.Width.Point - 2 * Margin;
            if (pageNo == 1)
            {
                g.DrawString(t.Title, title, XBrushes.Black, new XRect(Margin, y, usable, 20), XStringFormats.TopLeft);
                y += 22;
                foreach (var s in t.Subtitle)
                {
                    g.DrawString(s, small, muted, new XRect(Margin, y, usable, 12), XStringFormats.TopLeft);
                    y += 12;
                }
                y += 8;
                widths = ColumnWidths(t, g, font, usable);
            }
            // header row
            var x = Margin;
            for (var i = 0; i < t.Columns.Count; i++)
            {
                var right = t.Columns[i].Kind is not (ColumnKind.Text or ColumnKind.Date);
                g.DrawString(t.Columns[i].Header, bold, XBrushes.Black, new XRect(x + 3, y, widths[i] - 6, RowH), right ? XStringFormats.CenterRight : XStringFormats.CenterLeft);
                x += widths[i];
            }
            y += RowH;
            g.DrawLine(line, Margin, y, Margin + usable, y);
            y += 2;
            g.DrawString($"Page {pageNo}", small, muted, new XRect(Margin, page.Height.Point - Margin + 8, usable, 12), XStringFormats.TopRight);
        }

        NewPage();
        var n = 0;
        foreach (var r in t.Rows)
        {
            if (y + RowH > page!.Height.Point - Margin)
                NewPage();
            if (n++ % 2 == 1)
                g!.DrawRectangle(rowBack, Margin, y, usable, RowH);
            var x = Margin;
            for (var i = 0; i < r.Values.Length; i++)
            {
                var kind = t.Columns[i].Kind;
                var text = Exporters.Cell(r.Values[i], kind, o);
                var f = r.IsFolder && i == 0 ? bold : font;
                if (i == 0)
                {
                    var indent = t.IsTree ? r.Level * 10 : 0;
                    var bx = x + 3 + indent;
                    if (r.Bar is { } b)
                    {
                        g!.DrawRectangle(barBack, bx, y + 4, 34, RowH - 8);
                        g.DrawRectangle(accent, bx, y + 4, 34 * Math.Clamp(b, 0, 1), RowH - 8);
                        bx += 40;
                    }
                    g!.DrawString(Fit(g, text, f, widths[i] - (bx - x) - 3), f, XBrushes.Black, new XRect(bx, y, widths[i] - (bx - x) - 3, RowH), XStringFormats.CenterLeft);
                }
                else
                {
                    var right = kind is not (ColumnKind.Text or ColumnKind.Date);
                    g!.DrawString(Fit(g, text, f, widths[i] - 6), f, XBrushes.Black, new XRect(x + 3, y, widths[i] - 6, RowH), right ? XStringFormats.CenterRight : XStringFormats.CenterLeft);
                }
                x += widths[i];
            }
            y += RowH;
        }
        g?.Dispose();
        doc.Save(path);
    }

    private static double[] ColumnWidths(ExportTable t, XGraphics g, XFont font, double usable)
    {
        var w = new double[t.Columns.Count];
        for (var i = 1; i < w.Length; i++)
        {
            var max = g.MeasureString(t.Columns[i].Header, font).Width;
            foreach (var r in t.Rows.Take(400))
                max = Math.Max(max, g.MeasureString(r.Values[i]?.ToString() ?? "", font).Width * (t.Columns[i].Kind == ColumnKind.Size ? 0.9 : 1));
            w[i] = Math.Clamp(max + 12, 40, t.Columns[i].Kind == ColumnKind.Text ? 160 : 90);
        }
        w[0] = Math.Max(160, usable - w.Skip(1).Sum());
        var scale = usable / w.Sum();
        if (scale < 1)
            for (var i = 0; i < w.Length; i++)
                w[i] *= scale;
        return w;
    }

    private static string Fit(XGraphics g, string text, XFont f, double width)
    {
        if (width <= 0 || g.MeasureString(text, f).Width <= width)
            return text;
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (g.MeasureString(text[..mid] + "…", f).Width <= width)
                lo = mid;
            else
                hi = mid - 1;
        }
        return text[..lo] + "…";
    }
}
