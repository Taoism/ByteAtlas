using System.Management;
using System.Runtime.Versioning;

namespace ByteAtlas.Storage;

public sealed record ShadowCopy(string Id, string Volume, string DeviceObject, DateTime Created, string VolumeName)
{
    /// <summary>The same path inside the snapshot, ready to scan.</summary>
    public string MapPath(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        return DeviceObject.TrimEnd('\\') + "\\" + path[root.Length..];
    }

    public override string ToString() => $"{Created:g}  {VolumeName}";
}

/// <summary>Windows volume shadow copies (snapshots): list, create (administrator), map paths.</summary>
[SupportedOSPlatform("windows")]
public static class ShadowCopies
{
    public static List<ShadowCopy> List(string? forPath = null)
    {
        var volumes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var vs = new ManagementObjectSearcher("SELECT DeviceID, DriveLetter, Name FROM Win32_Volume"))
            foreach (ManagementObject v in vs.Get())
                if (v["DeviceID"] is string id)
                    volumes[id] = (v["DriveLetter"] as string ?? v["Name"] as string ?? id).TrimEnd('\\') + "\\";

        var list = new List<ShadowCopy>();
        using var s = new ManagementObjectSearcher("SELECT ID, VolumeName, DeviceObject, InstallDate FROM Win32_ShadowCopy");
        foreach (ManagementObject o in s.Get())
        {
            var vol = o["VolumeName"] as string ?? "";
            var name = volumes.GetValueOrDefault(vol, vol);
            var created = o["InstallDate"] is string d ? ManagementDateTimeConverter.ToDateTime(d) : DateTime.MinValue;
            list.Add(new ShadowCopy(o["ID"] as string ?? "", vol, o["DeviceObject"] as string ?? "", created, name));
        }
        if (forPath != null)
        {
            var root = (Path.GetPathRoot(forPath) ?? "").TrimEnd('\\') + "\\";
            list = [.. list.Where(c => string.Equals(c.VolumeName, root, StringComparison.OrdinalIgnoreCase))];
        }
        list.Sort((a, b) => b.Created.CompareTo(a.Created));
        return list;
    }

    /// <summary>Creates a snapshot of the volume holding <paramref name="path"/>; returns its id.</summary>
    public static string Create(string path)
    {
        var root = (Path.GetPathRoot(path) ?? path).TrimEnd('\\') + "\\";
        using var cls = new ManagementClass("Win32_ShadowCopy");
        using var inParams = cls.GetMethodParameters("Create");
        inParams["Volume"] = root;
        inParams["Context"] = "ClientAccessible";
        using var outParams = cls.InvokeMethod("Create", inParams, null);
        var rc = Convert.ToUInt32(outParams["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
        if (rc != 0)
            throw new IOException(rc switch
            {
                1 => "Access denied (administrator rights needed).",
                2 => "Invalid argument.",
                3 => "The volume was not found.",
                4 => "The volume does not support snapshots.",
                5 => "Unsupported snapshot context.",
                6 => "Not enough storage for the snapshot.",
                7 => "The volume is in use by another snapshot operation.",
                8 => "Maximum number of snapshots reached.",
                9 => "Another snapshot operation is running.",
                _ => $"Snapshot failed (code {rc}).",
            });
        return outParams["ShadowID"] as string ?? "";
    }
}
