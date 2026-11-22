using System.Text.Json.Serialization;

namespace ByteAtlas.Remote;

public enum RemoteKind
{
    Ssh,
    S3,
    AzureBlob,
    WebDav,
    DiskImage,
}

/// <summary>Where a remote scan reads from (SSH server, S3 bucket, Azure container, WebDAV folder, disk image).</summary>
public sealed class RemoteSpec
{
    public RemoteKind Kind { get; set; }

    // SSH / WebDAV
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string User { get; set; } = "";

    /// <summary>Password (SSH, WebDAV, S3 secret key). Not saved unless the user asks; then protected (DPAPI).</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";
    public string? PasswordProtected { get; set; }
    public string KeyFile { get; set; } = "";
    public bool StayOnFileSystem { get; set; } = true;

    /// <summary>Folder / prefix to scan.</summary>
    public string Path { get; set; } = "";

    // S3
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public bool PathStyle { get; set; }

    // Azure Blob (container URL with SAS) / WebDAV URL / disk image file
    public string Url { get; set; } = "";

    [JsonIgnore]
    public string Display => Kind switch
    {
        RemoteKind.Ssh => $"ssh://{(User.Length > 0 ? User + "@" : "")}{Host}{(Port is > 0 and not 22 ? ":" + Port : "")}{(Path.StartsWith('/') ? Path : "/" + Path)}",
        RemoteKind.S3 => Endpoint.Length > 0
            ? $"s3+{EndpointUri.Scheme}://{EndpointUri.Authority}/{Bucket}{S3Prefix}"
            : $"s3://{Bucket}{S3Prefix}",
        RemoteKind.AzureBlob => AzureDisplay(Url),
        RemoteKind.WebDav => Url,
        _ => Url,
    };

    private string S3Prefix => Path.Trim('/') is { Length: > 0 } p ? "/" + p : "";

    private Uri EndpointUri => new(Endpoint.Contains("://", StringComparison.Ordinal) ? Endpoint : "https://" + Endpoint);

    private static string AzureDisplay(string url)
    {
        // never show the SAS token
        var q = url.IndexOf('?');
        return "azure:" + (q < 0 ? url : url[..q]);
    }

    /// <summary>Disk image types that can be mounted read-only and scanned (Windows).</summary>
    public static readonly string[] ImageExtensions = [".vhdx", ".vhd", ".iso"];

    public RemoteSpec Clone() => (RemoteSpec)MemberwiseClone();

    /// <summary>True when a scan would need a password / secret that is not filled in.</summary>
    [JsonIgnore]
    public bool NeedsSecret => Kind switch
    {
        RemoteKind.Ssh => Password.Length == 0 && KeyFile.Length == 0,
        RemoteKind.S3 => Password.Length == 0 || AccessKey.Length == 0,
        RemoteKind.AzureBlob => !Url.Contains('?'),
        RemoteKind.WebDav => User.Length > 0 && Password.Length == 0,
        _ => false,
    };

