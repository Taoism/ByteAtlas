namespace ByteAtlas.Native;

/// <summary>Public face of the Windows calls the GUI needs (no-ops elsewhere).</summary>
public static class Platform
{
    public static void Properties(string path, IntPtr owner = default)
    {
        if (OperatingSystem.IsWindows())
            Win32.SHObjectProperties(owner, Win32.SHOP_FILEPATH, path, null);
    }

    /// <summary>Empties the recycle bin of all drives (Windows asks for confirmation).</summary>
    public static bool EmptyRecycleBin(IntPtr owner = default) =>
        OperatingSystem.IsWindows() && Win32.SHEmptyRecycleBin(owner, null, 0) == 0;

    public static void MapNetworkDrive(IntPtr owner = default)
    {
        if (OperatingSystem.IsWindows())
            Win32.WNetConnectionDialog(owner, 1);
    }

    public static List<string> ServerShares(string server) =>
        OperatingSystem.IsWindows() ? Win32.ServerShares(server) : [];

    public static string? Owner(string path) => OperatingSystem.IsWindows() ? Win32.GetOwner(path) : null;
}
