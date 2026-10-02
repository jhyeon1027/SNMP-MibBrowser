using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace SnmpMibBrowser;

public sealed class UpdateService
{
    private const string RepositorySlug = "jhyeon1027/SNMP-MibBrowser";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public UpdateService() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("SNMP-MibBrowser/1.0");

    public async Task<GitHubRelease?> FindUpdateAsync(bool includePrereleases, Version current, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{RepositorySlug}/releases?per_page=30", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.GetProperty("draft").GetBoolean()) continue;
            var prerelease = item.GetProperty("prerelease").GetBoolean();
            if (prerelease && !includePrereleases) continue;
            var tag = item.GetProperty("tag_name").GetString() ?? "";
            if (!TryVersion(tag, out var version) || version <= current) continue;
            var assets = item.GetProperty("assets").EnumerateArray().ToList();
            var asset = assets.FirstOrDefault(x => new[] { "SNMP-MibBrowser.exe", "SNMP MibBrowser.exe" }.Contains(x.GetProperty("name").GetString(), StringComparer.OrdinalIgnoreCase));
            if (asset.ValueKind == JsonValueKind.Undefined) asset = assets.FirstOrDefault(x => x.GetProperty("name").GetString()?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
            if (asset.ValueKind == JsonValueKind.Undefined) continue;
            return new GitHubRelease(tag, item.GetProperty("name").GetString() ?? tag, item.GetProperty("body").GetString() ?? "", prerelease,
                item.GetProperty("html_url").GetString() ?? "", asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.GetProperty("name").GetString() ?? "SNMP-MibBrowser.exe", asset.TryGetProperty("digest", out var digest) ? digest.GetString() ?? "" : "");
        }
        return null;
    }

    public async Task<string> DownloadAsync(GitHubRelease release, IProgress<int> progress, CancellationToken ct)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SNMP-MibBrowser", "Updates");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, $"SNMP-MibBrowser-{SafeName(release.Tag)}.exe");
        using var response = await _http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destination);
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct); total += read;
            if (length > 0) progress.Report((int)(total * 100 / length.Value));
        }
        await output.FlushAsync(ct);
        if (release.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            output.Close();
            await using var downloaded = File.OpenRead(destination);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, ct));
            if (!actual.Equals(release.Digest[7..], StringComparison.OrdinalIgnoreCase)) { File.Delete(destination); throw new InvalidDataException("SHA-256 digest mismatch."); }
        }
        return destination;
    }

    public static void InstallAfterExit(string downloadedExe)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Application path is unavailable.");
        var script = Path.Combine(Path.GetTempPath(), $"snmp-mibbrowser-update-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, $"@echo off\r\ntimeout /t 2 /nobreak >nul\r\ncopy /y \"{downloadedExe}\" \"{current}\" >nul\r\nstart \"\" \"{current}\"\r\ndel \"%~f0\"\r\n");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(script) { UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
    }

    private static bool TryVersion(string tag, out Version version) => Version.TryParse(tag.Trim().TrimStart('v', 'V').Split('-')[0], out version!);
    private static string SafeName(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
