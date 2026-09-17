using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Notlar;

// Checks GitHub Releases for a newer installer. The only network request the application makes;
// it sends no identifying data beyond a generic User-Agent. Disabled by a "guncelleme-kapali" file in data/.
public static class Updater
{
    // GitHub "owner/repo" whose Releases carry the installer.
    public const string Repository = "Obirize/NoteBook";
    public sealed record Release(Version Version, string InstallerUrl, string? ChecksumUrl, string Page);
    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }
    public static string CurrentLabel => Current.ToString(3);
    public static bool Enabled(string dataDirectory) => !Repository.StartsWith("OWNER/") && !File.Exists(Path.Combine(dataDirectory, "guncelleme-kapali"));
    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Notlar", CurrentLabel));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var client = Client();
        string json = await client.GetStringAsync("https://api.github.com/repos/" + Repository + "/releases/latest", ct);
        var release = Parse(json);
        return release != null && release.Version > Current ? release : null;
    }
    // Reads tag "v1.2.3" and the installer asset (plus an optional ".sha256" file) from the GitHub API response.
    public static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag)) return null;
        string label = (tag.GetString() ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(label, out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        string? installer = null, checksum = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (name.StartsWith("NoteBook-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) installer = url;
                if (name.StartsWith("NoteBook-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) checksum = url;
            }
        if (installer == null || !installer.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new Release(version, installer, checksum, page);
    }
    public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var client = Client();
        client.Timeout = TimeSpan.FromMinutes(10);
        string folder = Path.Combine(Path.GetTempPath(), "NoteBook-update");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "NoteBook-Setup-" + release.Version.ToString(3) + ".exe");
        using (var response = await client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920]; long done = 0; int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            { await target.WriteAsync(buffer.AsMemory(0, read), ct); done += read; if (total > 0) progress?.Report((double)done / total); }
        }
        if (release.ChecksumUrl != null)
        {
            string expected = (await client.GetStringAsync(release.ChecksumUrl, ct)).Trim().Split(' ', '\t', '\n')[0];
            if (!VerifyChecksum(path, expected)) { File.Delete(path); throw new InvalidDataException(L10n.T("UpdateChecksumFailed")); }
        }
        return path;
    }
    public static bool VerifyChecksum(string path, string expectedHex)
    {
        using var stream = File.OpenRead(path);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(expectedHex.Trim()));
    }
    // Silent per-user install; the installer closes the running application, replaces app\ and relaunches it.
    public static void Install(string installerPath) =>
        Process.Start(new ProcessStartInfo(installerPath) { Arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS /UPDATE=1", UseShellExecute = true });
}
