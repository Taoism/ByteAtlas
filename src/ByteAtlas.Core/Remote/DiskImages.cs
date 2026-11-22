using System.Diagnostics;
using System.Runtime.Versioning;

namespace ByteAtlas.Remote;

/// <summary>Mounts disk images (VHDX, VHD, ISO) read-only so their volumes can be scanned like drives.</summary>
[SupportedOSPlatform("windows")]
public static class DiskImages
{
    public static readonly string[] Extensions = [".vhdx", ".vhd", ".iso"];

    private static string Ps(string script)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new IOException(error.Trim().Length > 0 ? error.Trim() : "PowerShell failed.");
        return output;
    }

    private static string Lit(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Mounts the image read-only and returns the paths of its volumes (drive letters or \\?\Volume{…}\).</summary>
    public static List<string> Mount(string image)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $img = Mount-DiskImage -ImagePath {{Lit(image)}} -Access ReadOnly -PassThru
            Start-Sleep -Milliseconds 800
            if ($img.ImagePath -like '*.iso') { $vols = $img | Get-Volume } else { $vols = $img | Get-Disk | Get-Partition | Get-Volume }
            foreach ($v in $vols) { if ($v.DriveLetter) { "$($v.DriveLetter):\" } elseif ($v.Path) { $v.Path } }
            """;
        var lines = Ps(script).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (lines.Count == 0)
        {
            Dismount(image);
            throw new IOException("The image has no readable volume (unknown file system?).");
        }
        return lines;
    }

    public static void Dismount(string image)
    {
        try
        {
            Ps($"Dismount-DiskImage -ImagePath {Lit(image)} | Out-Null");
        }
        catch (IOException)
        {
            // already gone
        }
    }
}
