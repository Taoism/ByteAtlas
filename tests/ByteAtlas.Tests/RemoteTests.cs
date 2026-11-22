using ByteAtlas.Remote;
using ByteAtlas.Scanning;

namespace ByteAtlas.Tests;

public class RemoteSpecTests
{
    [Fact]
    public void ParsesSsh()
    {
        var r = RemoteSpec.Parse("ssh://ba:secret@server.lan:2222/srv/data dir")!;
        Assert.Equal(RemoteKind.Ssh, r.Kind);
        Assert.Equal("server.lan", r.Host);
        Assert.Equal(2222, r.Port);
        Assert.Equal("ba", r.User);
        Assert.Equal("secret", r.Password);
        Assert.Equal("/srv/data dir", r.Path);
        Assert.Equal("ssh://ba@server.lan:2222/srv/data dir", r.Display);
        Assert.Equal(22, RemoteSpec.Parse("ssh://host/~/x")!.Port);
        Assert.Equal("~/x", RemoteSpec.Parse("ssh://host/~/x")!.Path);
    }

    [Fact]
    public void ParsesOtherSources()
    {
        var s3 = RemoteSpec.Parse("s3://bucket/some/prefix")!;
        Assert.Equal((RemoteKind.S3, "bucket", "some/prefix"), (s3.Kind, s3.Bucket, s3.Path));
        Assert.Equal("", RemoteSpec.Parse("s3://bucket")!.Path);
        var minio = RemoteSpec.Parse("s3+http://127.0.0.1:9300/ba-test/a b")!;
        Assert.Equal(("http://127.0.0.1:9300", "ba-test", "a b", true), (minio.Endpoint, minio.Bucket, minio.Path, minio.PathStyle));
        Assert.Equal("s3+http://127.0.0.1:9300/ba-test/a b", minio.Display);
        Assert.Equal(minio.Display, RemoteSpec.Parse(minio.Display)!.Display);

        var az = RemoteSpec.Parse("https://acct.blob.core.windows.net/c?sv=1&sig=SECRET")!;
        Assert.Equal(RemoteKind.AzureBlob, az.Kind);
        Assert.DoesNotContain("SECRET", az.Display, StringComparison.Ordinal);
        Assert.Equal(RemoteKind.AzureBlob, RemoteSpec.Parse("azure:http://127.0.0.1:10000/dev/c?sig=x")!.Kind);

        Assert.Equal("https://host/dav", RemoteSpec.Parse("davs://host/dav")!.Url);
        Assert.Equal("http://host/dav", RemoteSpec.Parse("dav://host/dav")!.Url);
        var dav = RemoteSpec.Parse("davs://me@cloud.example/remote.php/dav/")!;
        Assert.Equal(("https://cloud.example/remote.php/dav/", "me", ""), (dav.Url, dav.User, dav.Password));
        Assert.True(dav.NeedsSecret);
        Assert.Equal("s3://bucket", RemoteSpec.Parse("s3://bucket/")!.Display);
        Assert.Equal(RemoteKind.WebDav, RemoteSpec.Parse("https://cloud.example/remote.php/dav")!.Kind);
        Assert.Equal(RemoteKind.DiskImage, RemoteSpec.Parse(@"D:\Images\disk.VHDX")!.Kind);
        Assert.Null(RemoteSpec.Parse(@"C:\Users"));
        Assert.Null(RemoteSpec.Parse(@"\\server\share"));
    }

    [Fact]
    public void ShellPathKeepsHomeAndQuotes()
    {
        Assert.Equal("\"$HOME\"", RemoteSources.ShellPath("~"));
        Assert.Equal("\"$HOME\"/'a b'", RemoteSources.ShellPath("~/a b"));
        Assert.Equal("'/it'\\''s'", RemoteSources.ShellPath("/it's"));
    }

