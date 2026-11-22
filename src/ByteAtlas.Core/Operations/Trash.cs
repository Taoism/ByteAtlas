using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using ByteAtlas.Native;

namespace ByteAtlas.Operations;

/// <summary>
/// The freedesktop.org trash (GNOME, KDE, Xfce, … and their file managers): files go to ~/.local/share/Trash, or on
/// another file system to its $topdir/.Trash/$uid or $topdir/.Trash-$uid, each with a .trashinfo record so the
/// file manager can restore them. A file that cannot be trashed is left where it is (never deleted instead).
/// </summary>
[SupportedOSPlatform("linux")]
public static class Trash
{
    /// <summary>The home trash folder ($XDG_DATA_HOME/Trash).</summary>
    public static string HomeTrash
    {
        get
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data) || !Path.IsPathRooted(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "Trash");
        }
    }

    /// <summary>Moves a file or folder to the trash; throws IOException when that is not possible.</summary>
    public static void Move(string path, DateTime? now = null)
    {
        path = Path.GetFullPath(path).TrimEnd('/');
        if (!File.Exists(path) && !Directory.Exists(path) && new FileInfo(path).LinkTarget == null)
            throw new FileNotFoundException("Not found", path);
        if (!UnixStat.TryGet(path, out var item))
            throw new IOException("Cannot read " + path);
        var (trash, topdir) = TrashFor(path, item.Device);
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        CreatePrivate(trash);
        CreatePrivate(files);
        CreatePrivate(info);

        // the spec: Path is absolute in the home trash, relative to the top directory in a top-dir trash
        var original = topdir == null ? path : path[(topdir.TrimEnd('/').Length + 1)..];
        var record = new StringBuilder()
            .Append("[Trash Info]\n")
            .Append("Path=").Append(Escape(original)).Append('\n')
            .Append("DeletionDate=").Append((now ?? DateTime.Now).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n')
            .ToString();

        // reserve a free name by creating its .trashinfo exclusively, then move the item in
        var name = Path.GetFileName(path);
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{Path.GetFileNameWithoutExtension(name)}.{n}{Path.GetExtension(name)}";
            var infoFile = Path.Combine(info, candidate + ".trashinfo");
            FileStream fs;
            try
            {
                fs = new FileStream(infoFile, FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException) when (File.Exists(infoFile) && n < 10_000)
            {
                continue;
            }
            using (fs)
                fs.Write(Encoding.UTF8.GetBytes(record));
            var target = Path.Combine(files, candidate);
            if (File.Exists(target) || Directory.Exists(target))
            {
                File.Delete(infoFile);
                continue;
            }
            try
            {
                if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget == null)
                    Directory.Move(path, target);
                else
                    File.Move(path, target);
            }
            catch
            {
                File.Delete(infoFile);
                throw;
            }
            return;
        }
    }

    /// <summary>Trash folder for an item on device <paramref name="device"/>, and the top directory for top-dir trashes.</summary>
    private static (string Trash, string? Topdir) TrashFor(string path, ulong device)
    {
        var home = HomeTrash;
        var homeBase = Path.GetDirectoryName(home)!;
        Directory.CreateDirectory(homeBase);
        if (UnixStat.TryGet(homeBase, out var h) && h.Device == device)
            return (home, null);

        var top = MountTop(path, device);
        var uid = UnixStat.CurrentUid.ToString(CultureInfo.InvariantCulture);
        // $topdir/.Trash/$uid, only when .Trash is a real sticky folder (else others could plant files there)
        var shared = Path.Combine(top, ".Trash");
        var sharedInfo = new DirectoryInfo(shared);
        if (sharedInfo.Exists && sharedInfo.LinkTarget == null && (sharedInfo.UnixFileMode & UnixFileMode.StickyBit) != 0)
        {
            var mine = Path.Combine(shared, uid);
            try
            {
                CreatePrivate(mine);
                return (mine, top);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // fall through to .Trash-$uid
            }
        }
        var own = Path.Combine(top, ".Trash-" + uid);
        try
        {
            CreatePrivate(own);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"No trash on the drive of {path} ({top} is not writable). Delete it for good instead.", e);
        }
        return (own, top);
    }

    /// <summary>The top directory (mount point) of the file system that holds <paramref name="path"/>.</summary>
    private static string MountTop(string path, ulong device)
    {
        var top = Path.GetDirectoryName(path) ?? "/";
        while (Path.GetDirectoryName(top) is { } parent && UnixStat.TryGet(parent, out var p) && p.Device == device)
            top = parent;
        return top;
    }

    private static void CreatePrivate(string dir)
    {
        var d = new DirectoryInfo(dir);
        if (d.Exists)
        {
            if (d.LinkTarget != null)
                throw new IOException(dir + " is a symbolic link: refusing to use it as a trash folder.");
            return;
        }
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Percent-encodes a path for the Path= key (keeps / and unreserved characters).</summary>
    internal static string Escape(string path)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or '~' or '/')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Items and bytes in the home trash.</summary>
    public static (int Items, long Bytes) HomeTrashSize()
    {
        var files = Path.Combine(HomeTrash, "files");
        if (!Directory.Exists(files))
            return (0, 0);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var bytes = Directory.EnumerateFiles(files, "*", opts).Sum(f => new FileInfo(f).Length);
        return (Directory.EnumerateFileSystemEntries(files).Count(), bytes);
    }

    /// <summary>Empties the home trash (files, their records and the size cache).</summary>
    public static OperationResult EmptyHome()
    {
        var errors = new List<string>();
        var done = 0;
        foreach (var sub in new[] { "files", "info", "expunged" })
        {
            var dir = Path.Combine(HomeTrash, sub);
            if (!Directory.Exists(dir))
                continue;
            foreach (var e in Directory.EnumerateFileSystemEntries(dir))
            {
                try
                {
                    if (Directory.Exists(e) && new DirectoryInfo(e).LinkTarget == null)
                        Directory.Delete(e, true);
                    else
                        File.Delete(e);
                    if (sub == "files")
                        done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{e}: {ex.Message}");
                }
            }
        }
        var cache = Path.Combine(HomeTrash, "directorysizes");
        if (File.Exists(cache))
            File.Delete(cache);
        return new OperationResult(done, errors.Count, errors);
    }
}
