using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ByteAtlas.Native;

[SupportedOSPlatform("windows")]
internal static partial class Win32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint INVALID_FILE_SIZE = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetCompressedFileSize(string name, out uint high);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string root, out ulong freeToCaller, out ulong total, out ulong free);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string path, [Out] char[] volume, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(string root, [Out] char[]? name, uint nameLen, out uint serial, out uint maxComponent, out uint flags, [Out] char[] fsName, uint fsNameLen);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveType(string root);

    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIME is 4-byte aligned
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string cStreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstStreamW(string fileName, int infoLevel, out WIN32_FIND_STREAM_DATA data, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextStreamW(IntPtr h, out WIN32_FIND_STREAM_DATA data);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(IntPtr h);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateHardLink(string newName, string existing, IntPtr security);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle h, uint code, ref ushort inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    public const uint FSCTL_SET_COMPRESSION = 0x9C040;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle h, uint code, ref StoragePropertyQuery inBuf, int inSize, out SeekPenaltyDescriptor outBuf, int outSize, out int returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    /// <summary>True for spinning disks (seeks are expensive), false for SSDs, null when the volume does not say.</summary>
    public static bool? HasSeekPenalty(SafeFileHandle volume)
    {
        const uint IoctlStorageQueryProperty = 0x2D1400;
        var q = new StoragePropertyQuery { PropertyId = 7 }; // StorageDeviceSeekPenaltyProperty
        return DeviceIoControl(volume, IoctlStorageQueryProperty, ref q, Marshal.SizeOf<StoragePropertyQuery>(), out var d, Marshal.SizeOf<SeekPenaltyDescriptor>(), out var n, IntPtr.Zero) && n >= 9
            ? d.IncursSeekPenalty != 0
            : null;
    }

    // ----- security -----

    public const int SE_FILE_OBJECT = 1;
    public const uint OWNER_SECURITY_INFORMATION = 1;

    [LibraryImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetNamedSecurityInfo(string name, int type, uint info, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr sd);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr LocalFree(IntPtr mem);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> AccountNames = new();

    /// <summary>Owner of a file or folder as "DOMAIN\user" (or the SID when it does not resolve); null on failure.</summary>
    public static string? GetOwner(string path)
    {
        if (GetNamedSecurityInfo(LongPath(path), SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var sd) != 0)
            return null;
        try
        {
            if (owner == IntPtr.Zero)
                return null;
            var sid = new SecurityIdentifier(owner);
            return AccountName(sid);
        }
        finally
        {
            LocalFree(sd);
        }
    }

    public static string AccountName(SecurityIdentifier sid)
    {
        var key = sid.Value;
        return AccountNames.GetOrAdd(key, _ =>
        {
            try
            {
                return sid.Translate(typeof(NTAccount)).Value;
            }
            catch (IdentityNotMappedException)
            {
                return key;
            }
            catch (SystemException)
            {
                return key;
            }
        });
    }

    /// <summary>Adds the \\?\ prefix for paths longer than MAX_PATH (APIs that are not long-path aware).</summary>
    public static string LongPath(string path)
    {
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    /// <summary>Space taken on disk by a compressed or sparse file (-1 on failure).</summary>
    public static long CompressedSize(string path)
    {
        var low = GetCompressedFileSize(LongPath(path), out var high);
        if (low == INVALID_FILE_SIZE && Marshal.GetLastWin32Error() != 0)
            return -1;
        return ((long)high << 32) | low;
    }

    /// <summary>Turns NTFS compression on or off for one file or folder.</summary>
    public static bool SetCompression(string path, bool compress, out string? error)
    {
        error = null;
        using var h = CreateFile(LongPath(path), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid)
        {
            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        ushort format = compress ? (ushort)1 : (ushort)0; // COMPRESSION_FORMAT_DEFAULT / NONE
        if (!DeviceIoControl(h, FSCTL_SET_COMPRESSION, ref format, 2, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        return true;
    }

    // ----- shell -----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    public const uint FO_DELETE = 3;
    public const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "SHObjectProperties", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);

    public const uint SHOP_FILEPATH = 2;

    /// <summary>Moves files or folders to the recycle bin (or deletes them for good).</summary>
    public static bool ShellDelete(IEnumerable<string> paths, bool recycle, IntPtr owner = default)
    {
        var from = string.Join('\0', paths) + "\0\0";
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = (ushort)(FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | (recycle ? FOF_ALLOWUNDO : 0)),
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    [LibraryImport("mpr.dll")]
    public static partial int WNetConnectionDialog(IntPtr hwnd, uint type);

    // ----- network shares -----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHARE_INFO_1
    {
        public string shi1_netname;
        public uint shi1_type;
        public string shi1_remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetShareEnum(string server, int level, out IntPtr buf, int prefMaxLen, out int entriesRead, out int totalEntries, ref int resume);

    [DllImport("netapi32.dll")]
    public static extern int NetApiBufferFree(IntPtr buf);

    /// <summary>Disk shares of a server as \\server\share paths.</summary>
    public static List<string> ServerShares(string server)
    {
        var list = new List<string>();
        server = server.TrimStart('\\');
        var resume = 0;
        if (NetShareEnum(server, 1, out var buf, -1, out var read, out _, ref resume) != 0)
            return list;
        try
        {
            var size = Marshal.SizeOf<SHARE_INFO_1>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<SHARE_INFO_1>(buf + i * size);
                if ((info.shi1_type & 0xFF) == 0 && !info.shi1_netname.EndsWith('$'))
                    list.Add($@"\\{server}\{info.shi1_netname}");
            }
        }
        finally
        {
            NetApiBufferFree(buf);
        }
        return list;
    }
}
