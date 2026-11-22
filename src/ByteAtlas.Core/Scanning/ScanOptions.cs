using System.Text.RegularExpressions;

namespace ByteAtlas.Scanning;

public enum ScanMethod
{
    /// <summary>Read the NTFS master file table when possible (local NTFS, administrator), else walk folders.</summary>
    Auto,
    /// <summary>Always walk the folders with the file system API.</summary>
    FileSystem,
    /// <summary>Only the master file table (fails when not possible).</summary>
    Mft,
}

public enum PatternKind
{
    Wildcard,
    Regex,
    WholeName,
}

/// <summary>One include / exclude rule of the scan filter.</summary>
public sealed class FilterRule
{
    public string Pattern { get; set; } = "";
    public PatternKind Kind { get; set; } = PatternKind.Wildcard;
    public bool Exclude { get; set; } = true;
    public bool Folders { get; set; }
    public bool Enabled { get; set; } = true;

    private Regex? _rx;

    public bool Matches(string name)
    {
        if (string.IsNullOrEmpty(Pattern))
            return false;
        switch (Kind)
        {
            case PatternKind.WholeName:
                return string.Equals(name, Pattern, StringComparison.OrdinalIgnoreCase);
            case PatternKind.Regex:
                _rx ??= new Regex(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                try
                {
                    return _rx.IsMatch(name);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            default:
                // several patterns separated by ';'
                foreach (var p in Pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Wildcard.IsMatch(name, p))
                        return true;
                return false;
        }
    }
}

/// <summary>Wildcard matching with * and ? (case-insensitive).</summary>
public static class Wildcard
{
    public static bool IsMatch(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
                return false;
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}

public sealed class ScanOptions
{
    public ScanMethod Method { get; set; } = ScanMethod.Auto;

    /// <summary>Threads for the folder walk (0 = automatic: 2 on network paths, CPU count locally, max 32).</summary>
    public int Threads { get; set; }

    public ThreadPriority Priority { get; set; } = ThreadPriority.Normal;

    /// <summary>Follow junctions, mount points and directory symlinks.</summary>
    public bool FollowMountPoints { get; set; }

    /// <summary>Count hard-linked files once and add alternate data streams to the allocated space.</summary>
    public bool TrackHardlinksAndStreams { get; set; }

    /// <summary>Collect the owner of every file (slower).</summary>
    public bool CollectOwners { get; set; } = true;

    /// <summary>Folder dates come from the files inside only (not the folder's own time stamps).</summary>
    public bool FolderDateFromFilesOnly { get; set; } = true;

    public List<FilterRule> Filters { get; set; } = [];

    internal bool HasFilters => Filters.Any(f => f.Enabled && !string.IsNullOrEmpty(f.Pattern));

    /// <summary>Should a file with this name be counted?</summary>
    internal bool IncludeFile(string name)
    {
        var includes = false;
        var included = false;
        foreach (var f in Filters)
        {
            if (!f.Enabled || f.Folders)
                continue;
            if (f.Exclude)
            {
                if (f.Matches(name))
                    return false;
            }
            else
            {
                includes = true;
                included |= f.Matches(name);
            }
        }
        return !includes || included;
    }

    /// <summary>Should this folder be entered?</summary>
    internal bool IncludeFolder(string name, string fullPath)
    {
        foreach (var f in Filters)
        {
            if (!f.Enabled || !f.Folders || !f.Exclude)
                continue;
            if (f.Matches(name) || (f.Pattern.Contains('\\') && f.Matches(fullPath)))
                return false;
        }
        return true;
    }

    public ScanOptions Clone()
    {
        var c = (ScanOptions)MemberwiseClone();
        c.Filters = [.. Filters.Select(f => new FilterRule { Pattern = f.Pattern, Kind = f.Kind, Exclude = f.Exclude, Folders = f.Folders, Enabled = f.Enabled })];
        return c;
    }
}
