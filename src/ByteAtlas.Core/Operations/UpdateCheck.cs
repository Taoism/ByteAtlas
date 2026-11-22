using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ByteAtlas.Operations;

/// <summary>A published release of ByteAtlas.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Url, string Notes, DateTime Published);

/// <summary>
/// Asks GitHub for the latest release (one anonymous request to the public API, nothing about this computer is
/// sent but the program name and version in the User-Agent). Nothing is downloaded or installed.
/// </summary>
public static class UpdateCheck
{
    public const string Repository = "Taoism/ByteAtlas";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    public static Version Current => typeof(UpdateCheck).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ByteAtlas", Current.ToString(3)));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    /// <summary>The latest release (pre-releases and drafts are not "latest" on GitHub).</summary>
    public static async Task<ReleaseInfo?> LatestAsync(CancellationToken ct = default)
    {
        using var r = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (!r.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)r.StatusCode} {r.ReasonPhrase}");
        await using var stream = await r.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    internal static ReleaseInfo? Parse(JsonElement e)
    {
        var tag = e.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (ParseVersion(tag) is not { } version)
            return null;
        var url = e.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
        var notes = e.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var published = e.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
            && DateTime.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : default;
        return new ReleaseInfo(version, tag, url, notes, published);
    }

    /// <summary>"v1.2.3", "1.2", "v1.2.3-beta" → 1.2.3 (the suffix is ignored).</summary>
    public static Version? ParseVersion(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+', ' ']);
        if (dash >= 0)
            s = s[..dash];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v))
            return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static bool IsNewer(ReleaseInfo r) => r.Version > Current;
}
