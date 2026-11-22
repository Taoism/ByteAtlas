using ByteAtlas.Analysis;
using ByteAtlas.Model;
using ByteAtlas.Scanning;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ByteAtlas.Export;

public enum ReportSectionKind { Summary, Chart, Folders, FileTypes, Owners, Ages, TopFiles }

/// <summary>One part of a report, in the order of the list.</summary>
public sealed class ReportSection
{
    public ReportSectionKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Tables: rows at most (Folders: all rows down to the depth of the export settings).</summary>
    public int Rows { get; set; } = 25;

    public static string Name(ReportSectionKind k) => k switch
    {
        ReportSectionKind.Summary => "Summary",
        ReportSectionKind.Chart => "Chart",
        ReportSectionKind.Folders => "Folders",
        ReportSectionKind.FileTypes => "File types",
        ReportSectionKind.Owners => "Owners",
        ReportSectionKind.Ages => "Age of files",
        _ => "Largest files",
    };
}

/// <summary>What a report holds and how it looks; saved as templates in the settings.</summary>
public sealed class ReportDefinition
{
    public string Name { get; set; } = "Standard";
    public string Title { get; set; } = "Disk space report";
    public string Notes { get; set; } = "";
    public string LogoPath { get; set; } = "";
    public string Footer { get; set; } = "";
    public string Paper { get; set; } = "A4";
    public bool Landscape { get; set; } = true;

    /// <summary>Folders section: levels below the reported folder.</summary>
    public int Depth { get; set; } = 2;

    public List<ReportSection> Sections { get; set; } =
    [
        new() { Kind = ReportSectionKind.Summary },
        new() { Kind = ReportSectionKind.Chart },
        new() { Kind = ReportSectionKind.Folders },
        new() { Kind = ReportSectionKind.FileTypes, Rows = 15 },
        new() { Kind = ReportSectionKind.TopFiles, Rows = 20 },
        new() { Kind = ReportSectionKind.Owners, Enabled = false, Rows = 10 },
        new() { Kind = ReportSectionKind.Ages, Enabled = false },
    ];

    public ReportDefinition Clone()
    {
        var c = (ReportDefinition)MemberwiseClone();
        c.Sections = [.. Sections.Select(s => new ReportSection { Kind = s.Kind, Enabled = s.Enabled, Rows = s.Rows })];
        return c;
    }
}

/// <summary>What the report is about, plus what only the app can give (the chart picture, type colours).</summary>
public sealed class ReportInput
{
    public required ScanSession Scan { get; init; }
    public required DirNode Folder { get; init; }
    public ExportSettings Table { get; init; } = new();
    public FileGroups Groups { get; init; } = new();
    public AgeIntervals Ages { get; init; } = new();
    public AgeBasis AgeBasis { get; init; } = AgeBasis.Modified;

    /// <summary>PNG of the chart (the app draws it); no chart section without it.</summary>
    public byte[]? ChartPng { get; init; }

    /// <summary>0xRRGGBB of a file type ("mp4") for the swatches of the file types table.</summary>
    public Func<string, uint>? TypeColor { get; init; }
}

public enum ReportAlign { Left, Center, Right }

/// <summary>Something drawn on a report page; coordinates in points from the top left.</summary>
public abstract record ReportElement(double X, double Y, double W, double H);

public sealed record ReportText(double X, double Y, double W, double H, string Text, double Size, bool Bold, uint Color, ReportAlign Align = ReportAlign.Left)
    : ReportElement(X, Y, W, H);

public sealed record ReportBox(double X, double Y, double W, double H, uint Fill) : ReportElement(X, Y, W, H);

public sealed record ReportImage(double X, double Y, double W, double H, byte[] Png) : ReportElement(X, Y, W, H);

public sealed class ReportPage
{
    public double Width { get; init; }
    public double Height { get; init; }
    public List<ReportElement> Elements { get; } = [];
}

/// <summary>
/// Lays a report out into pages (measured with the PDF fonts, so the preview in the app and the PDF break the
/// same way) and writes them as PDF.
/// </summary>
public static class Report
{
    private const double Margin = 36;
    private const double RowH = 13;
    private const uint Black = 0x1F2328, Muted = 0x6B6F76, Accent = 0x3D7FD6, BarBack = 0xDDE7F5, RowBack = 0xF4F5F7, Rule = 0xD5D8DC;

    public static (double W, double H) PaperSize(ReportDefinition d)
    {
        var (w, h) = d.Paper == "Letter" ? (612.0, 792.0) : (595.3, 841.9);
        return d.Landscape ? (h, w) : (w, h);
    }

