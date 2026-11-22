using System.Diagnostics;

namespace ByteAtlas.Cli;

/// <summary>
/// Desktop notes from the command line tool (scheduled scans): Linux desktops get a notification through
/// notify-send when it is there; otherwise ByteAtlas shows a small note in the corner of the screen
/// ("ByteAtlas --notify"). Without a desktop (a task running while nobody is logged in) nothing is shown; the
/// warning is in the output and the exit code.
/// </summary>
internal static class Notifier
{
    public static void Show(string title, string message, string? path)
    {
        if (Environment.GetEnvironmentVariable("BYTEATLAS_NO_NOTIFY") == "1")
            return;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (Environment.GetEnvironmentVariable("DISPLAY") is null && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null)
                    return;
                if (FindOnPath("notify-send") is { } notify)
                {
                    Start(notify, ["--app-name=ByteAtlas", "--icon=byteatlas", "--urgency=critical", title, message]);
                    return;
                }
            }
            var gui = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ByteAtlas.exe" : "ByteAtlas");
            if (File.Exists(gui))
                Start(gui, path == null ? ["--notify", title, message] : ["--notify", title, message, path]);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Console.Error.WriteLine("note not shown: " + e.Message);
        }
    }

    private static void Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p))
                return p;
        }
        return null;
    }
}
