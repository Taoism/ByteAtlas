using System.IO.Compression;
using ByteAtlas.Native;

namespace ByteAtlas.Operations;

public sealed record OperationResult(int Done, int Failed, List<string> Errors)
{
    public static OperationResult Empty => new(0, 0, []);
}

public enum CollisionMode
{
    Skip,
    Overwrite,
    Rename,
}

/// <summary>Delete, move, copy, archive and compress files in bulk.</summary>
public static class FileOperations
{
    /// <summary>Deletes files and folders (to the recycle bin when <paramref name="recycle"/> is set).</summary>
    public static OperationResult Delete(IReadOnlyList<string> paths, bool recycle)
    {
        if (paths.Count == 0)
            return OperationResult.Empty;
        if (OperatingSystem.IsWindows() && recycle)
        {
            var ok = Win32.ShellDelete(paths, true);
            var left = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            return new OperationResult(paths.Count - left.Count, left.Count, ok ? [] : [.. left.Select(p => "Could not delete " + p)]);
        }
        var errors = new List<string>();
        var done = 0;
        if (recycle)
        {
            // never fall back to deleting for good: what cannot go to the trash stays where it is
            foreach (var p in paths)
            {
                try
                {
                    if (!OperatingSystem.IsLinux())
                        throw new PlatformNotSupportedException("There is no trash on this system.");
                    Trash.Move(p);
                    done++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    errors.Add($"{p}: {e.Message}");
                }
            }
            return new OperationResult(done, errors.Count, errors);
        }
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                    Directory.Delete(p, true);
                else
                    File.Delete(p);
                done++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{p}: {e.Message}");
            }
        }
        return new OperationResult(done, errors.Count, errors);
    }

    /// <summary>
    /// Moves or copies files into <paramref name="target"/>. With <paramref name="keepStructure"/> the folder path
    /// below <paramref name="baseFolder"/> is recreated in the target.
    /// </summary>
    public static OperationResult MoveOrCopy(IReadOnlyList<string> files, string target, bool move, bool keepStructure, string? baseFolder, CollisionMode collision, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var done = 0;
        Directory.CreateDirectory(target);
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var src = files[i];
            try
            {
                var dest = TargetPath(src, target, keepStructure, baseFolder);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest) || Directory.Exists(dest))
                {
                    if (collision == CollisionMode.Skip)
                        continue;
                    if (collision == CollisionMode.Rename)
                        dest = UniqueName(dest);
                }
                if (Directory.Exists(src))
                {
                    if (move)
                        Directory.Move(src, dest);
                    else
                        CopyDirectory(src, dest, collision == CollisionMode.Overwrite);
                }
                else if (move)
                    File.Move(src, dest, collision == CollisionMode.Overwrite);
                else
                    File.Copy(src, dest, collision == CollisionMode.Overwrite);
                done++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{src}: {e.Message}");
            }
            progress?.Report(i + 1);
        }
        return new OperationResult(done, errors.Count, errors);
    }

    /// <summary>Packs files into a zip archive, optionally deleting the originals afterwards.</summary>
    public static OperationResult Archive(IReadOnlyList<string> files, string zipPath, string? baseFolder, bool deleteOriginals, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var added = new List<string>();
        using (var zip = ZipFile.Open(zipPath, File.Exists(zipPath) ? ZipArchiveMode.Update : ZipArchiveMode.Create))
        {
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var entry = baseFolder != null && f.StartsWith(baseFolder, StringComparison.OrdinalIgnoreCase)
                        ? f[baseFolder.Length..].TrimStart('\\', '/')
                        : Path.GetFileName(f);
                    zip.CreateEntryFromFile(f, entry.Replace('\\', '/'), CompressionLevel.Optimal);
                    added.Add(f);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{f}: {e.Message}");
                }
            }
        }
        if (deleteOriginals)
            foreach (var f in added)
            {
                try
                {
                    File.Delete(f);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{f}: archived, but not deleted: {e.Message}");
                }
            }
        return new OperationResult(added.Count, errors.Count, errors);
    }

    /// <summary>
    /// Compresses files and folders (folders: everything below) with <paramref name="format"/>: NTFS (LZNT1), a compact
    /// format (XPRESS 4K/8K/16K, LZX — files only, folders just hold them) or None to uncompress both kinds.
    /// </summary>
    public static OperationResult Compress(IReadOnlyList<string> paths, Native.CompressionFormat format, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return new OperationResult(0, paths.Count, ["Compression is only available on Windows."]);
        var errors = new List<string>();
        var done = 0;
        foreach (var p in paths)
        {
            var targets = Directory.Exists(p)
                ? Directory.EnumerateFileSystemEntries(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Prepend(p)
                : [p];
            foreach (var t in targets)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(t);
                var isDir = Directory.Exists(t);
                string? err = null;
                var ok = format switch
                {
                    Native.CompressionFormat.None => (isDir || Native.Compression.RemoveCompact(t, out err)) && Win32.SetCompression(t, false, out err),
                    Native.CompressionFormat.Ntfs => (isDir || Native.Compression.RemoveCompact(t, out err)) && Win32.SetCompression(t, true, out err),
                    // compact compression is per file; LZNT1 has to go first
                    _ => isDir || (Win32.SetCompression(t, false, out err) && Native.Compression.SetCompact(t, format, out err)),
                };
                if (ok)
                    done++;
                else
                    errors.Add($"{t}: {err}");
            }
        }
        return new OperationResult(done, errors.Count, errors);
    }

    /// <summary>NTFS compression on/off for files and folders (folders: everything below too).</summary>
    public static OperationResult SetCompression(IReadOnlyList<string> paths, bool compress, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return new OperationResult(0, paths.Count, ["NTFS compression is only available on Windows."]);
        var errors = new List<string>();
        var done = 0;
        foreach (var p in paths)
        {
            var targets = Directory.Exists(p)
                ? Directory.EnumerateFileSystemEntries(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Prepend(p)
                : [p];
            foreach (var t in targets)
            {
                ct.ThrowIfCancellationRequested();
                if (Win32.SetCompression(t, compress, out var err))
                    done++;
                else
                    errors.Add($"{t}: {err}");
            }
        }
        return new OperationResult(done, errors.Count, errors);
    }

    /// <summary>
    /// Replaces each duplicate with a hard link to <paramref name="keep"/> (same volume only). The duplicate is
    /// renamed first and only deleted once the link exists, so nothing is lost on failure.
    /// </summary>
    public static OperationResult ReplaceWithHardlinks(string keep, IReadOnlyList<string> duplicates)
    {
        if (!OperatingSystem.IsWindows())
            return new OperationResult(0, duplicates.Count, ["Hard links are only created on Windows."]);
        var errors = new List<string>();
        var done = 0;
        var keepRoot = Path.GetPathRoot(Path.GetFullPath(keep));
        foreach (var d in duplicates)
        {
            if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(d)), keepRoot, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{d}: on another drive than {keep}");
                continue;
            }
            var tmp = d + ".byteatlas-dedup";
            try
            {
                File.Move(d, tmp);
                if (!Win32.CreateHardLink(d, keep, IntPtr.Zero))
                {
                    var err = new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message;
                    File.Move(tmp, d);
                    errors.Add($"{d}: {err}");
                    continue;
                }
                File.Delete(tmp);
                done++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(tmp) && !File.Exists(d))
                    File.Move(tmp, d);
                errors.Add($"{d}: {e.Message}");
            }
        }
        return new OperationResult(done, errors.Count, errors);
    }

    private static string TargetPath(string src, string target, bool keepStructure, string? baseFolder)
    {
        if (keepStructure && baseFolder != null && src.StartsWith(baseFolder, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(target, src[baseFolder.Length..].TrimStart('\\', '/'));
        if (keepStructure)
        {
            // keep the full path without the drive: D:\a\b.txt -> target\D\a\b.txt
            var root = Path.GetPathRoot(src) ?? "";
            var rel = src[root.Length..];
            var drive = root.TrimEnd('\\', '/', ':').Replace("\\", "").Replace(":", "");
            return Path.Combine(target, drive, rel);
        }
        return Path.Combine(target, Path.GetFileName(src));
    }

    public static string UniqueName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p))
                return p;
        }
    }

    private static void CopyDirectory(string src, string dest, bool overwrite)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)), overwrite);
    }
}