    [Fact]
    public void SignatureV4MatchesAwsExample()
    {
        // "GET Bucket (List Objects)" example from the AWS Signature Version 4 documentation
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/?max-keys=2&prefix=J");
        RemoteSources.SignV4(req, "examplebucket.s3.amazonaws.com", "/", "max-keys=2&prefix=J", "us-east-1",
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc));
        var auth = string.Join("", req.Headers.GetValues("Authorization"));
        Assert.EndsWith("Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", auth, StringComparison.Ordinal);
    }
}

/// <summary>
/// Scans the test servers from tests/remote/setup.sh (WSL + Docker). Run with BYTEATLAS_REMOTE_TESTS=1;
/// without it these tests do nothing.
/// </summary>
public class RemoteServerTests
{
    private const long TreeBytes = 1000 + 2500 + 1048576 + 10 + 7;

    private static bool Enabled => Environment.GetEnvironmentVariable("BYTEATLAS_REMOTE_TESTS") == "1";

    private static ScanSession Scan(RemoteSpec spec)
    {
        RemoteSources.KnownHostsFile = null;
        var s = new ScanSession(spec.Display, new ScanOptions()) { Remote = spec };
        Scanner.ScanAsync(s, CancellationToken.None).GetAwaiter().GetResult();
        return s;
    }

    private static void AssertTree(ScanSession s, int folders)
    {
        Assert.True(s.State == ScanState.Done, string.Join("; ", s.Errors.Select(e => e.Message)));
        var r = s.Root;
        var many = r.Dirs.FirstOrDefault(d => d.Name == "many");
        Assert.Equal(TreeBytes, r.Size - (many?.Size ?? 0));
        Assert.Equal(5, r.FileCount - (many?.FileCount ?? 0));
        Assert.Equal(folders, r.FolderCount - (many == null ? 0 : 2));
        var docs = r.Dirs.Single(d => d.Name == "docs");
        Assert.Equal(3500, docs.Size);
        Assert.Contains(r.Dirs, d => d.Name == "with space" && d.Files.Single().Name == "ü n.txt");
    }

    [Fact]
    public void Ssh()
    {
        if (!Enabled)
            return;
        var s = Scan(new RemoteSpec { Kind = RemoteKind.Ssh, Host = "127.0.0.1", Port = 2222, User = "ba", Password = "ba-test", Path = "/data" });
        AssertTree(s, 5);
        Assert.Equal(new DateTime(2020, 1, 2), s.Root.Dirs.Single(d => d.Name == "docs").Files.Single().Modified.Date);
        Assert.True(s.VolumeTotal > 0);
    }

    [Fact]
    public void SshWrongPasswordFails()
    {
        if (!Enabled)
            return;
        var s = Scan(new RemoteSpec { Kind = RemoteKind.Ssh, Host = "127.0.0.1", Port = 2222, User = "ba", Password = "wrong", Path = "/data" });
        Assert.Equal(ScanState.Failed, s.State);
        Assert.Contains("login failed", s.Errors.Single().Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void S3()
    {
        if (!Enabled)
            return;
        AssertTree(Scan(new RemoteSpec { Kind = RemoteKind.S3, Endpoint = "http://127.0.0.1:9300", Bucket = "ba-test", AccessKey = "bakey", Password = "ba-secret-key" }), 5);
    }

    [Fact]
    public void WebDav()
    {
        if (!Enabled)
            return;
        AssertTree(Scan(new RemoteSpec { Kind = RemoteKind.WebDav, Url = "http://127.0.0.1:8090/", User = "ba", Password = "ba-test" }), 5);
    }

    [Fact]
    public void AzureBlob()
    {
        if (!Enabled)
            return;
        // container SAS signed with Azurite's published development key (valid until 2030)
        const string Sas = "http://127.0.0.1:10000/devstoreaccount1/ba-test?sv=2021-08-06&sr=c&sp=rl&se=2030-01-01T00%3A00%3A00Z&sig=Ek2S1mLZfyXLlLZBDe5l%2BxyDIJpDQhh6oeRRgpuOcR8%3D";
        AssertTree(Scan(new RemoteSpec { Kind = RemoteKind.AzureBlob, Url = Sas }), 4); // blob storage has no empty folders
    }
}
