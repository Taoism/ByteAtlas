using System.Runtime.InteropServices;

namespace ByteAtlas;

/// <summary>Hands a message (with an attachment) to the default mail program via Simple MAPI.</summary>
internal static class Mail
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private sealed class MapiMessage
    {
        public int Reserved;
        public string? Subject;
        public string? NoteText;
        public string? MessageType;
        public string? DateReceived;
        public string? ConversationId;
        public int Flags;
        public IntPtr Originator;
        public int RecipCount;
        public IntPtr Recips;
        public int FileCount;
        public IntPtr Files;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct MapiFileDesc
    {
        public int Reserved;
        public int Flags;
        public int Position;
        public string PathName;
        public string FileName;
        public IntPtr FileType;
    }

    [DllImport("mapi32.dll", CharSet = CharSet.Ansi)]
    private static extern int MAPISendMail(IntPtr session, IntPtr uiParam, MapiMessage message, int flags, int reserved);

    private const int MAPI_LOGON_UI = 0x1, MAPI_DIALOG = 0x8;

    public static bool Send(string subject, string body, string? attachment)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var msg = new MapiMessage { Subject = subject, NoteText = body };
        var mem = IntPtr.Zero;
        try
        {
            if (attachment != null)
            {
                var desc = new MapiFileDesc { Position = -1, PathName = attachment, FileName = Path.GetFileName(attachment) };
                mem = Marshal.AllocHGlobal(Marshal.SizeOf<MapiFileDesc>());
                Marshal.StructureToPtr(desc, mem, false);
                msg.FileCount = 1;
                msg.Files = mem;
            }
            return MAPISendMail(IntPtr.Zero, IntPtr.Zero, msg, MAPI_DIALOG | MAPI_LOGON_UI, 0) is 0 or 1; // 1 = user cancelled
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (mem != IntPtr.Zero)
            {
                Marshal.DestroyStructure<MapiFileDesc>(mem);
                Marshal.FreeHGlobal(mem);
            }
        }
    }
}
