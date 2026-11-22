using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace ByteAtlas.Operations;

public enum ScheduleFrequency
{
    Daily,
    Weekly,
    Monthly,
    AtLogon,
}

/// <summary>A scheduled scan as listed: its name, when it runs, the command and where it is kept.</summary>
public sealed record ScheduledTask(string Name, string When, string Command, string Backend);

/// <summary>
/// Scheduled scans on Linux: a systemd user timer (~/.config/systemd/user/byteatlas-NAME.timer) where the user's
/// systemd runs, else a line in the user's crontab marked "# ByteAtlas: NAME".
/// </summary>
[SupportedOSPlatform("linux")]
public static class UnixScheduler
{
    private const string Marker = "# ByteAtlas: ";

    /// <summary>Where new schedules go: "systemd", "cron", or null when neither is available.</summary>
    public static string? Backend =>
        Run("systemctl", ["--user", "show-environment"]).Exit == 0 ? "systemd"
        : Run("sh", ["-c", "command -v crontab"]).Exit == 0 ? "cron"
        : null;

    public static string UnitFolder => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "systemd", "user");

    /// <summary>Unit / marker name: letters, digits, dash, underscore and dot only.</summary>
    public static string Slug(string name)
    {
        var s = new string(name.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-', '.');
        while (s.Contains("--", StringComparison.Ordinal))
            s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Length == 0 ? "scan" : s.Length > 60 ? s[..60] : s;
    }

    public static string Describe(ScheduleFrequency f, TimeOnly at, DayOfWeek day) => f switch
    {
        ScheduleFrequency.Daily => $"daily at {at:HH\\:mm}",
        ScheduleFrequency.Weekly => $"every {day} at {at:HH\\:mm}",
        ScheduleFrequency.Monthly => $"monthly on the 1st at {at:HH\\:mm}",
        _ => "at log on",
    };

    /// <summary>Creates (or replaces) the schedule <paramref name="name"/> running <paramref name="command"/> (program + arguments).</summary>
    public static string Create(string name, IReadOnlyList<string> command, ScheduleFrequency f, TimeOnly at, DayOfWeek day)
    {
        var slug = Slug(name);
        Delete(slug);
        switch (Backend)
        {
            case "systemd":
                Directory.CreateDirectory(UnitFolder);
                File.WriteAllText(Path.Combine(UnitFolder, $"byteatlas-{slug}.service"), SystemdService(slug, command));
                File.WriteAllText(Path.Combine(UnitFolder, $"byteatlas-{slug}.timer"), SystemdTimer(slug, f, at, day));
                Check(Run("systemctl", ["--user", "daemon-reload"]));
                Check(Run("systemctl", ["--user", "enable", "--now", $"byteatlas-{slug}.timer"]));
                return "systemd";
            case "cron":
                var lines = CronLines();
                lines.Add(CronLine(slug, command, f, at, day));
                WriteCron(lines);
                return "cron";
            default:
                throw new IOException("Neither systemd (user timers) nor cron is available: install cron (cronie) to schedule scans.");
        }
    }

    /// <summary>The ByteAtlas schedules in systemd and in the crontab.</summary>
    public static List<ScheduledTask> List()
    {
        var list = new List<ScheduledTask>();
        if (Directory.Exists(UnitFolder))
        {
            foreach (var timer in Directory.EnumerateFiles(UnitFolder, "byteatlas-*.timer").Order(StringComparer.Ordinal))
            {
                var slug = Path.GetFileNameWithoutExtension(timer)["byteatlas-".Length..];
                var when = File.ReadLines(timer).FirstOrDefault(l => l.StartsWith("# When: ", StringComparison.Ordinal))?[8..] ?? "";
                var service = Path.ChangeExtension(timer, ".service");
                var exec = File.Exists(service) ? File.ReadLines(service).FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal))?[10..] ?? "" : "";
                list.Add(new ScheduledTask(slug, when, exec, "systemd"));
            }
        }
        foreach (var line in CronLines())
        {
            var m = line.LastIndexOf(Marker, StringComparison.Ordinal);
            if (m < 0)
                continue;
            var fields = line[..m].Trim();
            var cut = fields.StartsWith('@') ? fields.IndexOf(' ') : NthSpace(fields, 5);
            list.Add(new ScheduledTask(line[(m + Marker.Length)..].Trim(), cut > 0 ? fields[..cut] : "", cut > 0 ? fields[(cut + 1)..] : fields, "cron"));
        }
        return list;
    }

    /// <summary>Removes the schedule (timer and service, or crontab line). False when there was none.</summary>
    public static bool Delete(string name)
    {
        var slug = Slug(name);
        var found = false;
        var timer = Path.Combine(UnitFolder, $"byteatlas-{slug}.timer");
        if (File.Exists(timer))
        {
            Run("systemctl", ["--user", "disable", "--now", $"byteatlas-{slug}.timer"]);
            File.Delete(timer);
            File.Delete(Path.ChangeExtension(timer, ".service"));
            Run("systemctl", ["--user", "daemon-reload"]);
            found = true;
        }
        var lines = CronLines();
        var kept = lines.Where(l => !l.TrimEnd().EndsWith(Marker + slug, StringComparison.Ordinal)).ToList();
        if (kept.Count != lines.Count)
        {
            WriteCron(kept);
            found = true;
        }
        return found;
    }

    // ---------------- systemd ----------------

    internal static string SystemdService(string slug, IReadOnlyList<string> command) =>
        $"""
        [Unit]
        Description=ByteAtlas scheduled scan: {slug}

        [Service]
        Type=oneshot
        ExecStart={string.Join(' ', command.Select(SystemdQuote))}
        Nice=10
        IOSchedulingClass=idle

        """;

    internal static string SystemdTimer(string slug, ScheduleFrequency f, TimeOnly at, DayOfWeek day)
    {
        var time = at.ToString("HH:mm:00", CultureInfo.InvariantCulture);
        var trigger = f switch
        {
            ScheduleFrequency.Daily => $"OnCalendar=*-*-* {time}\nPersistent=true",
            ScheduleFrequency.Weekly => $"OnCalendar={day.ToString()[..3]} *-*-* {time}\nPersistent=true",
            ScheduleFrequency.Monthly => $"OnCalendar=*-*-01 {time}\nPersistent=true",
            _ => "OnStartupSec=2min",
        };
        return $"""
            # When: {Describe(f, at, day)}
            [Unit]
            Description=ByteAtlas scheduled scan: {slug}

            [Timer]
            {trigger}

            [Install]
            WantedBy=timers.target

            """;
    }

    /// <summary>systemd ExecStart quoting: "…" with \ and " escaped, % and $ doubled (no expansion).</summary>
    internal static string SystemdQuote(string arg) =>
        "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$") + "\"";

    // ---------------- cron ----------------

    internal static string CronLine(string slug, IReadOnlyList<string> command, ScheduleFrequency f, TimeOnly at, DayOfWeek day)
    {
        var when = f switch
        {
            ScheduleFrequency.Daily => $"{at.Minute} {at.Hour} * * *",
            ScheduleFrequency.Weekly => $"{at.Minute} {at.Hour} * * {(int)day}",
            ScheduleFrequency.Monthly => $"{at.Minute} {at.Hour} 1 * *",
            _ => "@reboot",
        };
        // sh quoting; % ends the command in a crontab line, so it is escaped
        var cmd = string.Join(' ', command.Select(a => "'" + a.Replace("'", "'\\''") + "'")).Replace("%", "\\%");
        return $"{when} {cmd} >/dev/null 2>&1 {Marker}{slug}";
    }

    private static List<string> CronLines()
    {
        var r = Run("crontab", ["-l"]);
        // "no crontab for user" is an empty crontab
        return r.Exit == 0 ? [.. r.Output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)] : [];
    }

    private static void WriteCron(List<string> lines) =>
        Check(Run("crontab", ["-"], string.Join('\n', lines) + "\n"));

    private static int NthSpace(string s, int n)
    {
        var i = -1;
        for (var k = 0; k < n; k++)
        {
            i = s.IndexOf(' ', i + 1);
            if (i < 0)
                return -1;
        }
        return i;
    }

    // ---------------- processes ----------------

    private static void Check((int Exit, string Output) r)
    {
        if (r.Exit != 0)
            throw new IOException(r.Output.Trim().Length > 0 ? r.Output.Trim() : "exit code " + r.Exit);
    }

    private static (int Exit, string Output) Run(string exe, string[] args, string? input = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input != null,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            if (input != null)
            {
                p.StandardInput.Write(input);
                p.StandardInput.Close();
            }
            var err = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output + err.Result);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (-1, e.Message);
        }
    }
}