    public static List<ReportPage> Layout(ReportDefinition def, ReportInput input)
    {
        PdfExporter.CheckFonts();
        var (pw, ph) = PaperSize(def);
        var usable = pw - 2 * Margin;
        using var g = XGraphics.CreateMeasureContext(new XSize(pw, ph), XGraphicsUnit.Point, XPageDirection.Downwards);
        var pages = new List<ReportPage>();
        ReportPage page = null!;
        double y = 0;
        var bottom = ph - Margin - 14; // room for the footer

        void NewPage()
        {
            page = new ReportPage { Width = pw, Height = ph };
            pages.Add(page);
            y = Margin;
            if (pages.Count > 1)
            {
                // running head: the title, small
                page.Elements.Add(new ReportText(Margin, y, usable, 12, def.Title, 8, false, Muted));
                y += 18;
            }
        }

        bool Fits(double h) => y + h <= bottom;

        void Text(string s, double size, bool bold, uint color, double h, ReportAlign align = ReportAlign.Left, double x = Margin, double? w = null)
        {
            var width = w ?? usable;
            page.Elements.Add(new ReportText(x, y, width, h, Fit(g, s, Font(size, bold), width), size, bold, color, align));
        }

        NewPage();
        // title block: logo on the right, title, what and when
        var logo = LoadLogo(def.LogoPath);
        var titleW = usable;
        if (logo is { } l)
        {
            var lh = Math.Min(48, l.H);
            var lw = l.W * lh / l.H;
            if (lw > 160)
                (lw, lh) = (160, l.H * 160 / l.W);
            page.Elements.Add(new ReportImage(pw - Margin - lw, y, lw, lh, l.Png));
            titleW = usable - lw - 12;
        }
        Text(def.Title, 18, true, Black, 24, w: titleW);
        y += 26;
        var s = input.Scan;
        var d = input.Folder;
        Text(d.FullPath, 9.5, false, Black, 13, w: titleW);
        y += 13;
        var when = s.IndexFile != null ? $"Index file from {s.ScanDate:g}" : $"Scanned {s.ScanDate:g}";
        Text($"{when}  ·  report made {DateTime.Now:g}", 8.5, false, Muted, 12, w: titleW);
        y += 16;
        if (logo is { } l2)
            y = Math.Max(y, Margin + Math.Min(48, l2.H) + 6);
        if (def.Notes.Trim().Length > 0)
        {
            foreach (var line in Wrap(g, def.Notes.Trim(), Font(9, false), usable))
            {
                if (!Fits(12))
                    NewPage();
                Text(line, 9, false, Black, 12);
                y += 12;
            }
            y += 6;
        }
        page.Elements.Add(new ReportBox(Margin, y, usable, 1, Rule));
        y += 10;

        void Heading(string text, double need)
        {
            if (!Fits(28 + need))
                NewPage();
            y += 6;
            Text(text, 12, true, Black, 16);
            y += 20;
        }

        void Table(ExportTable t, int maxRows)
        {
            var rows = maxRows > 0 ? t.Rows.Take(maxRows).ToList() : t.Rows;
            var font = Font(8, false);
            var bold = Font(8, true);
            var widths = ColumnWidths(t, g, font, usable);

            void Header()
            {
                var x = Margin;
                for (var i = 0; i < t.Columns.Count; i++)
                {
                    var right = t.Columns[i].Kind is not (ColumnKind.Text or ColumnKind.Date);
                    page.Elements.Add(new ReportText(x + 3, y, widths[i] - 6, RowH, Fit(g, t.Columns[i].Header, bold, widths[i] - 6), 8, true, Black, right ? ReportAlign.Right : ReportAlign.Left));
                    x += widths[i];
                }
                y += RowH;
                page.Elements.Add(new ReportBox(Margin, y, usable, 0.5, Rule));
                y += 2;
            }

            Header();
            var n = 0;
            foreach (var r in rows)
            {
                if (!Fits(RowH))
                {
                    NewPage();
                    Header();
                }
                if (n++ % 2 == 1)
                    page.Elements.Add(new ReportBox(Margin, y, usable, RowH, RowBack));
                var x = Margin;
                for (var i = 0; i < r.Values.Length && i < widths.Length; i++)
                {
                    var kind = t.Columns[i].Kind;
                    var text = Exporters.Cell(r.Values[i], kind, input.Table);
                    var isBold = r.IsFolder && i == 0;
                    if (i == 0)
                    {
                        var bx = x + 3 + (t.IsTree ? r.Level * 10 : 0);
                        if (r.Swatch is { } sw)
                        {
                            page.Elements.Add(new ReportBox(bx, y + 3, 7, 7, sw));
                            bx += 11;
                        }
                        if (r.Bar is { } b)
                        {
                            page.Elements.Add(new ReportBox(bx, y + 4, 34, RowH - 8, BarBack));
                            page.Elements.Add(new ReportBox(bx, y + 4, 34 * Math.Clamp(b, 0, 1), RowH - 8, Accent));
                            bx += 40;
                        }
                        var w = widths[i] - (bx - x) - 3;
                        page.Elements.Add(new ReportText(bx, y, w, RowH, Fit(g, text, isBold ? bold : font, w), 8, isBold, Black));
                    }
                    else
                    {
                        var right = kind is not (ColumnKind.Text or ColumnKind.Date);
                        page.Elements.Add(new ReportText(x + 3, y, widths[i] - 6, RowH, Fit(g, text, font, widths[i] - 6), 8, false, Black, right ? ReportAlign.Right : ReportAlign.Left));
                    }
                    x += widths[i];
                }
                y += RowH;
            }
            if (maxRows > 0 && t.Rows.Count > maxRows)
            {
                if (!Fits(RowH))
                    NewPage();
                Text($"… {t.Rows.Count - maxRows:N0} more", 8, false, Muted, RowH);
                y += RowH;
            }
            y += 8;
        }

        foreach (var sec in def.Sections.Where(x => x.Enabled))
        {
            switch (sec.Kind)
            {
                case ReportSectionKind.Summary:
                {
                    Heading("Summary", 60);
                    var facts = new List<(string, string)>
                    {
                        ("Size", SizeFormat.Format(d.Size, input.Table.Unit, input.Table.Decimals)),
                        ("Allocated", SizeFormat.Format(d.Allocated, input.Table.Unit, input.Table.Decimals)),
                        ("Files", SizeFormat.Count(d.FileCount)),
                        ("Folders", SizeFormat.Count(d.FolderCount)),
                    };
                    if (s.VolumeTotal > 0)
                        facts.Add(("Free on the drive", $"{SizeFormat.Format(s.VolumeFree)} of {SizeFormat.Format(s.VolumeTotal)} ({SizeFormat.Percent((double)s.VolumeFree / s.VolumeTotal)})"));
                    if (d.Parent != null && s.Root.Size > 0)
                        facts.Add(("Of the scan", SizeFormat.Percent((double)d.Size / s.Root.Size)));
                    var colW = usable / 3;
                    for (var i = 0; i < facts.Count; i += 3)
                    {
                        if (!Fits(30))
                            NewPage();
                        for (var k = 0; k < 3 && i + k < facts.Count; k++)
                        {
                            var (label, value) = facts[i + k];
                            Text(label, 8, false, Muted, 11, x: Margin + k * colW, w: colW - 10);
                            y += 11;
                            Text(value, 12, true, Black, 16, x: Margin + k * colW, w: colW - 10);
                            y -= 11;
                        }
                        y += 32;
                    }
                    break;
                }
                case ReportSectionKind.Chart when input.ChartPng != null && PngSize(input.ChartPng) is { } size:
                {
                    var w = usable;
                    var h = w * size.H / size.W;
                    var maxH = Math.Min(bottom - Margin - 40, usable * 0.6);
                    if (h > maxH)
                        (w, h) = (maxH * size.W / size.H, maxH);
                    // the rest of this page when at least half the chart fits there: no half-empty page
                    var left = bottom - y - 40;
                    if (left < h && left >= h / 2)
                        (w, h) = (left * size.W / size.H, left);
                    Heading("Chart", h);
                    page.Elements.Add(new ReportImage(Margin + (usable - w) / 2, y, w, h, input.ChartPng));
                    y += h + 10;
                    break;
                }
                case ReportSectionKind.Folders:
                {
                    var o = input.Table.Clone();
                    o.MaxDepth = Math.Max(0, def.Depth);
                    Heading("Folders", 3 * RowH);
                    Table(ExportBuilder.Tree(s, d, o), 0);
                    break;
                }
                case ReportSectionKind.FileTypes:
                {
                    var t = ExportBuilder.Stats("", "Type", Statistics.ByExtension(d, input.Groups), s, d, true);
                    if (input.TypeColor is { } color)
                        for (var i = 0; i < t.Rows.Count; i++)
                            t.Rows[i] = new ExportRow { Values = t.Rows[i].Values, Bar = t.Rows[i].Bar, Swatch = color(t.Rows[i].Values[0] as string is "(none)" ? "" : t.Rows[i].Values[0] as string ?? "") };
                    Heading("File types", 3 * RowH);
                    Table(t, sec.Rows);
                    break;
                }
                case ReportSectionKind.Owners when s.Remote == null:
                    Heading("Owners", 3 * RowH);
                    Table(ExportBuilder.Stats("", "Owner", Statistics.ByOwner(d, s.Owners), s, d), sec.Rows);
                    break;
                case ReportSectionKind.Ages:
                    Heading("Age of files", 3 * RowH);
                    Table(ExportBuilder.Stats("", "Age", Statistics.ByAge(d, input.Ages, input.AgeBasis), s, d), 0);
                    break;
                case ReportSectionKind.TopFiles:
                    Heading($"Largest {sec.Rows} files", 3 * RowH);
                    Table(ExportBuilder.Items("", Statistics.TopFiles(d, sec.Rows), s, [TreeColumn.Name, TreeColumn.Size, TreeColumn.Modified, TreeColumn.FullPath]), 0);
                    break;
            }
        }

        // footer on every page, now that the count is known
        for (var i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var fy = ph - Margin;
            if (def.Footer.Trim().Length > 0)
                p.Elements.Add(new ReportText(Margin, fy, usable * 0.75, 12, Fit(g, def.Footer.Trim(), Font(8, false), usable * 0.75), 8, false, Muted));
            p.Elements.Add(new ReportText(Margin, fy, usable, 12, $"Page {i + 1} of {pages.Count}", 8, false, Muted, ReportAlign.Right));
        }
        return pages;
    }

