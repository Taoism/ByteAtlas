using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ByteAtlas;

/// <summary>
/// The real Windows Explorer context menu for files and folders (Open with, Send to, 7-Zip, Git, …), shown at the
/// mouse position. Owner-drawn submenus get their window messages through a temporary window subclass.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ShellMenu
{
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, IntPtr name, IntPtr eaten, IntPtr pidl, IntPtr attrs);
        void EnumObjects(IntPtr hwnd, int flags, IntPtr enumIdList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwnd, ref Guid riid, IntPtr ppv);
        void GetAttributesOf(uint cidl, IntPtr apidl, ref uint inOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr pidlLast);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(IntPtr pidl);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr hmenu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT p);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int key);

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(IntPtr hwnd, IntPtr proc, UIntPtr id, UIntPtr refData);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(IntPtr hwnd, IntPtr proc, UIntPtr id);

    [LibraryImport("comctl32.dll")]
    private static partial IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint CMF_NORMAL = 0, CMF_EXPLORE = 4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2;
    private const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
    private const uint FirstCmd = 1, LastCmd = 0x7FFF;
    private const uint WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120, WM_MENUSELECT = 0x11F;

    public static bool ShiftDown => (GetKeyState(0x10) & 0x8000) != 0;

    private static IContextMenu2? _menu2;
    private static IContextMenu3? _menu3;
    private static readonly SubclassProc Proc = WndProc;
    private static readonly IntPtr ProcPtr = Marshal.GetFunctionPointerForDelegate(Proc);

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR or WM_MENUSELECT)
        {
            if (_menu3 != null && _menu3.HandleMenuMsg2(msg, wParam, lParam, out var result) == 0)
                return msg == WM_MENUCHAR ? result : IntPtr.Zero;
            if (_menu2 != null && _menu2.HandleMenuMsg(msg, wParam, lParam) == 0)
                return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Shows the Explorer menu for the items (those in the same folder as the first) at the mouse.</summary>
    public static void Show(IntPtr hwnd, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        var parentDir = Path.GetDirectoryName(paths[0]);
        var same = paths.Where(p => string.Equals(Path.GetDirectoryName(p), parentDir, StringComparison.OrdinalIgnoreCase)).Take(500).ToList();
        var pidls = new List<IntPtr>();
        IntPtr folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero, hmenu = IntPtr.Zero;
        object? folderObj = null, menuObj = null;
        try
        {
            var children = new List<IntPtr>();
            foreach (var p in same)
            {
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                    continue;
                pidls.Add(pidl);
                var iidFolder = typeof(IShellFolder).GUID;
                if (SHBindToParent(pidl, ref iidFolder, out var parent, out var last) != 0)
                    continue;
                if (folderPtr == IntPtr.Zero)
                    folderPtr = parent;
                else
                    Marshal.Release(parent);
                children.Add(last);
            }
            if (folderPtr == IntPtr.Zero || children.Count == 0)
                return;
            folderObj = Marshal.GetObjectForIUnknown(folderPtr);
            var folder = (IShellFolder)folderObj;
            var iidMenu = typeof(IContextMenu).GUID;
            if (folder.GetUIObjectOf(hwnd, (uint)children.Count, [.. children], ref iidMenu, IntPtr.Zero, out menuPtr) != 0 || menuPtr == IntPtr.Zero)
                return;
            menuObj = Marshal.GetObjectForIUnknown(menuPtr);
            var menu = (IContextMenu)menuObj;
            _menu2 = menuObj as IContextMenu2;
            _menu3 = menuObj as IContextMenu3;
            hmenu = CreatePopupMenu();
            var flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (ShiftDown ? CMF_EXTENDEDVERBS : 0);
            if (menu.QueryContextMenu(hmenu, 0, FirstCmd, LastCmd, flags) < 0)
                return;
            GetCursorPos(out var pt);
            SetWindowSubclass(hwnd, ProcPtr, 1, 0);
            int cmd;
            try
            {
                cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);
            }
            finally
            {
                RemoveWindowSubclass(hwnd, ProcPtr, 1);
            }
            if (cmd > 0)
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                    hwnd = hwnd,
                    lpVerb = (IntPtr)(cmd - (int)FirstCmd),
                    lpVerbW = (IntPtr)(cmd - (int)FirstCmd),
                    nShow = 1,
                    ptInvoke = pt,
                };
                menu.InvokeCommand(ref info);
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException)
        {
            // a broken shell extension: nothing to show
        }
        finally
        {
            _menu2 = null;
            _menu3 = null;
            if (hmenu != IntPtr.Zero)
                DestroyMenu(hmenu);
            if (menuObj != null)
                Marshal.ReleaseComObject(menuObj);
            if (menuPtr != IntPtr.Zero)
                Marshal.Release(menuPtr);
            if (folderObj != null)
                Marshal.ReleaseComObject(folderObj);
            if (folderPtr != IntPtr.Zero)
                Marshal.Release(folderPtr);
            foreach (var p in pidls)
                ILFree(p);
        }
    }
}
