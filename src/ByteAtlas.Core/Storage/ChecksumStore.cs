using Microsoft.Data.Sqlite;
using ByteAtlas.Analysis;
using ByteAtlas.Model;

namespace ByteAtlas.Storage;

/// <summary>
/// Checksums of files read before, keyed by path, size and modified time (whole seconds, as the scan cache keeps
/// them), so a duplicate search after a refresh only reads files that changed. New checksums are written in one
/// transaction by <see cref="Flush"/>; entries not used for <see cref="MaxAge"/> are removed then.
/// </summary>
public sealed class ChecksumStore : IChecksumCache, IDisposable
{
    public static TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(120);

    private readonly SqliteConnection _db;
    private readonly Lock _lock = new();
    private readonly List<(string Path, long Size, long Modified, string Kind, string Hash)> _new = [];
    private readonly HashSet<string> _used = [];

    public ChecksumStore(string? file = null)
    {
        File = file ?? DefaultFile;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = File, Pooling = false }.ToString());
        _db.Open();
        using var c = _db.CreateCommand();
        c.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sums(path TEXT NOT NULL, kind TEXT NOT NULL, size INTEGER NOT NULL,
                modified INTEGER NOT NULL, hash TEXT NOT NULL, used INTEGER NOT NULL, PRIMARY KEY(path, kind));
            """;
        c.ExecuteNonQuery();
    }

    public static string DefaultFile => System.IO.Path.Combine(AppPaths.Local, "checksums.db");

    public string File { get; }

    /// <summary>Lookups answered from the store since it was opened (for tests and the status line).</summary>
    public int Hits { get; private set; }

    private static string Key(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private static long Seconds(FileItem f) => f.ModifiedTicks / TimeSpan.TicksPerSecond;

    public string? Get(FileItem f, string kind)
    {
        var key = Key(f.FullPath);
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT hash FROM sums WHERE path=$p AND kind=$k AND size=$s AND modified=$m";
            c.Parameters.AddWithValue("$p", key);
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$s", f.Size);
            c.Parameters.AddWithValue("$m", Seconds(f));
            if (c.ExecuteScalar() is not string h)
                return null;
            Hits++;
            _used.Add(key);
            return h;
        }
    }

    public void Put(FileItem f, string kind, string hash)
    {
        if (f.ModifiedTicks <= 0)
            return; // no reliable date: never trust it later
        lock (_lock)
            _new.Add((Key(f.FullPath), f.Size, Seconds(f), kind, hash));
    }

    /// <summary>Writes new checksums, marks used ones and removes old entries.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow.Ticks;
            using var t = _db.BeginTransaction();
            using (var c = _db.CreateCommand())
            {
                c.Transaction = t;
                c.CommandText = "INSERT OR REPLACE INTO sums VALUES($p,$k,$s,$m,$h,$u)";
                var p = c.Parameters.Add("$p", SqliteType.Text);
                var k = c.Parameters.Add("$k", SqliteType.Text);
                var s = c.Parameters.Add("$s", SqliteType.Integer);
                var m = c.Parameters.Add("$m", SqliteType.Integer);
                var h = c.Parameters.Add("$h", SqliteType.Text);
                c.Parameters.AddWithValue("$u", now);
                foreach (var e in _new)
                {
                    (p.Value, s.Value, m.Value, k.Value, h.Value) = (e.Path, e.Size, e.Modified, e.Kind, e.Hash);
                    c.ExecuteNonQuery();
                }
            }
            using (var c = _db.CreateCommand())
            {
                c.Transaction = t;
                c.CommandText = "UPDATE sums SET used=$u WHERE path=$p";
                var p = c.Parameters.Add("$p", SqliteType.Text);
                c.Parameters.AddWithValue("$u", now);
                foreach (var e in _used)
                {
                    p.Value = e;
                    c.ExecuteNonQuery();
                }
            }
            using (var c = _db.CreateCommand())
            {
                c.Transaction = t;
                c.CommandText = "DELETE FROM sums WHERE used<$old";
                c.Parameters.AddWithValue("$old", now - MaxAge.Ticks);
                c.ExecuteNonQuery();
            }
            t.Commit();
            _new.Clear();
            _used.Clear();
        }
    }

    /// <summary>Forgets every checksum.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "DELETE FROM sums";
            c.ExecuteNonQuery();
            _new.Clear();
            _used.Clear();
        }
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}