    /// <summary>
    /// A copy fit for a settings file: the password (for Azure the SAS token of the URL) is left out, or kept
    /// encrypted for the current Windows user when <paramref name="remember"/> is set.
    /// </summary>
    public RemoteSpec ForSaving(bool remember)
    {
        var c = Clone();
        var secret = Password;
        if (Kind == RemoteKind.AzureBlob && Url.IndexOf('?') is var q and >= 0)
        {
            secret = Url[(q + 1)..];
            c.Url = Url[..q];
        }
        c.Password = "";
        c.PasswordProtected = remember && secret.Length > 0 && OperatingSystem.IsWindows()
            ? Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(secret), null, System.Security.Cryptography.DataProtectionScope.CurrentUser))
            : null;
        return c;
    }

    /// <summary>A working copy of a saved source with its remembered secret decrypted (when it can be).</summary>
    public RemoteSpec Restored()
    {
        var c = Clone();
        if (PasswordProtected == null || !OperatingSystem.IsWindows())
            return c;
        try
        {
            var secret = System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(PasswordProtected), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            if (Kind == RemoteKind.AzureBlob)
                c.Url = Url + "?" + secret;
            else
                c.Password = secret;
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
        {
            // saved by another Windows user or machine: ask again
        }
        return c;
    }

    /// <summary>
    /// Reads a source address: ssh://user@host:port/path, s3://bucket/prefix, azure:https://account.blob…/container?SAS
    /// (or any *.blob.core.windows.net URL), dav://host/path or davs://host/path (WebDAV over http / https),
    /// webdav:https://…, or a .vhdx / .vhd / .iso file. Returns null for a normal local path.
    /// </summary>
    public static RemoteSpec? Parse(string text)
    {
        text = text.Trim();
        var colon = text.IndexOf(':');
        var scheme = colon > 1 ? text[..colon].ToLowerInvariant() : "";
        switch (scheme)
        {
            case "ssh" or "sftp":
                return ParseSsh(text);
            case "s3":
            {
                var rest = text[(colon + 1)..].TrimStart('/');
                var slash = rest.IndexOf('/');
                return new RemoteSpec
                {
                    Kind = RemoteKind.S3,
                    Bucket = slash < 0 ? rest : rest[..slash],
                    Path = slash < 0 ? "" : rest[(slash + 1)..],
                };
            }
            case "s3+http" or "s3+https":
            {
                // S3-compatible endpoint: s3+https://endpoint/bucket/prefix
                var u = new Uri(text[3..]);
                var rest = Uri.UnescapeDataString(u.AbsolutePath).TrimStart('/');
                var slash = rest.IndexOf('/');
                return new RemoteSpec
                {
                    Kind = RemoteKind.S3,
                    Endpoint = $"{u.Scheme}://{u.Authority}",
                    PathStyle = true,
                    Bucket = slash < 0 ? rest : rest[..slash],
                    Path = slash < 0 ? "" : rest[(slash + 1)..],
                };
            }
            case "azure":
                return new RemoteSpec { Kind = RemoteKind.AzureBlob, Url = text[(colon + 1)..] };
            case "webdav":
                return WebDav(text[(colon + 1)..]);
            case "dav" or "davs":
                return WebDav((scheme == "davs" ? "https" : "http") + text[colon..]);
            case "http" or "https":
                return Uri.TryCreate(text, UriKind.Absolute, out var h) && h.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase)
                    ? new RemoteSpec { Kind = RemoteKind.AzureBlob, Url = text }
                    : WebDav(text);
            default:
                return ImageExtensions.Any(e => text.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                    ? new RemoteSpec { Kind = RemoteKind.DiskImage, Url = text }
                    : null;
        }
    }

    /// <summary>WebDAV address; user:password@ in it goes to the User / Password fields (never kept in the URL).</summary>
    private static RemoteSpec WebDav(string url)
    {
        var u = new Uri(url);
        var info = Uri.UnescapeDataString(u.UserInfo);
        var cut = info.IndexOf(':');
        return new RemoteSpec
        {
            Kind = RemoteKind.WebDav,
            Url = info.Length == 0 ? url : new UriBuilder(u) { UserName = "", Password = "" }.Uri.ToString(),
            User = cut < 0 ? info : info[..cut],
            Password = cut < 0 ? "" : info[(cut + 1)..],
        };
    }

    private static RemoteSpec ParseSsh(string text)
    {
        var u = new Uri(text);
        var user = Uri.UnescapeDataString(u.UserInfo);
        var cut = user.IndexOf(':');
        var path = Uri.UnescapeDataString(u.AbsolutePath);
        if (path.StartsWith("/~", StringComparison.Ordinal))
            path = path[1..]; // ssh://host/~/x = home folder
        return new RemoteSpec
        {
            Kind = RemoteKind.Ssh,
            Host = u.Host,
            Port = u.Port > 0 ? u.Port : 22,
            User = cut < 0 ? user : user[..cut],
            Password = cut < 0 ? "" : user[(cut + 1)..],
            Path = path,
        };
    }
}
