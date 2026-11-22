using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ByteAtlas.Native;

/// <summary>
/// File details the .NET API does not give on Linux: owner, real allocated size, hard-link identity and the
/// device (to stay on one file system). Uses statx(2), whose struct layout is the same on every architecture.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class UnixStat
{
    public readonly record struct Info(uint Uid, long Allocated, uint Links, ulong Device, ulong Inode);

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int dirfd, string path, int flags, uint mask, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();

    /// <summary>The user id this process runs as.</summary>
    public static uint CurrentUid => GetUid();

    private static bool _missing;

    /// <summary>Details of a file or folder (links are not followed); false when it cannot be read.</summary>
    public static bool TryGet(string path, out Info info)
    {
        info = default;
        if (_missing)
            return false;
        var b = new byte[256];
        try
        {
            if (Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxBasicStats, b) != 0)
                return false;
        }
        catch (EntryPointNotFoundException)
        {
            _missing = true; // glibc older than 2.28
            return false;
        }
        // struct statx: nlink @16, uid @20, ino @32, blocks (512 B) @48, dev_major @136, dev_minor @140
        var span = b.AsSpan();
        info = new Info(
            BinaryPrimitives.ReadUInt32LittleEndian(span[20..]),
            (long)BinaryPrimitives.ReadUInt64LittleEndian(span[48..]) * 512,
            BinaryPrimitives.ReadUInt32LittleEndian(span[16..]),
            ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(span[136..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(span[140..]),
            BinaryPrimitives.ReadUInt64LittleEndian(span[32..]));
        return true;
    }

    private static readonly ConcurrentDictionary<uint, string> Users = new();
    private static readonly Lazy<Dictionary<uint, string>> Passwd = new(ReadPasswd);

    /// <summary>User name of a uid (from /etc/passwd; "uid 1234" when unknown).</summary>
    public static string UserName(uint uid) =>
        Users.GetOrAdd(uid, u => Passwd.Value.TryGetValue(u, out var n) ? n : "uid " + u);

    private static Dictionary<uint, string> ReadPasswd()
    {
        var map = new Dictionary<uint, string>();
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var f = line.Split(':');
                if (f.Length > 2 && uint.TryParse(f[2], out var uid))
                    map.TryAdd(uid, f[0]);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // names stay numeric
        }
        return map;
    }
}
