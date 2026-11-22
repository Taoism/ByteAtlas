using System.Globalization;
using System.Text;

namespace ByteAtlas.Storage;

/// <summary>
/// A plain text log per day (scans with their timings, errors, crashes) in the "logs" folder next to the history,
/// kept for <see cref="KeepDays"/> days. Paths of scanned folders are written, file contents never.
/// </summary>
public static class AppLog
{
    public static int KeepDays { get; set; } = 14;

    private static readonly Lock Gate = new();
    private static bool _trimmed;

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ByteAtlas", "logs");

    public static string CurrentFile => Path.Combine(Folder, $"byteatlas-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? e = null) => Write("ERROR", e == null ? message : $"{message}: {e}");

    private static void Write(string level, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} {level} {message.Replace("\r\n", "\n").Replace("\n", "\n    ")}\n");
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
                if (!_trimmed)
                {
                    _trimmed = true;
                    Trim();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // no log rather than no app
            }
        }
    }

    private static void Trim()
    {
        foreach (var f in new DirectoryInfo(Folder).GetFiles("byteatlas-*.log"))
        {
            if (DateTime.Now - f.LastWriteTime > TimeSpan.FromDays(KeepDays))
                f.Delete();
        }
    }
}
