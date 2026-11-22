using ByteAtlas.Analysis;
using ByteAtlas.Scanning;

namespace ByteAtlas.Search;

public enum SearchTemplate
{
    Custom,
    LargeFiles,
    OldFiles,
    TemporaryFiles,
    Duplicates,
    LongPaths,
    AllFiles,
}

public enum HashKind
{
    None,
    Md5,
    Sha256,
}

/// <summary>What the file search looks for. Every set criterion must match.</summary>
public sealed class SearchCriteria
{
    public string Name { get; set; } = "Custom search";
    public SearchTemplate Template { get; set; } = SearchTemplate.Custom;

    /// <summary>Name patterns; a file matches when any of them matches (empty = all names).</summary>
    public List<FilterRule> Include { get; set; } = [];

    /// <summary>Names or folders to leave out.</summary>
    public List<FilterRule> Exclude { get; set; } = [];

    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }

    public AgeBasis DateBasis { get; set; } = AgeBasis.Modified;
    public DateTime? OlderThan { get; set; }
    public DateTime? NewerThan { get; set; }

    public FileAttributes AttributesSet { get; set; }
    public FileAttributes AttributesNotSet { get; set; }

    /// <summary>Owner name contains this text.</summary>
    public string? Owner { get; set; }

    public int? MinPathLength { get; set; }

    public bool IncludeFolders { get; set; }

    /// <summary>Stop after this many results (0 = no limit).</summary>
    public int MaxResults { get; set; }

    /// <summary>Largest first and only the top MaxResults (large files search).</summary>
    public bool SortBySizeDesc { get; set; }

    // ----- duplicates -----
    public bool DuplicatesSameName { get; set; }
    public bool DuplicatesSameDate { get; set; }
    public HashKind DuplicatesHash { get; set; } = HashKind.Md5;

    public static SearchCriteria FromTemplate(SearchTemplate t) => t switch
    {
        SearchTemplate.LargeFiles => new SearchCriteria
        {
            Name = "Large files",
            Template = t,
            MinSize = 100L << 20,
            MaxResults = 1000,
            SortBySizeDesc = true,
        },
        SearchTemplate.OldFiles => new SearchCriteria
        {
            Name = "Old files",
            Template = t,
            DateBasis = AgeBasis.Accessed,
            OlderThan = DateTime.Now.AddYears(-1),
            MinSize = 1L << 20,
        },
        SearchTemplate.TemporaryFiles => new SearchCriteria
        {
            Name = "Temporary files",
            Template = t,
            Include =
            [
                new FilterRule { Pattern = "*.tmp;*.temp;~*;*.~*;*.bak;*.old;*.chk;*.dmp;*.gid;thumbs.db;desktop.ini.bak;*.log;*._mp;*.$$$", Exclude = false },
            ],
        },
        SearchTemplate.Duplicates => new SearchCriteria
        {
            Name = "Duplicate files",
            Template = t,
            MinSize = 1,
            DuplicatesHash = HashKind.Md5,
        },
        SearchTemplate.LongPaths => new SearchCriteria
        {
            Name = "Files with long paths",
            Template = t,
            MinPathLength = 256,
            IncludeFolders = true,
        },
        SearchTemplate.AllFiles => new SearchCriteria { Name = "All files", Template = t },
        _ => new SearchCriteria(),
    };
}
