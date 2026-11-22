using ByteAtlas.Model;

namespace ByteAtlas.Analysis;

/// <summary>
/// "Filter by this extension / user" and pattern filters on the finished scan: computes filtered totals
/// for every folder so the tree can show only what matches.
/// </summary>
public sealed class ViewFilter
{
    public string Description { get; init; } = "";
    public required Func<FileItem, bool> Predicate { get; init; }

    public static ViewFilter ForExtensions(IReadOnlyCollection<string> exts) => new()
    {
        Description = "Extension: " + string.Join(", ", exts.Select(e => e.Length == 0 ? "(none)" : "." + e)),
        Predicate = f => exts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase),
    };

    public static ViewFilter ForOwners(IReadOnlyCollection<int> owners, string label) => new()
    {
        Description = "Owner: " + label,
        Predicate = f => owners.Contains(f.OwnerId),
    };

    public static ViewFilter ForPattern(Scanning.FilterRule rule) => new()
    {
        Description = "Name: " + rule.Pattern,
        Predicate = f => rule.Matches(f.Name),
    };

    public static ViewFilter ForGroup(FileGroups groups, string group) => new()
    {
        Description = "File group: " + group,
        Predicate = f => groups.GroupOf(f.Extension) == group,
    };

    /// <summary>Fills FilteredSize / FilteredFiles of every folder below <paramref name="root"/>.</summary>
    public void Apply(DirNode root) => Compute(root);

    private (long, long, long) Compute(DirNode d)
    {
        long size = 0, alloc = 0, files = 0;
        foreach (var f in d.Files)
        {
            if (!Predicate(f))
                continue;
            size += f.Size;
            alloc += f.Allocated;
            files++;
        }
        foreach (var c in d.Dirs)
        {
            var (s, a, n) = Compute(c);
            size += s;
            alloc += a;
            files += n;
        }
        d._fSize = size;
        d._fAlloc = alloc;
        d._fFiles = files;
        return (size, alloc, files);
    }
}
