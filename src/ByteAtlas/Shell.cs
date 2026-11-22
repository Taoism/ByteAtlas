using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace ByteAtlas;

/// <summary>Operating system helpers: Explorer, properties, command prompt, admin restart, context menu, tasks.</summary>
internal static class Shell
{
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // nothing to open with
        }
    }

    /// <summary>Shows the item in Explorer (selected) / the file manager.</summary>
    private static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    private static string WinDir(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);

    public static void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(path) && Path.GetPathRoot(path) == path)
                Process.Start(WinDir("explorer.exe"), $"\"{path}\"");
            else
                Process.Start(WinDir("explorer.exe"), $"/select,\"{path}\"");
        }
        else
            Open(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
    }

    public static void Properties(string path)
    {
        if (OperatingSystem.IsWindows())
            ByteAtlas.Native.Platform.Properties(path);
    }

    public static void CommandPrompt(string folder)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(Sys("cmd.exe")) { WorkingDirectory = folder, UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("/usr/bin/x-terminal-emulator") { WorkingDirectory = folder, UseShellExecute = true });
    }

    public static bool IsAdmin
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return Environment.UserName == "root";
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ByteAtlas.exe");

    /// <summary>Starts again elevated with the given paths; true when the new process started.</summary>
    public static bool RestartAsAdmin(IEnumerable<string> paths)
    {
        try
        {
            var psi = new ProcessStartInfo(ExePath) { UseShellExecute = true, Verb = "runas" };
            foreach (var p in paths)
                psi.ArgumentList.Add(p);
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC prompt declined
        }
    }

    // ----- Explorer context menu (current user, no admin needed) -----

    private static readonly string[] MenuKeys = [@"Software\Classes\Directory\shell\ByteAtlas", @"Software\Classes\Drive\shell\ByteAtlas"];
    private static readonly string[] SearchKeys = [@"Software\Classes\Directory\shell\ByteAtlasSearch", @"Software\Classes\Drive\shell\ByteAtlasSearch"];

    [SupportedOSPlatform("windows")]
    public static bool ContextMenuInstalled(bool search = false) =>
        Registry.CurrentUser.OpenSubKey((search ? SearchKeys : MenuKeys)[0]) != null;

    [SupportedOSPlatform("windows")]
    public static void SetContextMenu(bool search, bool on)
    {
        foreach (var key in search ? SearchKeys : MenuKeys)
        {
            if (!on)
            {
                Registry.CurrentUser.DeleteSubKeyTree(key, false);
                continue;
            }
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue("", search ? "Search with ByteAtlas" : "Analyze with ByteAtlas");
            k.SetValue("Icon", $"\"{ExePath}\",0");
            using var c = k.CreateSubKey("command");
            c.SetValue("", search ? $"\"{ExePath}\" --search \"%1\"" : $"\"{ExePath}\" \"%1\"");
        }
    }

    // ----- scheduled scans (Windows Task Scheduler, folder \ByteAtlas) -----

    public static string CliPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "byteatlas-cli.exe" : "byteatlas-cli");

    public static (bool Ok, string Output) SchTasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Sys("schtasks.exe")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode == 0, output.Trim());
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (false, e.Message);
        }
    }

    public static void RunAndForget(string exe, string args = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.IsPathRooted(exe) ? exe : Sys(exe), args) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // not available here
        }
    }
}