    public static void WritePdf(IReadOnlyList<ReportPage> pages, string path, string title)
    {
        PdfExporter.CheckFonts();
        using var doc = new PdfDocument();
        doc.Info.Title = title;
        doc.Info.Creator = "ByteAtlas";
        var images = new Dictionary<byte[], XImage>(ReferenceEqualityComparer.Instance);
        try
        {
            foreach (var p in pages)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(p.Width);
                page.Height = XUnit.FromPoint(p.Height);
                using var g = XGraphics.FromPdfPage(page);
                foreach (var e in p.Elements)
                {
                    switch (e)
                    {
                        case ReportBox b:
                            g.DrawRectangle(new XSolidBrush(Color(b.Fill)), b.X, b.Y, b.W, b.H);
                            break;
                        case ReportText t:
                            var format = t.Align switch
                            {
                                ReportAlign.Right => XStringFormats.CenterRight,
                                ReportAlign.Center => XStringFormats.Center,
                                _ => XStringFormats.CenterLeft,
                            };
                            g.DrawString(t.Text, Font(t.Size, t.Bold), new XSolidBrush(Color(t.Color)), new XRect(t.X, t.Y, t.W, t.H), format);
                            break;
                        case ReportImage im:
                            if (!images.TryGetValue(im.Png, out var x))
                                images[im.Png] = x = XImage.FromStream(new MemoryStream(im.Png));
                            g.DrawImage(x, im.X, im.Y, im.W, im.H);
                            break;
                    }
                }
            }
            doc.Save(path);
        }
        finally
        {
            foreach (var x in images.Values)
                x.Dispose();
        }
    }

    private static XColor Color(uint rgb) => XColor.FromArgb((int)((rgb >> 16) & 0xFF), (int)((rgb >> 8) & 0xFF), (int)(rgb & 0xFF));

    private static XFont Font(double size, bool bold) => bold ? new XFont("Segoe UI", size, XFontStyleEx.Bold) : new XFont("Segoe UI", size);

    private static (byte[] Png, double W, double H)? LoadLogo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            return PngSize(bytes) is { } s ? (bytes, s.W, s.H) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Width and height from a PNG header (only PNG pictures are placed: the app converts others).</summary>
    public static (double W, double H)? PngSize(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G')
            return null;
        static int Be(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        var w = Be(png, 16);
        var h = Be(png, 20);
        return w > 0 && h > 0 ? (w, h) : null;
    }

    private static List<string> Wrap(XGraphics g, string text, XFont f, double width)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && g.MeasureString(next, f).Width > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                    line = next;
            }
            lines.Add(line);
        }
        return lines;
    }

    private static double[] ColumnWidths(ExportTable t, XGraphics g, XFont font, double usable)
    {
        var w = new double[t.Columns.Count];
        for (var i = 1; i < w.Length; i++)
        {
            var max = g.MeasureString(t.Columns[i].Header, font).Width;
            foreach (var r in t.Rows.Take(300))
                max = Math.Max(max, g.MeasureString(r.Values[i]?.ToString() ?? "", font).Width * (t.Columns[i].Kind == ColumnKind.Size ? 0.9 : 1));
            w[i] = Math.Clamp(max + 12, 40, t.Columns[i].Kind == ColumnKind.Text ? 220 : 90);
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
