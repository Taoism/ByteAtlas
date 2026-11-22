using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ByteAtlas.Native;

/// <summary>How a file is compressed on disk.</summary>
public enum CompressionFormat
{
    None,
    /// <summary>Classic NTFS compression (LZNT1, the "compress contents" attribute).</summary>
    Ntfs,
    Xpress4K,
    Lzx,
    Xpress8K,
    Xpress16K,
}

/// <summary>
/// Windows compact compression (the Windows Overlay Filter: XPRESS 4K/8K/16K, LZX, as used by compact.exe /exe)
/// and classic NTFS compression: read the format, compress, uncompress.
/// </summary>
public static partial class Compression
{
    private const uint FSCTL_GET_EXTERNAL_BACKING = 0x90310;
    private const uint FSCTL_SET_EXTERNAL_BACKING = 0x9030C;
    private const uint FSCTL_DELETE_EXTERNAL_BACKING = 0x90314;
    private const uint WOF_PROVIDER_FILE = 2;
    private const int ERROR_OBJECT_NOT_EXTERNALLY_BACKED = 342;

    [StructLayout(LayoutKind.Sequential)]
    private struct WofFileInfo
    {
        // WOF_EXTERNAL_INFO
        public uint WofVersion;
        public uint Provider;
        // FILE_PROVIDER_EXTERNAL_INFO_V1
        public uint FileVersion;
        public uint Algorithm;
        public uint Flags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle h, uint code, ref WofFileInfo inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out WofFileInfo outBuf, int outSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    public static string Name(CompressionFormat f) => f switch
    {
        CompressionFormat.Ntfs => "NTFS (LZNT1)",
        CompressionFormat.Xpress4K => "XPRESS 4K",
        CompressionFormat.Xpress8K => "XPRESS 8K",
        CompressionFormat.Xpress16K => "XPRESS 16K",
        CompressionFormat.Lzx => "LZX",
        _ => "",
    };

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle Open(string path, bool write) =>
        Win32.CreateFile(Win32.LongPath(path), write ? Win32.GENERIC_READ | Win32.GENERIC_WRITE : 0x80 /* FILE_READ_ATTRIBUTES */,
            Win32.FILE_SHARE_READ | (write ? 0 : Win32.FILE_SHARE_WRITE) | Win32.FILE_SHARE_DELETE, IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

    /// <summary>The compression of one file (None when it cannot be read).</summary>
    [SupportedOSPlatform("windows")]
    public static CompressionFormat Get(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Compressed) != 0)
            return CompressionFormat.Ntfs;
        // the overlay filter hides its reparse point and sparse attributes: ask the file itself
        if ((attributes & FileAttributes.Directory) != 0)
            return CompressionFormat.None;
        using var h = Open(path, false);
        if (h.IsInvalid)
            return CompressionFormat.None;
        if (!DeviceIoControl(h, FSCTL_GET_EXTERNAL_BACKING, IntPtr.Zero, 0, out WofFileInfo info, Marshal.SizeOf<WofFileInfo>(), out _, IntPtr.Zero))
            return CompressionFormat.None;
        return info.Provider != WOF_PROVIDER_FILE ? CompressionFormat.None : info.Algorithm switch
        {
            0 => CompressionFormat.Xpress4K,
            1 => CompressionFormat.Lzx,
            2 => CompressionFormat.Xpress8K,
            3 => CompressionFormat.Xpress16K,
            _ => CompressionFormat.None,
        };
    }

    [SupportedOSPlatform("windows")]
    /// <summary>Compresses one file with a compact-compression algorithm (the file stays readable; writing to it uncompresses it).</summary>
    public static bool SetCompact(string path, CompressionFormat format, out string? error)
    {
        error = null;
        uint algorithm = format switch
        {
            CompressionFormat.Lzx => 1,
            CompressionFormat.Xpress8K => 2,
            CompressionFormat.Xpress16K => 3,
            _ => 0,
        };
        using var h = Open(path, true);
        if (h.IsInvalid)
        {
            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        var info = new WofFileInfo { WofVersion = 1, Provider = WOF_PROVIDER_FILE, FileVersion = 1, Algorithm = algorithm };
        if (!DeviceIoControl(h, FSCTL_SET_EXTERNAL_BACKING, ref info, Marshal.SizeOf<WofFileInfo>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            var code = Marshal.GetLastWin32Error();
            error = code == 344 // ERROR_COMPRESSION_NOT_BENEFICIAL
                ? "does not get smaller"
                : new System.ComponentModel.Win32Exception(code).Message;
            return code == 344;
        }
        return true;
    }

    /// <summary>Removes compact compression from one file.</summary>
    [SupportedOSPlatform("windows")]
    public static bool RemoveCompact(string path, out string? error)
    {
        error = null;
        using var h = Open(path, true);
        if (h.IsInvalid)
        {
            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        if (!DeviceIoControl(h, FSCTL_DELETE_EXTERNAL_BACKING, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            var code = Marshal.GetLastWin32Error();
            if (code == ERROR_OBJECT_NOT_EXTERNALLY_BACKED)
                return true;
            error = new System.ComponentModel.Win32Exception(code).Message;
            return false;
        }
        return true;
    }
}
