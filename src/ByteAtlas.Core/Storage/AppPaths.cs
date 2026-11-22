namespace ByteAtlas.Storage;

/// <summary>
/// Where ByteAtlas keeps its data. Normally the user's profile (settings roaming, history / checksums / cache /
/// logs local). Portable mode: a file "portable.txt" next to the program puts everything in a "data" folder next
/// to it (a USB stick, a tools folder), as long as that folder can be written.
/// </summary>
public static class AppPaths
{
    public const string MarkerName = "portable.txt";
    public const string DataName = "data";

    private static readonly Lazy<string?> PortableFolder = new(FindPortable);

    /// <summary>The data folder of a portable copy, or null when installed normally.</summary>
    public static string? Portable => Override ?? PortableFolder.Value;

    /// <summary>Tests: a folder to use instead of the profile (null: normal behaviour).</summary>
    public static string? Override { get; set; }

    public static bool IsPortable => Portable != null;

    /// <summary>Settings, saved sources, known SSH hosts.</summary>
    public static string Roaming => Portable ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ByteAtlas");

    /// <summary>History, checksums, scan cache, logs.</summary>
    public static string Local => Portable ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ByteAtlas");

    private static string? FindPortable()
    {
        var dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, MarkerName)))
            return null;
        var data = Path.Combine(dir, DataName);
        try
        {
            Directory.CreateDirectory(data);
            // read-only places (a CD, Program Files without rights): normal folders instead
            var probe = Path.Combine(data, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return data;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes a portable copy of the program in <paramref name="target"/>: every file next to the running program,
    /// the marker, and (when asked) the current settings and history, so the copy starts as this one is set up.
    /// </summary>
    public static int CreatePortableCopy(string target, bool withData)
    {
        var source = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(target);
        if (full.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full.Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a folder outside the program folder.");
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, f);
            // not this copy's own data
            if (rel.StartsWith(DataName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            var to = Path.Combine(full, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, true);
            n++;
        }
        File.WriteAllText(Path.Combine(full, MarkerName),
            "ByteAtlas portable: settings, history, checksums, cache and logs are kept in the \"data\" folder next to this file.\n" +
            "Delete this file to use the normal folders of your user account again.\n");
        var data = Path.Combine(full, DataName);
        Directory.CreateDirectory(data);
        if (withData)
        {
            foreach (var (from, name) in new[] { (Roaming, "settings.json"), (Roaming, "ssh-known-hosts.txt"), (Local, "history.db"), (Local, "checksums.db") })
            {
                var f = Path.Combine(from, name);
                if (File.Exists(f))
                    File.Copy(f, Path.Combine(data, name), true);
            }
            // a history file chosen somewhere on this computer: the copy keeps its own, in its data folder
            var settings = Path.Combine(data, "settings.json");
            if (File.Exists(settings) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings)) is System.Text.Json.Nodes.JsonObject o && o.ContainsKey("HistoryFile"))
            {
                o["HistoryFile"] = "";
                File.WriteAllText(settings, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        }
        return n;
    }
}
