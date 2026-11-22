using ByteAtlas.Model;

namespace ByteAtlas.Storage
{
    /// <summary>Size of one folder in a history snapshot; Folder is its path below the scanned folder ("a/b").</summary>
    public sealed record FolderSize(string Folder, long Size, long Allocated, long Files);
}

namespace ByteAtlas.Analysis
{
    using ByteAtlas.Storage;

    /// <summary>How much a folder changed between a history snapshot and now.</summary>
    public sealed class GrowthRow
    {
        public required string Folder { get; init; }

        /// <summary>The folder now; null when it is gone.</summary>
        public DirNode? Node { get; init; }

        /// <summary>Null: not in the snapshot (new, or too small then to be kept).</summary>
        public long? Then { get; init; }
        public long Now { get; init; }
        public long Change => Now - (Then ?? 0);
        public long FilesThen { get; init; }
        public long FilesNow { get; init; }
        public int Level => Folder.Length == 0 ? 0 : Folder.Count(c => c == '/') + 1;
    }

    /// <summary>"What grew": the folders whose size changed most since an earlier scan.</summary>
    public static class Growth
    {
        /// <summary>
        /// Compares the current tree with a snapshot. Only folders kept in the snapshot or big enough to be kept
        /// now are compared. With <paramref name="deepest"/>, a folder is left out when one of its sub folders
        /// accounts for (nearly) all of its change: the list then names where the growth really happened.
        /// </summary>
        public static List<GrowthRow> Compare(DirNode root, IReadOnlyDictionary<string, FolderSize> then, bool deepest = true, int max = 500)
        {
            var now = HistoryStore.Snapshot(root).ToDictionary(f => f.Folder, StringComparer.OrdinalIgnoreCase);
            var rows = new List<GrowthRow>();
            foreach (var (key, n) in now)
            {
                then.TryGetValue(key, out var t);
                rows.Add(new GrowthRow { Folder = key, Node = Find(root, key), Then = t?.Size, Now = n.Size, FilesThen = t?.Files ?? 0, FilesNow = n.Files });
            }
            foreach (var (key, t) in then)
            {
                if (now.ContainsKey(key))
                    continue;
                // in the snapshot, not big enough to be kept now (or gone)
                var node = Find(root, key);
                rows.Add(new GrowthRow { Folder = key, Node = node, Then = t.Size, Now = node?.Size ?? 0, FilesThen = t.Files, FilesNow = node?.FileCount ?? 0 });
            }
            rows.RemoveAll(r => r.Change == 0);
            if (deepest)
            {
                // per folder: the largest growth and the largest shrinking of its direct sub folders
                var children = new Dictionary<string, (long Up, long Down)>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows.Where(r => r.Folder.Length > 0))
                {
                    var slash = r.Folder.LastIndexOf('/');
                    var parent = slash < 0 ? "" : r.Folder[..slash];
                    var (up, down) = children.GetValueOrDefault(parent);
                    children[parent] = (Math.Max(up, r.Change), Math.Min(down, r.Change));
                }
                // left out: a sub folder changed the same way by at least 90 % of this folder's change
                rows.RemoveAll(r => children.TryGetValue(r.Folder, out var c)
                    && (r.Change > 0 ? c.Up >= r.Change * 0.9 : c.Down <= r.Change * 0.9));
            }
            return [.. rows.OrderByDescending(r => Math.Abs(r.Change)).Take(max)];
        }

        /// <summary>The folder at "a/b" below <paramref name="root"/>, or null when it is gone.</summary>
        public static DirNode? Find(DirNode root, string rel)
        {
            if (rel.Length == 0)
                return root;
            var d = root;
            foreach (var part in rel.Split('/'))
            {
                d = Array.Find(d.Dirs, x => string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase));
                if (d == null)
                    return null;
            }
            return d;
        }
    }
}
