namespace ByteAtlas.Analysis;

/// <summary>File groups ("Video files", "Archives", …) by extension; editable in the options.</summary>
public sealed class FileGroups
{
    public const string Other = "Other files";

    public Dictionary<string, string> Groups { get; set; } = new()
    {
        ["Audio files"] = "aac;aif;aiff;alac;ape;flac;m4a;mid;midi;mka;mp2;mp3;mpc;oga;ogg;opus;ra;wav;wma;wv",
        ["Video files"] = "3gp;asf;avi;divx;flv;m2ts;m4v;mkv;mov;mp4;mpeg;mpg;mts;ogv;rm;rmvb;ts;vob;webm;wmv",
        ["Image files"] = "arw;avif;bmp;cr2;cr3;dng;gif;heic;heif;ico;jfif;jpeg;jpg;jxl;nef;orf;png;psd;raf;raw;svg;tga;tif;tiff;webp",
        ["Archives"] = "001;7z;ace;arj;bz2;cab;gz;iso;lz;lzh;lzma;r00;rar;tar;tbz;tgz;txz;xz;z;zip;zst",
        ["Documents"] = "csv;doc;docm;docx;epub;md;mobi;odp;ods;odt;pdf;pps;ppt;pptx;rtf;txt;xls;xlsb;xlsm;xlsx;xps",
        ["Executables"] = "appx;bat;cmd;com;dll;exe;msi;msix;msp;ps1;scr;sys",
        ["Disk images"] = "dmg;img;qcow2;vdi;vhd;vhdx;vmdk;wim;esd",
        ["Source code"] = "c;cc;cpp;cs;css;go;h;hpp;html;java;js;json;kt;py;rb;rs;sh;sql;swift;ts;tsx;xml;yaml;yml",
        ["Databases"] = "accdb;db;db3;dbf;ldf;mdb;mdf;ndf;sqlite;sqlite3",
        ["Temporary files"] = "~tmp;bak;chk;dmp;etl;gid;log;old;temp;tmp",
        ["Fonts"] = "fon;otf;ttc;ttf;woff;woff2",
    };

    private Dictionary<string, string>? _byExt;

    public void Invalidate() => _byExt = null;

    private Dictionary<string, string> Map()
    {
        if (_byExt != null)
            return _byExt;
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (group, exts) in Groups)
            foreach (var e in exts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                m.TryAdd(e.TrimStart('.', '*'), group);
        return _byExt = m;
    }

    public string GroupOf(string extension) => Map().TryGetValue(extension, out var g) ? g : Other;

    /// <summary>Text for the "Description" column of the extensions view.</summary>
    public string Describe(string extension) =>
        extension.Length == 0 ? "Files without extension" : Map().TryGetValue(extension, out var g) ? g : "";
}
