using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ByteAtlas.Scanning;
using Renci.SshNet;

namespace ByteAtlas.Remote;

/// <summary>Scans remote sources into a session: SSH (GNU find), S3 (ListObjectsV2), Azure Blob (SAS list), WebDAV (PROPFIND).</summary>
public static class RemoteSources
{
    /// <summary>Known SSH host keys (host:port → SHA-256 fingerprint); unknown keys are trusted on first use and remembered.</summary>
    public static Dictionary<string, string> KnownHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where <see cref="KnownHosts"/> is kept (shared by the app and byteatlas-cli); null = memory only.</summary>
    public static string? KnownHostsFile { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ByteAtlas", "ssh-known-hosts.txt");

    private static bool _hostsLoaded;

    private static void LoadKnownHosts()
    {
        if (_hostsLoaded || KnownHostsFile == null)
            return;
        _hostsLoaded = true;
        if (!File.Exists(KnownHostsFile))
            return;
        foreach (var line in File.ReadAllLines(KnownHostsFile))
        {
            var cut = line.IndexOf(' ');
            if (cut > 0 && !line.StartsWith('#'))
                KnownHosts[line[..cut]] = line[(cut + 1)..].Trim();
        }
    }

    private static void SaveKnownHosts()
    {
        if (KnownHostsFile == null)
            return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(KnownHostsFile)!);
        File.WriteAllLines(KnownHostsFile, KnownHosts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + " " + kv.Value));
    }

    /// <summary>Forgets a host key (after the server was reinstalled). Returns false when it was not known.</summary>
    public static bool ForgetHost(string hostAndPort)
    {
        lock (KnownHosts)
        {
            LoadKnownHosts();
            if (!KnownHosts.Remove(hostAndPort))
                return false;
            SaveKnownHosts();
            return true;
        }
    }

    public static void Scan(ScanSession s, RemoteSpec spec, CancellationToken ct)
    {
        s.Root.SetOwnDates(DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks);
        switch (spec.Kind)
        {
            case RemoteKind.Ssh:
                Ssh(s, spec, ct);
                break;
            case RemoteKind.S3:
                S3(s, spec, ct).GetAwaiter().GetResult();
                break;
            case RemoteKind.AzureBlob:
                Azure(s, spec, ct).GetAwaiter().GetResult();
                break;
            case RemoteKind.WebDav:
                WebDav(s, spec, ct).GetAwaiter().GetResult();
                break;
            default:
                throw new InvalidOperationException("Disk images are mounted and scanned as drives.");
        }
    }

    // ---------------- SSH ----------------

    private static void Ssh(ScanSession s, RemoteSpec spec, CancellationToken ct)
    {
        var port = spec.Port > 0 ? spec.Port : 22;
        var methods = new List<AuthenticationMethod>();
        if (spec.KeyFile.Length > 0)
            methods.Add(new PrivateKeyAuthenticationMethod(spec.User, OpenKey(spec.KeyFile, spec.Password)));
        if (spec.Password.Length > 0)
            methods.Add(new PasswordAuthenticationMethod(spec.User, spec.Password));
        if (methods.Count == 0)
            methods.Add(new NoneAuthenticationMethod(spec.User));
        var info = new ConnectionInfo(spec.Host, port, spec.User, [.. methods]) { Timeout = TimeSpan.FromSeconds(20) };
        using var client = new SshClient(info);
        var hostKey = $"{spec.Host}:{port}";
        var rejected = false;
        client.HostKeyReceived += (_, e) =>
        {
            var fp = e.FingerPrintSHA256;
            lock (KnownHosts)
            {
                LoadKnownHosts();
                if (KnownHosts.TryGetValue(hostKey, out var known))
                {
                    e.CanTrust = known == fp;
                    rejected = !e.CanTrust;
                }
                else
                {
                    KnownHosts[hostKey] = fp;
                    SaveKnownHosts();
                    e.CanTrust = true;
                }
            }
        };
        s.CurrentPath = "Connecting to " + hostKey;
        s.MethodUsed = $"SSH {hostKey} (find)";
        try
        {
            client.Connect();
        }
        catch (Renci.SshNet.Common.SshException) when (rejected)
        {
            throw new IOException($"The host key of {hostKey} changed since the last connection — refusing (possible man-in-the-middle). If the server was reinstalled, use \"Forget host key\" in the Remote source dialog (or byteatlas-cli scan … --forget-host-key).");
        }
        catch (Renci.SshNet.Common.SshAuthenticationException e)
        {
            throw new UnauthorizedAccessException("SSH login failed: " + e.Message);
        }
        catch (System.Net.Sockets.SocketException e)
        {
            throw new IOException($"Cannot reach {hostKey}: {e.Message}");
        }
        var path = spec.Path.Length == 0 ? "." : spec.Path;
        if (client.RunCommand($"test -d {ShellPath(path)}").ExitStatus != 0)
            throw new DirectoryNotFoundException($"{hostKey}: folder not found or not readable: {path}");
        // one pass of GNU find: type, size, blocks (512 B), mtime, atime, owner, relative path
        var cmd = $"LC_ALL=C find {ShellPath(path)} {(spec.StayOnFileSystem ? "-xdev " : "")}-mindepth 1 -printf '%y\\t%s\\t%b\\t%T@\\t%A@\\t%u\\t%P\\n' 2>/dev/null";
        using var command = client.CreateCommand(cmd);
        var running = command.BeginExecute();
        var tree = new RemoteTree(s);
        s.MethodUsed = $"SSH {hostKey} (find)";
        s.ClusterSize = 4096;
        using (var reader = new StreamReader(command.OutputStream, Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) != null || !running.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                s.PauseGate.Wait(ct);
                if (line == null)
                {
                    Thread.Sleep(20);
                    continue;
                }
                var f = line.Split('\t', 7);
                if (f.Length < 7)
                    continue;
                var mtime = Unix(f[3]);
                if (f[0] == "d")
                    tree.FolderDates(f[6], mtime);
                else if (f[0] is "f" or "l")
                {
                    long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                    long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var blocks);
                    tree.File(f[6], f[0] == "l" ? 0 : size, mtime, f[0] == "l" ? 0 : blocks * 512, Unix(f[4]), f[5]);
                }
            }
        }
        command.EndExecute(running);
        if (s.FilesDone == 0 && s.FoldersDone == 0)
        {
            var test = client.RunCommand("find --version 2>&1 | head -1");
            if (!test.Result.Contains("GNU", StringComparison.Ordinal))
                throw new IOException("The server has no GNU find (needed for -printf). On BusyBox / macOS install findutils.");
        }
        try
        {
            var df = client.RunCommand($"LC_ALL=C df -kP {ShellPath(path)} | tail -1").Result.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (df.Length >= 4)
            {
                s.VolumeTotal = long.Parse(df[1], CultureInfo.InvariantCulture) * 1024;
                s.VolumeFree = long.Parse(df[3], CultureInfo.InvariantCulture) * 1024;
            }
        }
        catch (FormatException)
        {
            // df output not understood: no drive figures
        }
        client.Disconnect();
    }

    private static PrivateKeyFile OpenKey(string file, string passphrase)
    {
        try
        {
            return passphrase.Length > 0 ? new PrivateKeyFile(file, passphrase) : new PrivateKeyFile(file);
        }
        catch (Exception e) when (e is CryptographicException or Renci.SshNet.Common.SshException or ArgumentException or InvalidOperationException)
        {
            throw new UnauthorizedAccessException(passphrase.Length == 0 && e is Renci.SshNet.Common.SshPassPhraseNullOrEmptyException
                ? $"The key {System.IO.Path.GetFileName(file)} needs its passphrase (give it as the password)."
                : $"Cannot open the key {System.IO.Path.GetFileName(file)}: wrong passphrase or unknown key format ({e.Message})");
        }
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Quotes a path for the remote shell; a leading ~ stays the home folder.</summary>
    internal static string ShellPath(string path)
    {
        if (path == "~")
            return "\"$HOME\"";
        return path.StartsWith("~/", StringComparison.Ordinal) ? "\"$HOME\"/" + Quote(path[2..]) : Quote(path);
    }

    private static DateTime Unix(string seconds) =>
        double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? DateTime.UnixEpoch.AddSeconds(v)
            : default;

    // ---------------- S3 (and S3-compatible: MinIO, Backblaze B2, Wasabi, …) ----------------

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private static async Task S3(ScanSession s, RemoteSpec spec, CancellationToken ct)
    {
        var region = spec.Region.Length > 0 ? spec.Region : "us-east-1";
        var endpoint = spec.Endpoint.Length > 0
            ? new Uri(spec.Endpoint.Contains("://") ? spec.Endpoint : "https://" + spec.Endpoint)
            : new Uri($"https://s3.{region}.amazonaws.com");
        var pathStyle = spec.PathStyle || spec.Endpoint.Length > 0;
        var host = pathStyle ? endpoint.Authority : $"{spec.Bucket}.{endpoint.Authority}";
        var basePath = pathStyle ? "/" + spec.Bucket : "";
        var prefix = spec.Path.TrimStart('/');
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
            prefix += "/";
        var tree = new RemoteTree(s);
        s.MethodUsed = $"S3 {spec.Bucket}";
        string? token = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            s.PauseGate.Wait(ct);
            var q = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["list-type"] = "2", ["max-keys"] = "1000" };
            if (prefix.Length > 0)
                q["prefix"] = prefix;
            if (token != null)
                q["continuation-token"] = token;
            var query = string.Join('&', q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
            var uri = new Uri($"{endpoint.Scheme}://{host}{basePath}/?{query}");
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            SignV4(req, host, basePath + "/", query, region, spec.AccessKey, spec.Password);
            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new IOException($"S3: {(int)resp.StatusCode} {resp.ReasonPhrase}: {ErrorText(body)}");
            var xml = XDocument.Parse(body);
            var ns = xml.Root!.Name.Namespace;
            foreach (var c in xml.Root.Elements(ns + "Contents"))
            {
                var key = c.Element(ns + "Key")!.Value;
                var rel = key.Length >= prefix.Length ? key[prefix.Length..] : key;
                if (rel.Length == 0)
                    continue;
                var size = long.Parse(c.Element(ns + "Size")?.Value ?? "0", CultureInfo.InvariantCulture);
                var mod = DateTime.TryParse(c.Element(ns + "LastModified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var m) ? m : default;
                if (rel.EndsWith('/'))
                    tree.FolderDates(rel, mod);
                else
                    tree.File(rel, size, mod, size, default, c.Element(ns + "Owner")?.Element(ns + "DisplayName")?.Value);
            }
            token = xml.Root.Element(ns + "IsTruncated")?.Value == "true" ? xml.Root.Element(ns + "NextContinuationToken")?.Value : null;
        }
        while (token != null);
    }

    private static string ErrorText(string body)
    {
        try
        {
            var x = XDocument.Parse(body);
            return x.Root?.Element(x.Root.Name.Namespace + "Message")?.Value ?? x.Root?.Element("Message")?.Value ?? body;
        }
        catch (System.Xml.XmlException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }

    /// <summary>AWS Signature Version 4 for a GET without body.</summary>
    internal static void SignV4(HttpRequestMessage req, string host, string canonicalPath, string canonicalQuery, string region, string accessKey, string secretKey, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        var amzDate = t.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = t.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        const string PayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"; // SHA-256 of ""
        req.Headers.Host = host;
        req.Headers.Add("x-amz-date", amzDate);
        req.Headers.Add("x-amz-content-sha256", PayloadHash);
        var canonicalPathEncoded = string.Join('/', canonicalPath.Split('/').Select(Uri.EscapeDataString));
        var canonical = $"GET\n{canonicalPathEncoded}\n{canonicalQuery}\nhost:{host}\nx-amz-content-sha256:{PayloadHash}\nx-amz-date:{amzDate}\n\nhost;x-amz-content-sha256;x-amz-date\n{PayloadHash}";
        var scope = $"{date}/{region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), "s3"), "aws4_request");
        var signature = Hex(Hmac(key, toSign));
        req.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature={signature}");
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // ---------------- Azure Blob (container URL with a SAS token) ----------------

    private static async Task Azure(ScanSession s, RemoteSpec spec, CancellationToken ct)
    {
        var url = spec.Url.Trim();
        var q = url.IndexOf('?');
        if (q < 0)
            throw new ArgumentException("Give the container URL with a SAS token (…/container?sv=…&sig=…).");
        var container = url[..q].TrimEnd('/');
        var sas = url[(q + 1)..];
        var prefix = spec.Path.TrimStart('/');
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
            prefix += "/";
        var tree = new RemoteTree(s);
        s.MethodUsed = "Azure Blob " + new Uri(container).Host;
        string? marker = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            s.PauseGate.Wait(ct);
            var list = $"{container}?restype=container&comp=list&maxresults=5000{(prefix.Length > 0 ? "&prefix=" + Uri.EscapeDataString(prefix) : "")}{(marker != null ? "&marker=" + Uri.EscapeDataString(marker) : "")}&{sas}";
            using var req = new HttpRequestMessage(HttpMethod.Get, list);
            req.Headers.Add("x-ms-version", "2021-08-06");
            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new IOException($"Azure: {(int)resp.StatusCode} {resp.ReasonPhrase}: {ErrorText(body)}");
            var xml = XDocument.Parse(body);
            foreach (var b in xml.Root!.Element("Blobs")?.Elements("Blob") ?? [])
            {
                var name = b.Element("Name")!.Value;
                var rel = name.Length >= prefix.Length ? name[prefix.Length..] : name;
                var p = b.Element("Properties");
                var size = long.Parse(p?.Element("Content-Length")?.Value ?? "0", CultureInfo.InvariantCulture);
                var mod = DateTime.TryParse(p?.Element("Last-Modified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var m) ? m : default;
                if (rel.Length > 0)
                    tree.File(rel, size, mod);
            }
            marker = xml.Root.Element("NextMarker")?.Value is { Length: > 0 } nm ? nm : null;
        }
        while (marker != null);
    }

    // ---------------- WebDAV ----------------

    private static async Task WebDav(ScanSession s, RemoteSpec spec, CancellationToken ct)
    {
        var start = new Uri(spec.Url.EndsWith('/') ? spec.Url : spec.Url + "/");
        using var handler = new HttpClientHandler();
        if (spec.User.Length > 0)
            handler.Credentials = new NetworkCredential(spec.User, spec.Password);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        var tree = new RemoteTree(s);
        s.MethodUsed = "WebDAV " + start.Host;
        var queue = new Queue<Uri>();
        queue.Enqueue(start);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        XNamespace d = "DAV:";
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            s.PauseGate.Wait(ct);
            var folder = queue.Dequeue();
            if (!seen.Add(folder.AbsolutePath))
                continue;
            s.CurrentPath = Uri.UnescapeDataString(folder.AbsolutePath);
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), folder);
            req.Headers.Add("Depth", "1");
            req.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>", Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (ReferenceEquals(folder, start))
                    throw new IOException($"WebDAV: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                s.AddError(folder.ToString(), $"{(int)resp.StatusCode} {resp.ReasonPhrase}");
                continue;
            }
            var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var r in xml.Descendants(d + "response"))
            {
                var href = r.Element(d + "href")?.Value;
                if (href == null)
                    continue;
                var uri = new Uri(start, href);
                if (uri.AbsolutePath.TrimEnd('/') == folder.AbsolutePath.TrimEnd('/'))
                    continue;
                if (!uri.AbsolutePath.StartsWith(start.AbsolutePath, StringComparison.Ordinal))
                    continue;
                var rel = Uri.UnescapeDataString(uri.AbsolutePath[start.AbsolutePath.Length..]).Trim('/');
                var prop = r.Descendants(d + "prop").FirstOrDefault();
                var isDir = prop?.Element(d + "resourcetype")?.Element(d + "collection") != null;
                var mod = DateTime.TryParse(prop?.Element(d + "getlastmodified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var m) ? m : default;
                if (isDir)
                {
                    tree.FolderDates(rel, mod);
                    queue.Enqueue(new Uri(start, uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/"));
                }
                else
                {
                    long.TryParse(prop?.Element(d + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                    tree.File(rel, size, mod);
                }
            }
        }
    }
}
