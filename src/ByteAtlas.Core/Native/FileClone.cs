using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ByteAtlas.Native;

/// <summary>
/// Copy-on-write clones (ReFS / Dev Drive block cloning on Windows, FICLONE on btrfs / XFS / bcachefs on Linux) and
/// hard links on Linux. A clone shares the blocks of its source until one of them is changed, so unlike a hard link,
/// changing one file later does not change the other.
/// </summary>
internal static partial class FileClone
{
    // ---------- Windows ----------

    private const uint FsctlDuplicateExtentsToFile = 0x00098344;
    private const uint FsctlGetIntegrityInformation = 0x0009027C;
    private const uint FsctlSetIntegrityInformation = 0x0009C280;
    private const uint FsctlSetSparse = 0x000900C4;
    private const uint FileSupportsBlockRefcounting = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct DuplicateExtentsData
    {
        public IntPtr FileHandle;
        public long SourceFileOffset;
        public long TargetFileOffset;
        public long ByteCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GetIntegrityBuffer
    {
        public ushort ChecksumAlgorithm;
        public ushort Reserved;
        public uint Flags;
        public uint ChecksumChunkSizeInBytes;
        public uint ClusterSizeInBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetIntegrityBuffer
    {
        public ushort ChecksumAlgorithm;
        public ushort Reserved;
        public uint Flags;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Duplicate(SafeFileHandle h, uint code, ref DuplicateExtentsData inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIntegrity(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out GetIntegrityBuffer outBuf, int outSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetIntegrity(SafeFileHandle h, uint code, ref SetIntegrityBuffer inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSparse(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    // ---------- Linux ----------

    private const ulong Ficlone = 0x40049409;

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoctlClone(SafeFileHandle fd, ulong request, SafeFileHandle source);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existing, string newName);

    /// <summary>Whether the drive of <paramref name="path"/> can clone (Windows: asks the volume; Linux: only trying tells).</summary>
    public static bool CanClone(string path)
    {
        if (!OperatingSystem.IsWindows())
            return OperatingSystem.IsLinux();
        var root = VolumeRoot(path);
        var fs = new char[64];
        return Win32.GetVolumeInformation(root, null, 0, out _, out _, out var flags, fs, (uint)fs.Length) && (flags & FileSupportsBlockRefcounting) != 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string VolumeRoot(string path)
    {
        var buf = new char[1024];
        return Win32.GetVolumePathName(Win32.LongPath(path), buf, (uint)buf.Length) ? new string(buf).TrimEnd('\0') : Path.GetPathRoot(path) ?? path;
    }

    /// <summary>Creates <paramref name="target"/> (must not exist) as a copy-on-write clone of <paramref name="source"/>.</summary>
    public static void Clone(string source, string target)
    {
        if (OperatingSystem.IsWindows())
            CloneWindows(source, target);
        else if (OperatingSystem.IsLinux())
            CloneLinux(source, target);
        else
            throw new PlatformNotSupportedException("Copy-on-write clones are made on Windows and Linux only.");
    }

    private static void CloneWindows(string source, string target)
    {
        using var src = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var length = RandomAccess.GetLength(src);
        var ok = false;
        var dst = File.OpenHandle(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            // the target must match the source: integrity streams (ReFS), sparse
            if (GetIntegrity(src, FsctlGetIntegrityInformation, IntPtr.Zero, 0, out var gi, Marshal.SizeOf<GetIntegrityBuffer>(), out _, IntPtr.Zero))
            {
                var si = new SetIntegrityBuffer { ChecksumAlgorithm = gi.ChecksumAlgorithm, Flags = gi.Flags };
                SetIntegrity(dst, FsctlSetIntegrityInformation, ref si, Marshal.SizeOf<SetIntegrityBuffer>(), IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
            var cluster = gi.ClusterSizeInBytes > 0 ? gi.ClusterSizeInBytes : 4096u;
            if ((File.GetAttributes(source) & FileAttributes.SparseFile) != 0)
                SetSparse(dst, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            RandomAccess.SetLength(dst, length);
            // whole clusters, at most 1 GB per call (older ReFS refuses 4 GB and more). The source handle goes
            // inside the input struct, so it is held (AddRef) while the raw value is in use.
            const long Chunk = 1L << 30;
            var held = false;
            try
            {
                src.DangerousAddRef(ref held);
                var raw = src.DangerousGetHandle();
                for (long off = 0; off < length; off += Chunk)
                {
                    var count = Math.Min(Chunk, length - off);
                    count = (count + cluster - 1) / cluster * cluster;
                    var d = new DuplicateExtentsData { FileHandle = raw, SourceFileOffset = off, TargetFileOffset = off, ByteCount = count };
                    if (!Duplicate(dst, FsctlDuplicateExtentsToFile, ref d, Marshal.SizeOf<DuplicateExtentsData>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
                    {
                        var err = Marshal.GetLastWin32Error();
                        throw new IOException(err is 1 or 50 // ERROR_INVALID_FUNCTION, ERROR_NOT_SUPPORTED
                            ? "this drive cannot clone files (block cloning needs ReFS or a Dev Drive)"
                            : new Win32Exception(err).Message);
                    }
                }
            }
            finally
            {
                if (held)
                    src.DangerousRelease();
            }
            ok = true;
        }
        finally
        {
            dst.Dispose();
            if (!ok)
                TryDelete(target);
        }
    }

    private static void CloneLinux(string source, string target)
    {
        using var src = File.OpenHandle(source, FileMode.Open, FileAccess.Read);
        var ok = false;
        var dst = File.OpenHandle(target, FileMode.CreateNew, FileAccess.Write);
        try
        {
            if (IoctlClone(dst, Ficlone, src) != 0)
            {
                var err = Marshal.GetLastPInvokeError();
                throw new IOException(err is 95 or 25 or 22 or 18 // EOPNOTSUPP, ENOTTY, EINVAL, EXDEV
                    ? "this file system cannot clone files (needs btrfs, XFS with reflink or bcachefs, on one file system)"
                    : Marshal.GetPInvokeErrorMessage(err));
            }
            ok = true;
        }
        finally
        {
            dst.Dispose();
            if (!ok)
                TryDelete(target);
        }
    }

    // ---------- where the data is ----------

    private const uint FsctlGetRetrievalPointers = 0x00090073;
    private const uint FsIocFiemap = 0xC020660B;

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IoctlBytes(SafeFileHandle h, uint code, [In] byte[] inBuf, int inSize, [Out] byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoctlBuffer(SafeFileHandle fd, ulong request, [In, Out] byte[] arg);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> CloneVolumes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where the first block of a file's data lies (volume + cluster / device + physical offset), on drives that can
    /// clone: copies that share their blocks (clones, hard links) give the same answer. Null when unknown (other
    /// drives, empty or tiny files kept in the file record).
    /// </summary>
    public static (ulong, ulong)? FirstBlock(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var root = VolumeRoot(path);
                if (!CloneVolumes.GetOrAdd(root, CanClone))
                    return null;
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var output = new byte[32];
                // RETRIEVAL_POINTERS_BUFFER: ExtentCount, StartingVcn, then NextVcn + Lcn of the first extent
                if (!IoctlBytes(h, FsctlGetRetrievalPointers, new byte[8], 8, output, output.Length, out var n, IntPtr.Zero) && Marshal.GetLastWin32Error() != 234 /* ERROR_MORE_DATA */)
                    return null;
                if (n < 32 || BitConverter.ToUInt32(output, 0) == 0)
                    return null;
                var lcn = BitConverter.ToInt64(output, 24);
                if (lcn < 0)
                    return null; // sparse / compressed run
                var serial = Win32.GetVolumeInformation(root, null, 0, out var s, out _, out _, new char[64], 64) ? s : 0u;
                return (serial, (ulong)lcn);
            }
            if (OperatingSystem.IsLinux())
            {
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
                // struct fiemap (32 bytes) + one struct fiemap_extent (56 bytes)
                var buf = new byte[32 + 56];
                BitConverter.TryWriteBytes(buf.AsSpan(8), ulong.MaxValue); // fm_length: whole file
                BitConverter.TryWriteBytes(buf.AsSpan(24), 1u);            // fm_extent_count
                if (IoctlBuffer(h, FsIocFiemap, buf) != 0 || BitConverter.ToUInt32(buf, 20) == 0)
                    return null;
                var flags = BitConverter.ToUInt32(buf, 32 + 40);
                if ((flags & 0x2000) == 0) // FIEMAP_EXTENT_SHARED: not shared, the file id says enough
                    return null;
                var physical = BitConverter.ToUInt64(buf, 32 + 8);
                return Native.UnixStat.TryGet(path, out var i) ? (i.Device, physical) : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // unreadable: unknown
        }
        return null;
    }

    /// <summary>Linux: <paramref name="newName"/> becomes a hard link to <paramref name="existing"/>.</summary>
    public static void HardLinkUnix(string existing, string newName)
    {
        if (Link(existing, newName) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new IOException(err == 18 ? "on another file system" : Marshal.GetPInvokeErrorMessage(err));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // left behind; the caller reports the error
        }
    }
}
