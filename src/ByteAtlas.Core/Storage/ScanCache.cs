using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ByteAtlas.Model;
using ByteAtlas.Native;
using ByteAtlas.Scanning;

namespace ByteAtlas.Storage;

/// <summary>
/// A fast binary snapshot of the last scan of a path (folders, files, owners, change-journal position), so the
/// window can show it at once on the next start and then refresh it (through the change journal where possible).
/// </summary>
public static class ScanCache
{
    private const string Magic = "BACACHE3";

    // dates as whole seconds since 1601 (variable length), sizes as variable-length numbers: ~3x smaller
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    public static string FileFor(string folder, string path)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ScanSession.NormalizeRoot(path).ToUpperInvariant())))[..24];
        return Path.Combine(folder, key + ".bacache");
    }

    public static void Save(ScanSession s, string folder)
    {
        Directory.CreateDirectory(folder);
        var file = FileFor(folder, s.Path);
        var tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var z = new ZLibStream(fs, CompressionLevel.Fastest))
        using (var buf = new BufferedStream(z, 1 << 20))
        using (var w = new BinaryWriter(buf, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(s.Path);
            w.Write(s.Root.FullPath);
            w.Write(s.Title ?? "");
            w.Write(s.ScanDate.Ticks);
            w.Write(s.VolumeTotal);
            w.Write(s.VolumeFree);
            w.Write(s.ClusterSize);
            w.Write(s.FileSystem);
            w.Write(s.MethodUsed);
            w.Write(s.Usn is { } u);
            w.Write(s.Usn?.JournalId ?? 0);
            w.Write(s.Usn?.NextUsn ?? 0);
            w.Write(s.Owners.Count);
            for (var i = 0; i < s.Owners.Count; i++)
                w.Write(s.Owners.Name(i));
            WriteFolder(w, s.Root);
        }
        File.Move(tmp, file, true);
    }

    private static void WriteFolder(BinaryWriter w, DirNode d)
    {
        w.Write(d.Name);
        w.Write7BitEncodedInt64(d.Frn);
        w.Write7BitEncodedInt((int)d.Attributes);
        w.Write7BitEncodedInt64(d.CreatedTicks / TicksPerSecond);
        w.Write7BitEncodedInt64(d._ownModified / TicksPerSecond);
        w.Write7BitEncodedInt64(d._ownAccessed / TicksPerSecond);
        w.Write7BitEncodedInt(d.OwnerId + 1);
        w.Write(d.Error ?? "");
        var files = d.FileList;
        w.Write7BitEncodedInt(files.Count);
        foreach (var f in files)
        {
            w.Write(f.Name);
            w.Write7BitEncodedInt64(f.Size);
            w.Write7BitEncodedInt64(f.Allocated);
            w.Write7BitEncodedInt64(f.CreatedTicks / TicksPerSecond);
            w.Write7BitEncodedInt64(f.ModifiedTicks / TicksPerSecond);
            w.Write7BitEncodedInt64(f.AccessedTicks / TicksPerSecond);
            w.Write7BitEncodedInt((int)f.Attributes);
            w.Write7BitEncodedInt(f.OwnerId + 1);
            w.Write7BitEncodedInt(f.LinkCount);
            w.Write7BitEncodedInt(f.StreamCount);
            w.Write7BitEncodedInt64(f.StreamSize);
        }
        var dirs = d.DirList;
        w.Write7BitEncodedInt(dirs.Count);
        foreach (var c in dirs)
            WriteFolder(w, c);
    }

    /// <summary>The cached scan of <paramref name="path"/>, or null when there is none (or it is unreadable).</summary>
    public static ScanSession? Load(string folder, string path, ScanOptions options)
    {
        var file = FileFor(folder, path);
        if (!File.Exists(file))
            return null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var z = new ZLibStream(fs, CompressionMode.Decompress);
            using var buf = new BufferedStream(z, 1 << 20);
            using var r = new BinaryReader(buf, Encoding.UTF8);
            if (r.ReadString() != Magic)
                return null;
            var p = r.ReadString();
            var rootPath = r.ReadString();
            var s = new ScanSession(p, options)
            {
                Title = r.ReadString() is { Length: > 0 } t ? t : null,
            };
            s.ScanDate = new DateTime(r.ReadInt64(), DateTimeKind.Local);
            s.VolumeTotal = r.ReadInt64();
            s.VolumeFree = r.ReadInt64();
            s.ClusterSize = r.ReadInt32();
            s.FileSystem = r.ReadString();
            s.MethodUsed = r.ReadString() + " (cached)";
            var hasUsn = r.ReadBoolean();
            var jid = r.ReadUInt64();
            var next = r.ReadInt64();
            if (hasUsn)
                s.Usn = new UsnPosition(jid, next);
            var owners = r.ReadInt32();
            for (var i = 0; i < owners; i++)
                s.Owners.GetId(r.ReadString());
            s.Root = ReadFolder(r, null);
            s.Root.Name = rootPath;
            s.Root.Recalculate(options.FolderDateFromFilesOnly);
            s.Root.Sort(true);
            s.State = ScanState.Done;
            s.Started = s.Finished = s.ScanDate;
            s._foldersDone = s.Root.FolderCount;
            s._filesDone = s.Root.FileCount;
            return s;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static DirNode ReadFolder(BinaryReader r, DirNode? parent)
    {
        var d = new DirNode(r.ReadString(), parent) { Pending = false };
        d.Frn = r.Read7BitEncodedInt64();
        d.Attributes = (FileAttributes)r.Read7BitEncodedInt();
        var created = r.Read7BitEncodedInt64() * TicksPerSecond;
        var modified = r.Read7BitEncodedInt64() * TicksPerSecond;
        var accessed = r.Read7BitEncodedInt64() * TicksPerSecond;
        d.SetOwnDates(created, modified, accessed);
        d.OwnerId = r.Read7BitEncodedInt() - 1;
        var error = r.ReadString();
        d.Error = error.Length == 0 ? null : error;
        var files = r.Read7BitEncodedInt();
        d.FileList.Capacity = files;
        for (var i = 0; i < files; i++)
        {
            var f = new FileItem(r.ReadString(), d);
            var size = r.Read7BitEncodedInt64();
            var alloc = r.Read7BitEncodedInt64();
            f.CreatedTicks = r.Read7BitEncodedInt64() * TicksPerSecond;
            f.ModifiedTicks = r.Read7BitEncodedInt64() * TicksPerSecond;
            f.AccessedTicks = r.Read7BitEncodedInt64() * TicksPerSecond;
            f.Attributes = (FileAttributes)r.Read7BitEncodedInt();
            f.OwnerId = r.Read7BitEncodedInt() - 1;
            f.LinkCount = r.Read7BitEncodedInt();
            f.StreamCount = r.Read7BitEncodedInt();
            f.StreamSize = r.Read7BitEncodedInt64();
            f.SetSizes(size, alloc);
            d.FileList.Add(f);
        }
        var dirs = r.Read7BitEncodedInt();
        d.DirList.Capacity = dirs;
        for (var i = 0; i < dirs; i++)
            d.DirList.Add(ReadFolder(r, d));
        return d;
    }

    /// <summary>Removes cache files older than <paramref name="maxAge"/> and keeps the total under <paramref name="maxBytes"/>.</summary>
    public static void Trim(string folder, TimeSpan maxAge, long maxBytes)
    {
        if (!Directory.Exists(folder))
            return;
        var files = new DirectoryInfo(folder).GetFiles("*.bacache").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = 0;
        foreach (var f in files)
        {
            total += f.Length;
            if (DateTime.UtcNow - f.LastWriteTimeUtc > maxAge || total > maxBytes)
            {
                try
                {
                    f.Delete();
                }
                catch (IOException)
                {
                    // in use
                }
            }
        }
    }
}
