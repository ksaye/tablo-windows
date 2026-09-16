using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabloWindows.Services;

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string MsiUrl { get; init; }
    public required string ReleaseUrl { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// Checks this app's GitHub releases for a newer version and, if the user agrees, downloads and
/// launches the MSI. Public repository, so no token: GitHub allows 60 unauthenticated API calls an
/// hour per address, and this makes one per start.
/// </summary>
public sealed class UpdateService
{
    private const string ReleasesUrl = "https://api.github.com/repos/ksaye/tablo-windows/releases?per_page=20";

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<Asset> Assets { get; set; } = new();
    }

    private sealed class Asset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }

    private readonly HttpClient _http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API refuses requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"tablo-windows/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Returns update info if a newer release exists; null if current, unreachable, or misconfigured.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.GetAsync(ReleasesUrl, ct);
            if (!res.IsSuccessStatusCode) return null;

            var releases = JsonSerializer.Deserialize<List<Release>>(await res.Content.ReadAsStringAsync(ct));
            if (releases is null) return null;

            var newest = releases
                .Where(r => !r.Draft && !r.Prerelease)
                .Select(r => (Release: r, Ok: TryParseVersion(r.TagName, out var v), Version: v))
                .Where(x => x.Ok)
                .OrderByDescending(x => x.Version)
                .Select(x => (x.Release, x.Version))
                .FirstOrDefault();

            var rel = newest.Release;
            if (rel is null) return null;
            var latest = newest.Version;
            if (latest <= CurrentVersion) return null;

            var msi = rel.Assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
            if (msi is null) return null;

            return new UpdateInfo
            {
                Version = latest,
                MsiUrl = msi.BrowserDownloadUrl,
                ReleaseUrl = rel.HtmlUrl ?? "https://github.com/ksaye/tablo-windows/releases",
                Notes = rel.Body
            };
        }
        catch { return null; }
    }

    /// <summary>Download the MSI to a temp file and launch it, returning the path (or null on failure).</summary>
    public async Task<string?> DownloadMsiAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), $"TabloWindows-{info.Version}.msi");
            using var res = await _http.GetAsync(info.MsiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();

            var total = res.Content.Headers.ContentLength ?? -1;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(dest);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
            return dest;
        }
        catch { return null; }
    }

    /// <summary>Launch the downloaded MSI installer (self-elevates via UAC).</summary>
    public static void RunInstaller(string msiPath)
    {
        Process.Start(new ProcessStartInfo("msiexec", $"/i \"{msiPath}\"") { UseShellExecute = true });
    }

    /// <summary>Release tags are "v1.2.3".</summary>
    private static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrEmpty(tag)) return false;
        var s = tag.TrimStart('v', 'V');
        return Version.TryParse(s, out version!);
    }
}
