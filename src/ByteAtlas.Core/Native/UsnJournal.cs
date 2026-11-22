using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ByteAtlas.Native;

/// <summary>Where the NTFS change journal of a volume stands.</summary>
public readonly record struct UsnPosition(ulong JournalId, long NextUsn);

/// <summary>
/// The NTFS change journal (USN journal): which folders changed since a known point. Lets a refresh look only at
/// the folders that changed instead of reading the whole drive again.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class UsnJournal
{
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    private static SafeFileHandle Open(string driveRoot) =>
        Win32.CreateFile(@"\\.\" + driveRoot[..2], Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>The current journal position of a drive ("C:\"), or null when there is no journal / no access.</summary>
    public static UsnPosition? Query(string driveRoot)
    {
        if (driveRoot.Length < 2 || driveRoot[1] != ':')
            return null;
        using var h = Open(driveRoot);
        if (h.IsInvalid)
            return null;
        var buf = Marshal.AllocHGlobal(80);
        try
        {
            if (!DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, buf, 80, out _, IntPtr.Zero))
                return null;
            return new UsnPosition((ulong)Marshal.ReadInt64(buf, 0), Marshal.ReadInt64(buf, 16));
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// File reference numbers (record index) of folders whose contents changed since <paramref name="from"/>, and the new
    /// position. Null when the journal was reset or no longer reaches back that far (then a full scan is needed).
    /// Stops early (returns null) when more than <paramref name="maxFolders"/> folders changed.
    /// </summary>
    public static (HashSet<long> Folders, UsnPosition Now)? ChangedFolders(string driveRoot, UsnPosition from, int maxFolders, CancellationToken ct)
    {
        using var h = Open(driveRoot);
        if (h.IsInvalid)
            return null;
        var info = Marshal.AllocHGlobal(80);
        const int OutSize = 1 << 20;
        var outBuf = Marshal.AllocHGlobal(OutSize);
        var input = Marshal.AllocHGlobal(40);
        try
        {
            if (!DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, info, 80, out _, IntPtr.Zero))
                return null;
            var journalId = (ulong)Marshal.ReadInt64(info, 0);
            var firstUsn = Marshal.ReadInt64(info, 8);
            var nextUsn = Marshal.ReadInt64(info, 16);
            if (journalId != from.JournalId || from.NextUsn < firstUsn)
                return null;
            var folders = new HashSet<long>();
            var usn = from.NextUsn;
            var managed = new byte[OutSize];
            while (usn < nextUsn)
            {
                ct.ThrowIfCancellationRequested();
                // READ_USN_JOURNAL_DATA_V0: StartUsn, ReasonMask, ReturnOnlyOnClose, Timeout, BytesToWaitFor, UsnJournalID
                Marshal.WriteInt64(input, 0, usn);
                Marshal.WriteInt32(input, 8, unchecked((int)0xFFFFFFFF));
                Marshal.WriteInt32(input, 12, 0);
                Marshal.WriteInt64(input, 16, 0);
                Marshal.WriteInt64(input, 24, 0);
                Marshal.WriteInt64(input, 32, (long)journalId);
                if (!DeviceIoControl(h, FSCTL_READ_USN_JOURNAL, input, 40, outBuf, OutSize, out var got, IntPtr.Zero) || got <= 8)
                    break;
                Marshal.Copy(outBuf, managed, 0, got);
                var span = managed.AsSpan(0, got);
                var next = BinaryPrimitives.ReadInt64LittleEndian(span);
                var p = 8;
                while (p + 60 <= got)
                {
                    var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[p..]);
                    if (len <= 0 || p + len > got)
                        break;
                    var major = BinaryPrimitives.ReadUInt16LittleEndian(span[(p + 4)..]);
                    if (major == 2)
                    {
                        // USN_RECORD_V2: the parent folder changed (and the item itself, when it is a folder)
                        var frn = (long)(BinaryPrimitives.ReadUInt64LittleEndian(span[(p + 8)..]) & 0xFFFFFFFFFFFF);
                        var parent = (long)(BinaryPrimitives.ReadUInt64LittleEndian(span[(p + 16)..]) & 0xFFFFFFFFFFFF);
                        var attrs = BinaryPrimitives.ReadUInt32LittleEndian(span[(p + 52)..]);
                        folders.Add(parent);
                        if ((attrs & (uint)FileAttributes.Directory) != 0)
                            folders.Add(frn);
                    }
                    else if (major == 3 && p + 76 <= got)
                    {
                        // USN_RECORD_V3: 128-bit file ids (the low 64 bits hold the record number on NTFS)
                        var frn = (long)(BinaryPrimitives.ReadUInt64LittleEndian(span[(p + 8)..]) & 0xFFFFFFFFFFFF);
                        var parent = (long)(BinaryPrimitives.ReadUInt64LittleEndian(span[(p + 24)..]) & 0xFFFFFFFFFFFF);
                        var attrs = BinaryPrimitives.ReadUInt32LittleEndian(span[(p + 68)..]);
                        folders.Add(parent);
                        if ((attrs & (uint)FileAttributes.Directory) != 0)
                            folders.Add(frn);
                    }
                    if (folders.Count > maxFolders)
                        return null;
                    p += len;
                }
                if (next <= usn)
                    break;
                usn = next;
            }
            return (folders, new UsnPosition(journalId, Math.Max(usn, nextUsn)));
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(outBuf);
            Marshal.FreeHGlobal(input);
        }
    }

    /// <summary>The record number (file reference without sequence) of a folder, -1 when it cannot be opened.</summary>
    public static long FolderReference(string path)
    {
        using var h = Win32.CreateFile(Win32.LongPath(path), 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid || !Win32.GetFileInformationByHandle(h, out var info))
            return -1;
        return (long)((((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) & 0xFFFFFFFFFFFF);
    }
}
