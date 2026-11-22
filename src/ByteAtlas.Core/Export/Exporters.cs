using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using ByteAtlas.Model;

namespace ByteAtlas.Export;

public enum ExportFormat
{
    Text,
    Csv,
    Html,
    Excel,
    Pdf,
}

/// <summary>Writes an <see cref="ExportTable"/> as text, CSV, HTML, Excel (.xlsx) or PDF.</summary>
public static class Exporters
{
    public static ExportFormat FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => ExportFormat.Csv,
        ".htm" or ".html" => ExportFormat.Html,
        ".xlsx" => ExportFormat.Excel,
        ".pdf" => ExportFormat.Pdf,
        _ => ExportFormat.Text,
    };

    public static void Write(ExportTable t, string path, ExportSettings o, ExportFormat? format = null)
    {
        switch (format ?? FormatFromPath(path))
        {
            case ExportFormat.Csv:
                File.WriteAllText(path, Csv(t, o), new UTF8Encoding(true));
                break;
            case ExportFormat.Html:
                File.WriteAllText(path, Html(t, o), new UTF8Encoding(false));
                break;
            case ExportFormat.Excel:
                Xlsx(t, path, o);
                break;
            case ExportFormat.Pdf:
                PdfExporter.Write(t, path, o);
                break;
            default:
                File.WriteAllText(path, Text(t, o), new UTF8Encoding(true));
                break;
        }
    }

    public static string Cell(object? v, ColumnKind kind, ExportSettings o) => v switch
    {
        null => "",
        long l when kind == ColumnKind.Size => SizeFormat.Format(l, o.Unit, o.Decimals),
        long l => SizeFormat.Count(l),
        int i => SizeFormat.Count(i),
        double d when kind == ColumnKind.Percent => SizeFormat.Percent(d),
        double d => d.ToString("N" + o.Decimals, CultureInfo.CurrentCulture),
        DateTime dt => SizeFormat.Date(dt, o.DateFormat),
        _ => v.ToString() ?? "",
    };

    // ---------------- text ----------------

    public static string Text(ExportTable t, ExportSettings o)
    {
        var sb = new StringBuilder();
        sb.AppendLine(t.Title);
        foreach (var s in t.Subtitle)
            sb.AppendLine(s);
        sb.AppendLine();
        var cells = t.Rows.Select(r => r.Values.Select((v, i) => Cell(v, t.Columns[i].Kind, o)).ToArray()).ToList();
        var widths = new int[t.Columns.Count];
        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] = t.Columns[i].Header.Length;
            for (var r = 0; r < cells.Count; r++)
            {
                var len = cells[r][i].Length + (i == 0 && t.IsTree ? t.Rows[r].Level * 2 : 0);
                widths[i] = Math.Max(widths[i], len);
            }
            if (widths[i] > (i == 0 ? 120 : 40))
                widths[i] = i == 0 ? 120 : 40;
        }
        void Line(IReadOnlyList<string> c, int level)
        {
            for (var i = 0; i < c.Count; i++)
            {
                var text = i == 0 && t.IsTree ? new string(' ', level * 2) + c[i] : c[i];
                var right = t.Columns[i].Kind is not (ColumnKind.Text or ColumnKind.Date);
                sb.Append(right ? text.PadLeft(widths[i]) : text.PadRight(widths[i]));
                if (i < c.Count - 1)
                    sb.Append("  ");
            }
            sb.AppendLine();
        }
        Line([.. t.Columns.Select(c => c.Header)], 0);
        sb.AppendLine(new string('-', widths.Sum() + 2 * (widths.Length - 1)));
        for (var r = 0; r < cells.Count; r++)
            Line(cells[r], t.Rows[r].Level);
        return sb.ToString();
    }

    // ---------------- CSV ----------------

    public static string Csv(ExportTable t, ExportSettings o)
    {
        var sep = o.CsvSeparator;
        var sb = new StringBuilder();
        string Q(string s) => s.IndexOfAny([sep, '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var cols = t.Columns.Select(c => c.Header).ToList();
        if (t.IsTree)
            cols.Insert(0, "Level");
        sb.AppendLine(string.Join(sep, cols.Select(Q)));
        foreach (var r in t.Rows)
        {
            var vals = r.Values.Select((v, i) => o.RawNumbers ? Raw(v) : Cell(v, t.Columns[i].Kind, o)).ToList();
            if (t.IsTree)
                vals.Insert(0, r.Level.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(string.Join(sep, vals.Select(Q)));
        }
        return sb.ToString();
    }

    private static string Raw(object? v) => v switch
    {
        null => "",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        DateTime dt => dt == DateTime.MinValue ? "" : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    // ---------------- HTML ----------------

    public static string Html(ExportTable t, ExportSettings o)
    {
        var sb = new StringBuilder();
        string E(string s) => WebUtility.HtmlEncode(s);
        sb.Append("""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
            :root{--bg:#fff;--fg:#1d1f23;--muted:#6b6f76;--edge:#dcdee2;--bar:#3d7fd6;--bar2:#cfe0f6;--row:#f6f7f9}
            @media (prefers-color-scheme:dark){:root{--bg:#1b1c1f;--fg:#e8e8ea;--muted:#9a9ca1;--edge:#34363b;--bar:#3d7fd6;--bar2:#26344a;--row:#222428}}
            body{background:var(--bg);color:var(--fg);font:13px/1.4 "Segoe UI",system-ui,sans-serif;margin:24px}
            h1{font-size:18px;margin:0 0 6px}.sub{color:var(--muted);margin:0 0 2px}
            table{border-collapse:collapse;margin-top:16px;min-width:60%}
            th{text-align:left;font-weight:600;border-bottom:1px solid var(--edge);padding:6px 10px;white-space:nowrap}
            td{padding:4px 10px;border-bottom:1px solid var(--edge);white-space:nowrap}
            tr:nth-child(even) td{background:var(--row)}td.n{text-align:right;font-variant-numeric:tabular-nums}
            .bar{display:inline-block;height:8px;width:90px;background:var(--bar2);vertical-align:middle;margin-right:8px;border-radius:2px;overflow:hidden}
            .bar i{display:block;height:100%;background:var(--bar)}.f{font-weight:600}
            footer{color:var(--muted);margin-top:16px;font-size:11px}
            </style>
            """);
        sb.Append("<title>").Append(E(t.Title)).Append("</title></head><body>\n<h1>").Append(E(t.Title)).Append("</h1>\n");
        foreach (var s in t.Subtitle)
            sb.Append("<p class=\"sub\">").Append(E(s)).Append("</p>\n");
        sb.Append("<table><thead><tr>");
        foreach (var c in t.Columns)
            sb.Append("<th>").Append(E(c.Header)).Append("</th>");
        sb.Append("</tr></thead><tbody>\n");
        foreach (var r in t.Rows)
        {
            sb.Append("<tr>");
            for (var i = 0; i < r.Values.Length; i++)
            {
                var kind = t.Columns[i].Kind;
                var text = E(Cell(r.Values[i], kind, o));
                if (i == 0)
                {
                    var pad = t.IsTree ? r.Level * 18 : 0;
                    var bar = r.Bar is { } b ? $"<span class=\"bar\"><i style=\"width:{Math.Clamp(b, 0, 1) * 100:0.#}%\"></i></span>" : "";
                    sb.Append(CultureInfo.InvariantCulture, $"<td style=\"padding-left:{10 + pad}px\"{(r.IsFolder ? " class=\"f\"" : "")}>{bar}{text}</td>");
                }
                else
                    sb.Append(kind is ColumnKind.Text or ColumnKind.Date ? "<td>" : "<td class=\"n\">").Append(text).Append("</td>");
            }
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody></table>\n<footer>Created with ByteAtlas on ").Append(E(DateTime.Now.ToString("g", CultureInfo.CurrentCulture))).Append("</footer>\n</body></html>\n");
        return sb.ToString();
    }

    // ---------------- Excel (.xlsx, written by hand: no dependency) ----------------

    public static void Xlsx(ExportTable t, string path, ExportSettings o)
    {
        if (File.Exists(path))
            File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Entry("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
            """);
        Entry("_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        var sheetName = SecurityElement.Escape(new string(t.Title.Where(c => !"[]:*?/\\".Contains(c)).Take(31).ToArray()).Trim() is { Length: > 0 } n ? n : "ByteAtlas");
        Entry("xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{sheetName}" sheetId="1" r:id="rId1"/></sheets></workbook>
            """);
        Entry("xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
            """);
        // styles: 0 normal, 1 bold, 2 number #,##0, 3 size 0.0, 4 percent, 5 date, 6 title, 7 bold size
        Entry("xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
            <numFmts count="2"><numFmt numFmtId="164" formatCode="#,##0.0"/><numFmt numFmtId="165" formatCode="yyyy-mm-dd hh:mm"/></numFmts>
            <fonts count="3"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="14"/><name val="Calibri"/></font></fonts>
            <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
            <borders count="1"><border/></borders>
            <cellStyleXfs count="1"><xf/></cellStyleXfs>
            <cellXfs count="8"><xf/><xf fontId="1" applyFont="1"/><xf numFmtId="3" applyNumberFormat="1"/><xf numFmtId="164" applyNumberFormat="1"/><xf numFmtId="10" applyNumberFormat="1"/><xf numFmtId="165" applyNumberFormat="1"/><xf fontId="2" applyFont="1"/><xf fontId="1" numFmtId="164" applyFont="1" applyNumberFormat="1"/></cellXfs>
            </styleSheet>
            """);

        // sizes as numbers in a fixed unit (Excel cannot switch units per cell); raw = bytes
        var unit = o.RawNumbers ? SizeUnit.Bytes : o.Unit == SizeUnit.Auto ? SizeUnit.MB : o.Unit;
        var div = unit == SizeUnit.Bytes ? 1.0 : Math.Pow(1024, (int)unit - 1);
        var unitLabel = unit == SizeUnit.Bytes ? "Bytes" : unit.ToString();

        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetPr><outlinePr summaryBelow="0"/></sheetPr>""");
        sb.Append("<cols>");
        for (var i = 0; i < t.Columns.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{(i == 0 ? 50 : t.Columns[i].Kind == ColumnKind.Date ? 18 : 14)}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");
        var row = 1;
        void Str(StringBuilder b, int r, int c, string s, int style = 0) =>
            b.Append(CultureInfo.InvariantCulture, $"<c r=\"{Ref(c, r)}\" t=\"inlineStr\"{(style > 0 ? $" s=\"{style}\"" : "")}><is><t xml:space=\"preserve\">{SecurityElement.Escape(Clean(s))}</t></is></c>");
        sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{row}\">");
        Str(sb, row, 0, t.Title, 6);
        sb.Append("</row>");
        row++;
        foreach (var s in t.Subtitle)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{row}\">");
            Str(sb, row, 0, s);
            sb.Append("</row>");
            row++;
        }
        row++;
        var headerRow = row;
        sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{row}\">");
        for (var i = 0; i < t.Columns.Count; i++)
            Str(sb, row, i, t.Columns[i].Kind == ColumnKind.Size ? $"{t.Columns[i].Header} ({unitLabel})" : t.Columns[i].Header, 1);
        sb.Append("</row>");
        row++;
        foreach (var r in t.Rows)
        {
            var level = t.IsTree ? Math.Min(7, r.Level) : 0;
            sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{row}\"{(level > 0 ? $" outlineLevel=\"{level}\"" : "")}>");
            for (var i = 0; i < r.Values.Length; i++)
            {
                var v = r.Values[i];
                var kind = t.Columns[i].Kind;
                var cref = Ref(i, row);
                switch (v)
                {
                    case null:
                        break;
                    case long l when kind == ColumnKind.Size:
                        var style = r.IsFolder && t.IsTree ? 7 : unit == SizeUnit.Bytes ? 2 : 3;
                        sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{cref}\" s=\"{style}\"><v>{(l / div).ToString("0.###", CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    case long l:
                        sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{cref}\" s=\"2\"><v>{l}</v></c>");
                        break;
                    case int n2:
                        sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{cref}\" s=\"2\"><v>{n2}</v></c>");
                        break;
                    case double d:
                        sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{cref}\" s=\"{(kind == ColumnKind.Percent ? 4 : 3)}\"><v>{d.ToString("0.#####", CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    case DateTime dt when dt != DateTime.MinValue:
                        sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{cref}\" s=\"5\"><v>{dt.ToOADate().ToString("0.######", CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    case DateTime:
                        break;
                    default:
                        var text = i == 0 && t.IsTree ? new string(' ', r.Level * 2) + v : v.ToString() ?? "";
                        Str(sb, row, i, text, r.IsFolder && i == 0 && t.IsTree ? 1 : 0);
                        break;
                }
            }
            sb.Append("</row>");
            row++;
        }
        sb.Append("</sheetData>");
        if (t.Rows.Count > 0)
            sb.Append(CultureInfo.InvariantCulture, $"<autoFilter ref=\"A{headerRow}:{Ref(t.Columns.Count - 1, row - 1)}\"/>");
        sb.Append("</worksheet>");
        Entry("xl/worksheets/sheet1.xml", sb.ToString());
    }

    private static string Clean(string s)
    {
        // XML 1.0 does not allow most control characters
        if (!s.Any(c => c < 0x20 && c != '\t' && c != '\n' && c != '\r'))
            return s;
        return new string(s.Where(c => c >= 0x20 || c == '\t' || c == '\n' || c == '\r').ToArray());
    }

    private static string Ref(int col, int row)
    {
        var name = "";
        col++;
        while (col > 0)
        {
            var m = (col - 1) % 26;
            name = (char)('A' + m) + name;
            col = (col - 1) / 26;
        }
        return name + row.ToString(CultureInfo.InvariantCulture);
    }
}
