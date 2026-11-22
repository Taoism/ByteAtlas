#pragma warning disable CA1416 // the formatting helpers are plain string code; the file-system tests check for Linux first
using ByteAtlas.Operations;

namespace ByteAtlas.Tests;

public class LinuxFormatTests
{
    [Fact]
    public void TrashPathEscaping()
    {
        Assert.Equal("/home/me/a%20b/%C3%BC%25.txt", Trash.Escape("/home/me/a b/ü%.txt"));
        Assert.Equal("rel/x-y_z.~1", Trash.Escape("rel/x-y_z.~1"));
    }

    [Fact]
    public void SchedulerNamesAndQuoting()
    {
        Assert.Equal("Scan-home-me", UnixScheduler.Slug("Scan /home/me"));
        Assert.Equal("scan", UnixScheduler.Slug("///"));
        Assert.Equal("\"a b\"", UnixScheduler.SystemdQuote("a b"));
        Assert.Equal("\"100%% $$HOME \\\"q\\\" \\\\\"", UnixScheduler.SystemdQuote("100% $HOME \"q\" \\"));
    }

    [Fact]
    public void CronLines()
    {
        var line = UnixScheduler.CronLine("x", ["/opt/byteatlas/byteatlas-cli", "scan", "/data/it's", "--export", "/r/d_{date}%.xlsx"],
            ScheduleFrequency.Weekly, new TimeOnly(3, 5), DayOfWeek.Tuesday);
        Assert.Equal("5 3 * * 2 '/opt/byteatlas/byteatlas-cli' 'scan' '/data/it'\\''s' '--export' '/r/d_{date}\\%.xlsx' >/dev/null 2>&1 # ByteAtlas: x", line);
        Assert.StartsWith("0 4 1 * * ", UnixScheduler.CronLine("x", ["c"], ScheduleFrequency.Monthly, new TimeOnly(4, 0), DayOfWeek.Monday), StringComparison.Ordinal);
        Assert.StartsWith("@reboot ", UnixScheduler.CronLine("x", ["c"], ScheduleFrequency.AtLogon, default, DayOfWeek.Monday), StringComparison.Ordinal);
    }

