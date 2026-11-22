using System.Globalization;

namespace ByteAtlas.Model;

public enum SizeUnit
{
    Auto,
    Bytes,
    KB,
    MB,
    GB,
    TB,
}

/// <summary>Size and number formatting shared by the views and exports (1 KB = 1024 bytes).</summary>
public static class SizeFormat
{
    private static readonly string[] Units = ["Bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, SizeUnit unit = SizeUnit.Auto, int decimals = 1)
    {
        var neg = bytes < 0;
        var v = (double)Math.Abs(bytes);
        int idx;
        if (unit == SizeUnit.Auto)
        {
            idx = 0;
            while (v >= 1024 && idx < Units.Length - 1)
            {
                v /= 1024;
                idx++;
            }
        }
        else
        {
            idx = (int)unit - 1;
            v /= Math.Pow(1024, idx);
        }
        var d = idx == 0 ? 0 : decimals;
        var s = v.ToString("N" + d, CultureInfo.CurrentCulture) + " " + Units[idx];
        return neg ? "-" + s : s;
    }

    /// <summary>"+1.2 GB" / "-300 MB" for size changes.</summary>
    public static string FormatChange(long bytes, SizeUnit unit = SizeUnit.Auto, int decimals = 1) =>
        bytes > 0 ? "+" + Format(bytes, unit, decimals) : Format(bytes, unit, decimals);

    public static string Percent(double fraction, int decimals = 1) =>
        (fraction * 100).ToString("N" + decimals, CultureInfo.CurrentCulture) + " %";

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static string Date(DateTime d, string? format = null) =>
        d == DateTime.MinValue ? "" : d.ToString(string.IsNullOrEmpty(format) ? "g" : format, CultureInfo.CurrentCulture);

    /// <summary>Parses "500 MB", "1.5G", "1024" (bytes) and the like. Returns false on junk.</summary>
    public static bool TryParse(string text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim().ToUpperInvariant().Replace(" ", "");
        long mult = 1;
        string[] suffixes = ["PB", "TB", "GB", "MB", "KB", "P", "T", "G", "M", "K", "BYTES", "B"];
        foreach (var s in suffixes)
        {
            if (t.EndsWith(s, StringComparison.Ordinal))
            {
                mult = s[0] switch
                {
                    'P' => 1L << 50,
                    'T' => 1L << 40,
                    'G' => 1L << 30,
                    'M' => 1L << 20,
                    'K' => 1L << 10,
                    _ => 1,
                };
                t = t[..^s.Length];
                break;
            }
        }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
            !double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            return false;
        if (v < 0)
            return false;
        bytes = (long)(v * mult);
        return true;
    }
}
