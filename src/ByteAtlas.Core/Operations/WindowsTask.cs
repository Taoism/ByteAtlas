using System.Globalization;
using System.Security;
using System.Text;

namespace ByteAtlas.Operations;

/// <summary>
/// Task Scheduler definitions for scheduled scans (registered with schtasks /Create /XML). Program and arguments are
/// separate fields there, so long paths fit (schtasks /TR stops at 261 characters).
/// </summary>
public static class WindowsTask
{
    /// <summary>One argument quoted for a Windows command line (CommandLineToArgvW rules).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            return arg;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            // backslashes before a quote are doubled, the quote escaped
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        // backslashes before the closing quote are doubled
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static string CommandLine(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    /// <summary>
    /// The task XML: runs <paramref name="command"/> (program + arguments) as <paramref name="user"/> while logged on,
    /// without elevation; a missed run starts as soon as possible.
    /// </summary>
    public static string Xml(IReadOnlyList<string> command, ScheduleFrequency f, TimeOnly at, DayOfWeek day, string user, DateTime now)
    {
        var start = now.Date + at.ToTimeSpan();
        if (start <= now)
            start = start.AddDays(1);
        var boundary = start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        static string X(string s) => SecurityElement.Escape(s);
        var trigger = f switch
        {
            ScheduleFrequency.Daily => $"<CalendarTrigger><StartBoundary>{boundary}</StartBoundary><Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>",
            ScheduleFrequency.Weekly => $"<CalendarTrigger><StartBoundary>{boundary}</StartBoundary><Enabled>true</Enabled><ScheduleByWeek><DaysOfWeek><{day} /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek></CalendarTrigger>",
            ScheduleFrequency.Monthly => $"<CalendarTrigger><StartBoundary>{boundary}</StartBoundary><Enabled>true</Enabled><ScheduleByMonth><DaysOfMonth><Day>1</Day></DaysOfMonth><Months>{string.Concat(CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Where(m => m.Length > 0).Select(m => $"<{m} />"))}</Months></ScheduleByMonth></CalendarTrigger>",
            _ => $"<LogonTrigger><Enabled>true</Enabled><UserId>{X(user)}</UserId></LogonTrigger>",
        };
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>ByteAtlas scheduled scan</Description>
                <Author>{X(user)}</Author>
              </RegistrationInfo>
              <Triggers>
                {trigger}
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{X(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT12H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{X(command[0])}</Command>
                  <Arguments>{X(CommandLine(command.Skip(1)))}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
