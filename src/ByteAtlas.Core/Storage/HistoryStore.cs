using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using ByteAtlas.Scanning;

namespace ByteAtlas.Storage;

public sealed record HistoryPoint(string Path, DateTime At, long Size, long Allocated, long Files, long Folders, long VolumeFree, long VolumeTotal);

/// <summary>Remembers the totals of every finished scan per path, for the History chart.</summary>
public sealed class HistoryStore
{
    public HistoryStore(string? file = null) => File = file ?? DefaultFile;

    public static string DefaultFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ByteAtlas", "history.db");

    public string File { get; private set; }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = File, Pooling = false }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = """
            CREATE TABLE IF NOT EXISTS history(path TEXT NOT NULL, at INTEGER NOT NULL, size INTEGER, alloc INTEGER,
                files INTEGER, folders INTEGER, free INTEGER, total INTEGER);
            CREATE INDEX IF NOT EXISTS history_path ON history(path, at);
            CREATE TABLE IF NOT EXISTS folders(path TEXT NOT NULL, at INTEGER NOT NULL, folder TEXT NOT NULL,
                size INTEGER, alloc INTEGER, files INTEGER);
            CREATE INDEX IF NOT EXISTS folders_path ON folders(path, at);
            """;
        c.ExecuteNonQuery();
        return db;
    }

    private static string Key(string path) => ScanSession.NormalizeRoot(path).ToUpperInvariant();

    /// <summary>Folders kept per snapshot at most (the largest; enough to see what grew).</summary>
    public const int MaxSnapshotFolders = 20_000;

    public void Record(ScanSession s)
    {
        if (s.State != ScanState.Done || s.IndexFile != null)
            return;
        var p = new HistoryPoint(s.Root.FullPath, s.ScanDate == default ? DateTime.Now : s.ScanDate, s.Root.Size, s.Root.Allocated, s.Root.FileCount, s.Root.FolderCount, s.VolumeFree, s.VolumeTotal);
        Add(p, s.Remote == null ? Snapshot(s.Root) : null);
    }

    /// <summary>
    /// The folders worth remembering: down to 6 levels, at least 1 MB and 1/2000 of the scan, the largest
    /// <see cref="MaxSnapshotFolders"/>; keyed by their path below the scanned folder ("" = the folder itself).
    /// </summary>
    public static List<FolderSize> Snapshot(Model.DirNode root)
    {
        var min = Math.Max(1L << 20, root.Size / 2000);
        var list = new List<FolderSize> { new("", root.Size, root.Allocated, root.FileCount) };
        var stack = new Stack<(Model.DirNode, string, int)>();
        stack.Push((root, "", 0));
        while (stack.Count > 0)
        {
            var (d, rel, level) = stack.Pop();
            if (level >= 6)
                continue;
            foreach (var c in d.Dirs)
            {
                if (c.Size < min)
                    continue;
                var r = rel.Length == 0 ? c.Name : rel + "/" + c.Name;
                list.Add(new FolderSize(r, c.Size, c.Allocated, c.FileCount));
                stack.Push((c, r, level + 1));
            }
        }
        return list.Count <= MaxSnapshotFolders ? list : [.. list.OrderByDescending(f => f.Size).Take(MaxSnapshotFolders)];
    }

    /// <summary>Dates of the scans of <paramref name="path"/> that have folder sizes (newest first).</summary>
    public List<DateTime> Snapshots(string path)
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT DISTINCT at FROM folders WHERE path=$p ORDER BY at DESC";
        c.Parameters.AddWithValue("$p", Key(path));
        var list = new List<DateTime>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new DateTime(r.GetInt64(0), DateTimeKind.Utc).ToLocalTime());
        return list;
    }

    /// <summary>The folder sizes of one snapshot, by their path below the scanned folder.</summary>
    public Dictionary<string, FolderSize> Folders(string path, DateTime at)
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT folder,size,alloc,files FROM folders WHERE path=$p AND at=$t";
        c.Parameters.AddWithValue("$p", Key(path));
        c.Parameters.AddWithValue("$t", at.ToUniversalTime().Ticks);
        var map = new Dictionary<string, FolderSize>(StringComparer.OrdinalIgnoreCase);
        using var r = c.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = new FolderSize(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
        return map;
    }

    public void Add(HistoryPoint p, IReadOnlyList<FolderSize>? folders = null)
    {
        using var db = Open();
        using var t = db.BeginTransaction();
        using (var c = db.CreateCommand())
        {
            c.Transaction = t;
            c.CommandText = "INSERT INTO history VALUES($p,$t,$s,$a,$f,$d,$fr,$to)";
            c.Parameters.AddWithValue("$p", Key(p.Path));
            c.Parameters.AddWithValue("$t", p.At.ToUniversalTime().Ticks);
            c.Parameters.AddWithValue("$s", p.Size);
            c.Parameters.AddWithValue("$a", p.Allocated);
            c.Parameters.AddWithValue("$f", p.Files);
            c.Parameters.AddWithValue("$d", p.Folders);
            c.Parameters.AddWithValue("$fr", p.VolumeFree);
            c.Parameters.AddWithValue("$to", p.VolumeTotal);
            c.ExecuteNonQuery();
        }
        if (folders != null)
        {
            using var c = db.CreateCommand();
            c.Transaction = t;
            c.CommandText = "INSERT INTO folders VALUES($p,$t,$f,$s,$a,$n)";
            c.Parameters.AddWithValue("$p", Key(p.Path));
            c.Parameters.AddWithValue("$t", p.At.ToUniversalTime().Ticks);
            var f = c.Parameters.Add("$f", SqliteType.Text);
            var s = c.Parameters.Add("$s", SqliteType.Integer);
            var a = c.Parameters.Add("$a", SqliteType.Integer);
            var n = c.Parameters.Add("$n", SqliteType.Integer);
            foreach (var x in folders)
            {
                (f.Value, s.Value, a.Value, n.Value) = (x.Folder, x.Size, x.Allocated, x.Files);
                c.ExecuteNonQuery();
            }
        }
        t.Commit();
    }

    public List<HistoryPoint> Get(string path)
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT path,at,size,alloc,files,folders,free,total FROM history WHERE path=$p ORDER BY at";
        c.Parameters.AddWithValue("$p", Key(path));
        return Read(c);
    }

    public List<HistoryPoint> All()
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT path,at,size,alloc,files,folders,free,total FROM history ORDER BY path, at";
        return Read(c);
    }

    private static List<HistoryPoint> Read(SqliteCommand c)
    {
        var list = new List<HistoryPoint>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new HistoryPoint(r.GetString(0), new DateTime(r.GetInt64(1), DateTimeKind.Utc).ToLocalTime(), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7)));
        return list;
    }

    public void Remove(string path, DateTime at)
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "DELETE FROM history WHERE path=$p AND at=$t; DELETE FROM folders WHERE path=$p AND at=$t";
        c.Parameters.AddWithValue("$p", Key(path));
        c.Parameters.AddWithValue("$t", at.ToUniversalTime().Ticks);
        c.ExecuteNonQuery();
    }

    /// <summary>Removes the values of <paramref name="path"/> (all paths when null) older than <paramref name="before"/>.</summary>
    public int Purge(string? path, DateTime before)
    {
        using var db = Open();
        var removed = 0;
        foreach (var table in new[] { "folders", "history" })
        {
            using var c = db.CreateCommand();
            c.CommandText = path == null ? $"DELETE FROM {table} WHERE at < $t" : $"DELETE FROM {table} WHERE path=$p AND at < $t";
            if (path != null)
                c.Parameters.AddWithValue("$p", Key(path));
            c.Parameters.AddWithValue("$t", before.ToUniversalTime().Ticks);
            removed = c.ExecuteNonQuery(); // the history rows count, not the folder rows
        }
        return removed;
    }

    /// <summary>Moves the data file to a new place (copying what is there).</summary>
    public void MoveTo(string newFile)
    {
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(newFile)!);
        if (System.IO.File.Exists(File) && !System.IO.File.Exists(newFile))
            System.IO.File.Copy(File, newFile);
        File = newFile;
    }

    public void ExportCsv(string csv, string? path = null)
    {
        var sb = new StringBuilder("Path;Date;Size;Allocated;Files;Folders;Free;Total\r\n");
        foreach (var p in path == null ? All() : Get(path))
            sb.Append(CultureInfo.InvariantCulture, $"{p.Path};{p.At:o};{p.Size};{p.Allocated};{p.Files};{p.Folders};{p.VolumeFree};{p.VolumeTotal}\r\n");
        System.IO.File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));
    }

    public int ImportCsv(string csv)
    {
        var n = 0;
        foreach (var line in System.IO.File.ReadLines(csv).Skip(1))
        {
            var f = line.Split(';');
            if (f.Length < 8 || !DateTime.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                continue;
            static long L(string s) => long.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : 0;
            Add(new HistoryPoint(f[0], at, L(f[2]), L(f[3]), L(f[4]), L(f[5]), L(f[6]), L(f[7])));
            n++;
        }
        return n;
    }
}
