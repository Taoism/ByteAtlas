using System.Text.Json;
using System.Text.Json.Serialization;
using ByteAtlas.Analysis;
using ByteAtlas.Export;
using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas;

public enum TreeValueMode
{
    Size,
    Allocated,
    Files,
    Percent,
}

public enum StartupAction
{
    Nothing,
    LastPaths,
    FixedPaths,
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";

    // ----- view -----
    public SizeUnit Unit { get; set; } = SizeUnit.Auto;
    public int Decimals { get; set; } = 1;
    public TreeValueMode TreeMode { get; set; } = TreeValueMode.Size;
    public bool SortByName { get; set; }
    public bool ShowGradientBars { get; set; } = true;
    public bool BoldLarge { get; set; } = true;
    public long BoldThreshold { get; set; } = 1L << 30;
    public bool CompressedBlue { get; set; } = true;
    public bool LongPathRed { get; set; } = true;
    public int LongPathLength { get; set; } = 260;
    public bool ListFilesInTree { get; set; } = true;
    public long SizeThreshold { get; set; } = 100L << 20;
    public bool HideSmall { get; set; }
    public bool HideEmptyFolders { get; set; }
    public string DateFormat { get; set; } = "";
    public double CostPerGb { get; set; }
    public string Currency { get; set; } = "€";
    public bool ShowDriveList { get; set; } = true;
    public bool ShowDriveNames { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
    public bool CompactRows { get; set; }
    public bool UpdateDuringScan { get; set; } = true;
    public bool WatchChanges { get; set; }
    public bool ShowErrorsAfterScan { get; set; }
    public bool NotifyWhenDone { get; set; } = true;
    public bool GroupScans { get; set; }

    // ----- charts -----
    public string ChartType { get; set; } = "Treemap";
    public bool ChartLegend { get; set; } = true;
    public bool ChartLabels { get; set; } = true;
    public bool ChartFreeSpace { get; set; }
    public bool ChartGrid { get; set; } = true;
    public double ChartOtherPercent { get; set; } = 2;
    public bool TreemapHierarchy { get; set; } = true;
    public bool TreemapFiles { get; set; } = true;
    public bool TreemapColorByType { get; set; } = true;
    public int TreemapDepth { get; set; } = 6;

    // ----- statistics -----
    public AgeBasis AgeBasis { get; set; } = AgeBasis.Modified;
    public List<AgeInterval> AgeIntervals { get; set; } = [.. new AgeIntervals().Intervals.Select(i => new AgeInterval { Label = i.Label, Days = i.MaxAge == TimeSpan.MaxValue ? 0 : i.MaxAge.TotalDays })];
    public Dictionary<string, string> FileGroups
    {
        get => _fileGroups;
        set
        {
            _fileGroups = value;
            _groupSet = null;
        }
    }

    private Dictionary<string, string> _fileGroups = new FileGroups().Groups;
    private FileGroups? _groupSet;

    /// <summary>Colours of file groups / ".ext" types ("#RRGGBB"), chosen in Options › Colours.</summary>
    public Dictionary<string, string> TypeColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool GroupExtensions { get; set; }
    public int TopCount { get; set; } = 100;

    // ----- duplicate tabs -----
    public DuplicateMatch DuplicateMatch { get; set; } = DuplicateMatch.Contents;
    public Search.HashKind DuplicateHash { get; set; } = Search.HashKind.Md5;
    public long DuplicateMinSize { get; set; } = 1L << 20;
    public bool DuplicateSubsets { get; set; } = true;
    public string DuplicateKeep { get; set; } = "Oldest";
    public bool DuplicateAllScans { get; set; }

    /// <summary>Ask GitHub once a day whether a newer release exists (nothing is downloaded).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The report designer: the report last made, and saved templates.</summary>
    public Export.ReportDefinition Report { get; set; } = new();
    public List<Export.ReportDefinition> ReportTemplates { get; set; } = [];
    public DateTime LastUpdateCheck { get; set; }
    public string SkippedVersion { get; set; } = "";
    public bool DuplicateRememberChecksums { get; set; } = true;

    /// <summary>Never selected by "Select the other copies", never deleted / linked / moved from the duplicate tabs.</summary>
    public List<string> DuplicateProtected { get; set; } = [.. FolderRules.DefaultProtected];

    /// <summary>Left out of duplicate searches completely.</summary>
    public List<string> DuplicateIgnored { get; set; } = [.. FolderRules.DefaultIgnored];

    // ----- details columns -----
    public List<string> DetailColumns { get; set; } = ["Name", "Size", "Allocated", "Percent", "Files", "Folders", "Modified", "Owner", "Type"];

    // ----- scan -----
    public ScanOptions Scan { get; set; } = new();

    // ----- export -----
    public ExportSettings Export { get; set; } = new();
    public string ExportFolder { get; set; } = "";
    public bool MailBody { get; set; }
    public bool MailAttach { get; set; } = true;

    // ----- system -----
    public StartupAction Startup { get; set; } = StartupAction.Nothing;
    public List<string> StartupPaths { get; set; } = [];
    public List<string> LastPaths { get; set; } = [];
    public List<string> Recent { get; set; } = [];
    public int RecentMax { get; set; } = 12;
    public List<string> ExtraDrives { get; set; } = [];

    /// <summary>Remote sources kept by the user (SSH, S3, Azure, WebDAV, disk images).</summary>
    public List<Dialogs.SavedRemote> Remotes { get; set; } = [];
    public List<string> HiddenDrives { get; set; } = [];
    public bool AlwaysAdmin { get; set; }
    public string HistoryFile { get; set; } = "";

    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 880;
    public bool Maximized { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double TreeWidth { get; set; } = 480;

    /// <summary>Width of the value column(s) in the directory tree.</summary>
    public double TreeValueWidth { get; set; } = 96;

    /// <summary>Window sizes, splitter positions and column widths by key (see Controls.Resizing).</summary>
    public Dictionary<string, double> Layout { get; set; } = [];

    /// <summary>The settings of this run (for dialogs that remember their size).</summary>
    [JsonIgnore]
    public static AppSettings Current { get; private set; } = new();
    public string LastTab { get; set; } = "Chart";
    public bool PreviewPane { get; set; }

    /// <summary>Keep the last scan of each path: shown at once on the next start, then refreshed.</summary>
    public bool CacheScans { get; set; } = true;
    public bool DetailsThumbnails { get; set; }

    /// <summary>Extra Details columns: size of a file type / group / owner / age interval per folder.</summary>
    public List<ExtraColumn> DetailExtraColumns { get; set; } = [];

    public sealed class ExtraColumn
    {
        /// <summary>"ext", "group", "owner" or "age".</summary>
        public string Kind { get; set; } = "ext";
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
    }

    public sealed class AgeInterval
    {
        public string Label { get; set; } = "";

        /// <summary>Upper age in days (0 = no limit, the last interval).</summary>
        public double Days { get; set; }
    }

    [JsonIgnore]
    public AgeIntervals AgeIntervalSet => new()
    {
        Intervals = [.. AgeIntervals.Select(a => (a.Label, a.Days <= 0 ? TimeSpan.MaxValue : TimeSpan.FromDays(a.Days)))],
    };

    [JsonIgnore]
    /// <summary>The file groups, made once per change (charts colour every type with it).</summary>
    public FileGroups FileGroupSet => _groupSet ??= new() { Groups = FileGroups };

    public static string Folder => Storage.AppPaths.Roaming;

    /// <summary>Scan cache folder.</summary>
    public static string CacheFolder => Path.Combine(Storage.AppPaths.Local, "cache");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string? file = null)
    {
        try
        {
            var path = file ?? FilePath;
            if (File.Exists(path))
                return Keep(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings(), file);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall back to defaults
        }
        return Keep(new AppSettings(), file);
    }

    // the app's own settings file becomes Current; an imported file does not
    private static AppSettings Keep(AppSettings s, string? file)
    {
        if (file == null)
            Current = s;
        return s;
    }

    public void Save(string? file = null)
    {
        try
        {
            var path = file ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // settings are a convenience
        }
    }

    public string Size(long bytes) => SizeFormat.Format(bytes, Unit, Decimals);

    public void AddRecent(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > RecentMax)
            Recent.RemoveRange(RecentMax, Recent.Count - RecentMax);
    }
}
