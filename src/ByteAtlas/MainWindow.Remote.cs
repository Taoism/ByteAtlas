using ByteAtlas.Dialogs;
using ByteAtlas.Remote;
using ByteAtlas.Scanning;

namespace ByteAtlas;

/// <summary>Scans of SSH servers, S3 buckets, Azure containers, WebDAV folders and disk images.</summary>
public partial class MainWindow
{
    /// <summary>Scans of mounted disk images → image file (dismounted when the last of its scans is removed).</summary>
    private readonly Dictionary<ScanSession, string> _mountedImages = [];

    private async Task ScanRemoteDialogAsync(RemoteSpec? start = null)
    {
        var spec = await RemoteDialog.AskAsync(this, _settings, start);
        if (spec != null)
            await StartRemoteAsync(spec);
    }

    /// <summary>A typed or recent address (ssh://, s3://, azure:, dav://, .vhdx …); false for a normal path.</summary>
    private bool TryStartRemote(string path)
    {
        var saved = _settings.Remotes.FirstOrDefault(r => string.Equals(ScanSession.NormalizeRoot(r.Spec.Display), ScanSession.NormalizeRoot(path), StringComparison.Ordinal));
        RemoteSpec? spec;
        try
        {
            spec = saved?.Spec.Restored() ?? RemoteSpec.Parse(path);
        }
        catch (UriFormatException e)
        {
            _ = MessageBox.ShowAsync(this, $"\"{path}\" is not a valid address: {e.Message}", MessageKind.Warning, "ByteAtlas");
            return true;
        }
        if (spec == null)
            return false;
        // missing password / key / SAS: ask, with what was typed already filled in
        _ = Report(spec.NeedsSecret ? ScanRemoteDialogAsync(spec) : StartRemoteAsync(spec));
        return true;
    }

    /// <summary>Runs a started task to its end and shows what went wrong instead of losing it.</summary>
    private async Task Report(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await MessageBox.ShowAsync(this, e.GetType().Name + ": " + e.Message, MessageKind.Error, "ByteAtlas");
        }
    }

    private async Task StartRemoteAsync(RemoteSpec spec)
    {
        if (spec.Kind == RemoteKind.DiskImage)
        {
            await MountAndScanAsync(spec.Url);
            return;
        }
        var path = ScanSession.NormalizeRoot(spec.Display);
        var existing = _tree.Scans.FirstOrDefault(s => s.Remote != null && s.Path == path);
        if (existing != null)
        {
            _tree.RemoveScan(existing);
            UpdateGroup();
        }
        _settings.AddRecent(path);
        RefreshRecentMenu();
        var session = new ScanSession(spec.Display, _settings.Scan.Clone()) { Remote = spec, Title = existing?.Title };
        _tree.AddScan(session);
        UpdateGroup();
        _tree.Rebuild();
        _pathBox.Text = session.Path;
        Run(session, Scanner.ScanAsync);
    }

    private async Task MountAndScanAsync(string image)
    {
        if (!OperatingSystem.IsWindows())
        {
            await MessageBox.ShowAsync(this, "Disk images can only be mounted on Windows.", MessageKind.Warning, "ByteAtlas");
            return;
        }
        var open = _mountedImages.FirstOrDefault(kv => string.Equals(kv.Value, image, StringComparison.OrdinalIgnoreCase)).Key;
        if (open != null)
        {
            _tree.Select(open.Root);
            Rescan(open);
            return;
        }
        SetStatus("Mounting " + image + " read-only…");
        List<string> volumes;
        try
        {
            volumes = await Task.Run(() => OperatingSystem.IsWindows() ? DiskImages.Mount(image) : [], CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SetStatus("");
            var hint = Shell.IsAdmin ? "" : "\n\nMounting needs administrator rights (Scan › Start as administrator).";
            await MessageBox.ShowAsync(this, $"Cannot mount {Path.GetFileName(image)}: {e.Message}{hint}", MessageKind.Warning, "ByteAtlas");
            return;
        }
        _settings.AddRecent(image);
        RefreshRecentMenu();
        for (var i = 0; i < volumes.Count; i++)
        {
            var session = new ScanSession(volumes[i], _settings.Scan.Clone())
            {
                Title = Path.GetFileName(image) + (volumes.Count > 1 ? $" ({i + 1})" : ""),
            };
            _mountedImages[session] = image;
            _tree.AddScan(session);
            Run(session, Scanner.ScanAsync);
        }
        UpdateGroup();
        _tree.Rebuild();
    }

    /// <summary>Dismounts the image of a removed scan once no other scan uses it.</summary>
    private void ReleaseImage(ScanSession s)
    {
        if (!OperatingSystem.IsWindows() || !_mountedImages.Remove(s, out var image) || _mountedImages.ContainsValue(image))
            return;
        _ = Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
                DiskImages.Dismount(image);
        }, CancellationToken.None);
    }

    private void DismountAllImages()
    {
        if (!OperatingSystem.IsWindows())
            return;
        foreach (var image in _mountedImages.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            DiskImages.Dismount(image);
        _mountedImages.Clear();
    }
}
