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
        RemoteKind.S3 => $"s3://{Bucket}/{Path.TrimStart('/')}" + (Endpoint.Length > 0 ? $" ({new Uri(Endpoint.Contains("://") ? Endpoint : "https://" + Endpoint).Host})" : ""),
        RemoteKind.AzureBlob => AzureDisplay(Url),
        RemoteKind.WebDav => Url,
        _ => Url,
    };

    private static string AzureDisplay(string url)
    {
        // never show the SAS token
        var q = url.IndexOf('?');
        return "azure:" + (q < 0 ? url : url[..q]);
    }

    public RemoteSpec Clone() => (RemoteSpec)MemberwiseClone();
}