    [Fact]
    public void SystemdTimers()
    {
        var weekly = UnixScheduler.SystemdTimer("x", ScheduleFrequency.Weekly, new TimeOnly(3, 0), DayOfWeek.Sunday);
        Assert.Contains("OnCalendar=Sun *-*-* 03:00:00\nPersistent=true", weekly.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("# When: every Sunday at 03:00", weekly, StringComparison.Ordinal);
        Assert.Contains("OnCalendar=*-*-01 22:30:00", UnixScheduler.SystemdTimer("x", ScheduleFrequency.Monthly, new TimeOnly(22, 30), DayOfWeek.Monday), StringComparison.Ordinal);
        Assert.Contains("OnStartupSec=", UnixScheduler.SystemdTimer("x", ScheduleFrequency.AtLogon, default, DayOfWeek.Monday), StringComparison.Ordinal);
        Assert.Contains("ExecStart=\"/opt/b\" \"scan\" \"/a b\"", UnixScheduler.SystemdService("x", ["/opt/b", "scan", "/a b"]), StringComparison.Ordinal);
    }
}

public class WindowsTaskTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("C:\\Program Files\\x.exe", "\"C:\\Program Files\\x.exe\"")]
    [InlineData("C:\\", "C:\\")]
    [InlineData("D:\\My Folder\\", "\"D:\\My Folder\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void QuotesLikeCommandLineToArgv(string arg, string expected) => Assert.Equal(expected, WindowsTask.Quote(arg));

    [Fact]
    public void TaskXmlKeepsLongCommands()
    {
        var longPath = "C:\\" + string.Join('\\', Enumerable.Repeat("a long folder name", 20));
        var xml = WindowsTask.Xml(["C:\\Program Files\\ByteAtlas\\byteatlas-cli.exe", "scan", longPath, "--export", longPath + "\\r_{date}.xlsx"],
            ScheduleFrequency.Weekly, new TimeOnly(3, 0), DayOfWeek.Sunday, "PC\\me & you", new DateTime(2026, 11, 21, 21, 0, 0));
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        XNamespaceTest(doc, out var ns);
        Assert.Equal("C:\\Program Files\\ByteAtlas\\byteatlas-cli.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.True(doc.Descendants(ns + "Arguments").Single().Value.Length > 261);
        Assert.Equal("2026-11-22T03:00:00", doc.Descendants(ns + "StartBoundary").Single().Value);
        Assert.Single(doc.Descendants(ns + "Sunday"));
        Assert.Equal("PC\\me & you", doc.Descendants(ns + "UserId").First().Value);
        Assert.Equal(12, WindowsTask.Xml(["c"], ScheduleFrequency.Monthly, new TimeOnly(3, 0), DayOfWeek.Monday, "u", DateTime.Now)
            .Split("<Months>")[1].Split("</Months>")[0].Split(" />").Length - 1);
    }

    private static void XNamespaceTest(System.Xml.Linq.XDocument doc, out System.Xml.Linq.XNamespace ns) => ns = doc.Root!.Name.Namespace;
}

/// <summary>Real trash moves (Linux only). BYTEATLAS_OTHER_FS names a folder on another file system (a tmpfs) for top-dir trashes.</summary>
[Collection("environment")]
public class LinuxTrashTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "ba-trash-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void HomeTrashKeepsRestoreInfo()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var root = NewDir();
        var old = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "data"));
        try
        {
            var a = Path.Combine(root, "a b.txt");
            File.WriteAllText(a, "one");
            var dir = Path.Combine(root, "folder");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "f"), "x");
            var r = FileOperations.Delete([a, dir], recycle: true);
            Assert.Equal((2, 0), (r.Done, r.Failed));
            Assert.False(File.Exists(a));
            Assert.False(Directory.Exists(dir));

            var trash = Path.Combine(root, "data", "Trash");
            Assert.Equal("one", File.ReadAllText(Path.Combine(trash, "files", "a b.txt")));
            Assert.True(File.Exists(Path.Combine(trash, "files", "folder", "sub", "f")));
            var info = File.ReadAllText(Path.Combine(trash, "info", "a b.txt.trashinfo"));
            Assert.StartsWith("[Trash Info]\nPath=" + Trash.Escape(a) + "\nDeletionDate=", info, StringComparison.Ordinal);

            // same name again: a second, distinct entry
            File.WriteAllText(a, "two");
            Assert.Equal(1, FileOperations.Delete([a], true).Done);
            Assert.Equal("two", File.ReadAllText(Path.Combine(trash, "files", "a b.2.txt")));
            Assert.True(File.Exists(Path.Combine(trash, "info", "a b.2.txt.trashinfo")));

            Assert.Equal(3, Trash.HomeTrashSize().Items);
            Assert.Equal(3, Trash.EmptyHome().Done);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(trash, "files")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(trash, "info")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", old);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MissingFileIsReportedNotDeleted()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var r = FileOperations.Delete([Path.Combine(Path.GetTempPath(), "ba-does-not-exist-" + Guid.NewGuid())], true);
        Assert.Equal((0, 1), (r.Done, r.Failed));
    }

    [Fact]
    public void OtherFileSystemUsesItsOwnTrash()
    {
        var other = Environment.GetEnvironmentVariable("BYTEATLAS_OTHER_FS");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(other))
            return;
        var f = Path.Combine(other, "ba-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        File.WriteAllBytes(f, new byte[100]);
        Assert.Equal(1, FileOperations.Delete([f], true).Done);
        var uid = Native.UnixStat.CurrentUid;
        var trash = Path.Combine(other, ".Trash-" + uid);
        Assert.True(File.Exists(Path.Combine(trash, "files", Path.GetFileName(f))));
        // top-dir trash: the path is relative to the top directory
        var info = File.ReadAllText(Path.Combine(trash, "info", Path.GetFileName(f) + ".trashinfo"));
        Assert.Contains("\nPath=" + Path.GetFileName(f) + "\n", info, StringComparison.Ordinal);
    }
}

/// <summary>Creates, lists and removes real schedules (Linux; only with BYTEATLAS_SCHEDULER_TESTS=1: it changes the user's crontab / timers).</summary>
[Collection("environment")]
public class LinuxSchedulerTests
{
    [Fact]
    public void CreateListDelete()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("BYTEATLAS_SCHEDULER_TESTS") != "1")
            return;
        var backend = UnixScheduler.Backend;
        Assert.NotNull(backend);
        var name = "ba-test-" + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(backend, UnixScheduler.Create(name, ["/bin/true", "scan", "/tmp/a b"], ScheduleFrequency.Daily, new TimeOnly(4, 15), DayOfWeek.Monday));
        var t = Assert.Single(UnixScheduler.List(), x => x.Name == name);
        Assert.Contains("/tmp/a b", t.Command, StringComparison.Ordinal);
        // creating again replaces it
        UnixScheduler.Create(name, ["/bin/true"], ScheduleFrequency.Weekly, new TimeOnly(5, 0), DayOfWeek.Friday);
        Assert.Single(UnixScheduler.List(), x => x.Name == name);
        Assert.True(UnixScheduler.Delete(name));
        Assert.DoesNotContain(UnixScheduler.List(), x => x.Name == name);
        Assert.False(UnixScheduler.Delete(name));
    }
}
