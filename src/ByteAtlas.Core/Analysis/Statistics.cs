using ByteAtlas.Model;
using ByteAtlas.Scanning;

namespace ByteAtlas.Analysis;

/// <summary>One row of a grouped statistic (extension, file group, owner, age interval).</summary>
public sealed class StatRow
{
    public string Key { get; init; } = "";
    public string Description { get; set; } = "";
    public long Size { get; set; }
    public long Allocated { get; set; }
    public long Files { get; set; }
    public double Percent { get; set; }
    public double PercentAllocated { get; set; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

public enum AgeBasis
{
    Modified,
    Accessed,
    Created,
}

/// <summary>Age interval boundaries, newest first. Each value is the upper age of the interval.</summary>
public sealed class AgeIntervals
{
    public List<(string Label, TimeSpan MaxAge)> Intervals { get; set; } =
    [
        ("Less than 1 month", TimeSpan.FromDays(30)),
        ("1 to 3 months", TimeSpan.FromDays(91)),
        ("3 to 6 months", TimeSpan.FromDays(182)),
        ("6 to 12 months", TimeSpan.FromDays(365)),
        ("1 to 2 years", TimeSpan.FromDays(730)),
        ("2 to 5 years", TimeSpan.FromDays(1826)),
        ("More than 5 years", TimeSpan.MaxValue),
    ];
}

public static class Statistics
{
    public const string FutureLabel = "In the future";

    /// <summary>Totals per file extension in a branch, largest first.</summary>
    public static List<StatRow> ByExtension(DirNode branch, FileGroups? groups = null, Func<FileItem, bool>? filter = null)
    {
        var map = new Dictionary<string, StatRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in branch.AllFiles())
        {
            if (filter != null && !filter(f))
                continue;
            var ext = f.Extension;
            if (!map.TryGetValue(ext, out var row))
                map[ext] = row = new StatRow { Key = ext, Description = groups?.Describe(ext) ?? "" };
            row.Size += f.Size;
            row.Allocated += f.Allocated;
            row.Files++;
        }
        return Finish(map.Values);
    }

    /// <summary>Totals per file group (audio, video, …) in a branch.</summary>
    public static List<StatRow> ByGroup(DirNode branch, FileGroups groups)
    {
        var map = new Dictionary<string, StatRow>();
        foreach (var f in branch.AllFiles())
        {
            var g = groups.GroupOf(f.Extension);
            if (!map.TryGetValue(g, out var row))
                map[g] = row = new StatRow { Key = g, Description = g };
            row.Size += f.Size;
            row.Allocated += f.Allocated;
            row.Files++;
        }
        return Finish(map.Values);
    }

    /// <summary>Totals per owner in a branch.</summary>
    public static List<StatRow> ByOwner(DirNode branch, OwnerTable owners)
    {
        var map = new Dictionary<int, StatRow>();
        foreach (var f in branch.AllFiles())
        {
            if (!map.TryGetValue(f.OwnerId, out var row))
                map[f.OwnerId] = row = new StatRow { Key = f.OwnerId < 0 ? "(unknown)" : owners.Name(f.OwnerId) };
            row.Size += f.Size;
            row.Allocated += f.Allocated;
            row.Files++;
        }
        return Finish(map.Values);
    }

    /// <summary>Distribution of files over age intervals (kept in interval order).</summary>
    public static List<StatRow> ByAge(DirNode branch, AgeIntervals intervals, AgeBasis basis = AgeBasis.Modified, DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        var rows = new List<StatRow>();
        var prev = TimeSpan.Zero;
        foreach (var (label, max) in intervals.Intervals)
        {
            rows.Add(new StatRow
            {
                Key = label,
                Description = label,
                To = at - prev,
                From = max == TimeSpan.MaxValue ? DateTime.MinValue : at - max,
            });
            prev = max;
        }
        StatRow? future = null;
        foreach (var f in branch.AllFiles())
        {
            var d = basis switch
            {
                AgeBasis.Accessed => f.Accessed,
                AgeBasis.Created => f.Created,
                _ => f.Modified,
            };
            var age = at - d;
            StatRow row;
            if (age < -TimeSpan.FromDays(1))
                row = future ??= new StatRow { Key = FutureLabel, Description = "Time stamp after today (wrong clock?)", From = at, To = DateTime.MaxValue };
            else
            {
                var idx = 0;
                while (idx < intervals.Intervals.Count - 1 && age > intervals.Intervals[idx].MaxAge)
                    idx++;
                row = rows[idx];
            }
            row.Size += f.Size;
            row.Allocated += f.Allocated;
            row.Files++;
        }
        if (future != null)
            rows.Insert(0, future);
        SetPercents(rows);
        return rows;
    }

    /// <summary>The <paramref name="count"/> largest files in a branch.</summary>
    public static List<FileItem> TopFiles(DirNode branch, int count = 100, bool byAllocated = false)
    {
        var pq = new PriorityQueue<FileItem, long>(count + 1);
        foreach (var f in branch.AllFiles())
        {
            var v = byAllocated ? f.Allocated : f.Size;
            if (pq.Count < count)
                pq.Enqueue(f, v);
            else if (pq.TryPeek(out _, out var min) && v > min)
                pq.EnqueueDequeue(f, v);
        }
        var list = new List<FileItem>(pq.Count);
        while (pq.Count > 0)
            list.Add(pq.Dequeue());
        list.Reverse();
        return list;
    }

    /// <summary>The folder with the deepest nesting below <paramref name="branch"/>.</summary>
    public static DirNode DeepestFolder(DirNode branch)
    {
        var best = branch;
        var bestLevel = -1;
        var stack = new Stack<(DirNode, int)>();
        stack.Push((branch, 0));
        while (stack.Count > 0)
        {
            var (d, lvl) = stack.Pop();
            if (lvl > bestLevel)
            {
                best = d;
                bestLevel = lvl;
            }
            foreach (var c in d.Dirs)
                stack.Push((c, lvl + 1));
        }
        return best;
    }

    private static List<StatRow> Finish(IEnumerable<StatRow> rows)
    {
        var list = rows.OrderByDescending(r => r.Size).ToList();
        SetPercents(list);
        return list;
    }

    private static void SetPercents(List<StatRow> rows)
    {
        var total = Math.Max(1, rows.Sum(r => r.Size));
        var totalAlloc = Math.Max(1, rows.Sum(r => r.Allocated));
        foreach (var r in rows)
        {
            r.Percent = (double)r.Size / total;
            r.PercentAllocated = (double)r.Allocated / totalAlloc;
        }
    }
}
