using ByteAtlas.Native;

namespace ByteAtlas.Scanning;

/// <summary>Volume facts for the drive a path lives on.</summary>
public sealed record VolumeInfo(string Root, string FileSystem, int ClusterSize, long Total, long Free, bool IsNetwork, bool IsLocalFixed)
{
    public static VolumeInfo Get(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var d = new DriveInfo(path);
                return new VolumeInfo(d.RootDirectory.FullName, d.DriveFormat, 4096, d.TotalSize, d.AvailableFreeSpace, d.DriveType == DriveType.Network, d.DriveType == DriveType.Fixed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new VolumeInfo(path, "", 4096, 0, 0, false, false);
            }
        }
        var buf = new char[1024];
        var root = Win32.GetVolumePathName(Win32.LongPath(path), buf, (uint)buf.Length) ? new string(buf).TrimEnd('\0') : Path.GetPathRoot(path) ?? path;
        if (root.StartsWith(@"\\?\", StringComparison.Ordinal) && !root.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase) && !root.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            root = root.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + root[8..] : root[4..];
        var cluster = 4096;
        if (Win32.GetDiskFreeSpace(root, out var spc, out var bps, out _, out _) && spc * bps > 0)
            cluster = (int)(spc * bps);
        long total = 0, free = 0;
        if (Win32.GetDiskFreeSpaceEx(root, out _, out var t, out var f))
        {
            total = (long)t;
            free = (long)f;
        }
        var fs = new char[64];
        var fsName = Win32.GetVolumeInformation(root, null, 0, out _, out _, out _, fs, (uint)fs.Length) ? new string(fs).TrimEnd('\0') : "";
        var type = Win32.GetDriveType(root);
        return new VolumeInfo(root, fsName, cluster, total, free, type == 4 || root.StartsWith(@"\\", StringComparison.Ordinal) && !root.StartsWith(@"\\?\", StringComparison.Ordinal), type == 3);
    }
}
