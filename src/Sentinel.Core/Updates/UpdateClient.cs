using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Sentinel.Core.Updates;

public sealed record StagedUpdate(AppVersion Version, string AppDirectory, string Title, string Notes, Uri Page);

/// <summary>
/// Talks to GitHub Releases over HTTPS and stages verified packages under the updates folder. Sends no information
/// beyond a standard HTTPS request (User-Agent "Sentinel/&lt;version&gt;"). Nothing is executed here; installation is
/// a separate, user-visible step.
/// </summary>
public sealed class UpdateClient(HttpClient http, string updatesRoot, string publicKeyPem, ILogger<UpdateClient> log)
{
    public const long MaxPackageBytes = 400L * 1024 * 1024;
    private const string ReadyFile = "ready.json";

    private static readonly string[] AllowedHosts = ["github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    public string UpdatesRoot { get; } = updatesRoot;

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UpdateFeed.LatestReleaseApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        EnsureTrusted(resp.RequestMessage?.RequestUri);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null; // no published release yet
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (json.Length > 2_000_000) throw new InvalidDataException("Release feed is unexpectedly large.");
        return UpdateFeed.ParseRelease(json);
    }

    /// <summary>Downloads, verifies and unpacks a release. Returns the staged update; throws if verification fails.</summary>
    public async Task<StagedUpdate> DownloadAndStageAsync(ReleaseInfo release, string runtime, IProgress<double>? progress, CancellationToken ct)
    {
        var assets = release.PackageFor(runtime) ?? throw new InvalidDataException($"This release has no signed package for {runtime}.");
        if (assets.Package.Size > MaxPackageBytes) throw new InvalidDataException("Update package is unexpectedly large.");

        var dir = Path.Combine(UpdatesRoot, release.Version.ToString());
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "package.zip");

        var signature = await GetStringAsync(assets.Signature.DownloadUrl, 4096, ct).ConfigureAwait(false);
        await DownloadFileAsync(assets.Package.DownloadUrl, zipPath, assets.Package.Size, progress, ct).ConfigureAwait(false);

        bool valid;
        await using (var zip = File.OpenRead(zipPath)) valid = UpdateSignature.Verify(zip, signature, publicKeyPem);
        if (!valid)
        {
            log.LogWarning("Update {Version} rejected: signature does not match the Sentinel release key", release.Version);
            TryDelete(dir);
            throw new InvalidDataException("The update's signature is not valid. It was discarded and not installed.");
        }

        var appDir = Path.Combine(dir, "app");
        // .NET rejects entries that would extract outside the destination (zip-slip).
        ZipFile.ExtractToDirectory(zipPath, appDir, overwriteFiles: false);
        File.Delete(zipPath);
        var exe = Path.Combine(appDir, "Sentinel.exe");
        if (!File.Exists(exe))
        {
            TryDelete(dir);
            throw new InvalidDataException("The update package does not contain Sentinel.exe.");
        }

        var staged = new StagedUpdate(release.Version, appDir, release.Title, release.Notes, release.Page);
        await File.WriteAllTextAsync(Path.Combine(dir, ReadyFile), JsonSerializer.Serialize(new ReadyMarker(release.Version.ToString(), release.Title, release.Notes, release.Page.ToString())), ct)
            .ConfigureAwait(false);
        log.LogInformation("Update {Version} downloaded, verified and staged", release.Version);
        return staged;
    }

    /// <summary>The newest verified, fully staged update newer than <paramref name="current"/>, if any.</summary>
    public StagedUpdate? FindStaged(AppVersion current)
    {
        if (!Directory.Exists(UpdatesRoot)) return null;
        StagedUpdate? best = null;
        foreach (var dir in Directory.EnumerateDirectories(UpdatesRoot))
        {
            try
            {
                var marker = Path.Combine(dir, ReadyFile);
                if (!File.Exists(marker)) continue;
                var m = JsonSerializer.Deserialize<ReadyMarker>(File.ReadAllText(marker));
                if (m is null || !AppVersion.TryParse(m.Version, out var v) || v <= current) continue;
                var app = Path.Combine(dir, "app");
                if (!File.Exists(Path.Combine(app, "Sentinel.exe"))) continue;
                if (best is null || v > best.Version)
                    best = new StagedUpdate(v, app, m.Title ?? "", m.Notes ?? "", Uri.TryCreate(m.Page, UriKind.Absolute, out var u) ? u : UpdateFeed.ReleasesPage);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                log.LogInformation(ex, "Ignoring unreadable staged update in {Dir}", dir);
            }
        }
        return best;
    }

    /// <summary>Removes staged packages that are not newer than the running version (after an update was installed).</summary>
    public void CleanUp(AppVersion current)
    {
        if (!Directory.Exists(UpdatesRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(UpdatesRoot))
        {
            var name = Path.GetFileName(dir);
            if (!AppVersion.TryParse(name, out var v) || v <= current || !File.Exists(Path.Combine(dir, ReadyFile))) TryDelete(dir);
        }
    }

    private async Task<string> GetStringAsync(Uri url, int maxBytes, CancellationToken ct)
    {
        EnsureTrusted(url);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        EnsureTrusted(resp.RequestMessage?.RequestUri);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > maxBytes) throw new InvalidDataException("Signature file is unexpectedly large.");
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private async Task DownloadFileAsync(Uri url, string path, long expected, IProgress<double>? progress, CancellationToken ct)
    {
        EnsureTrusted(url);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        EnsureTrusted(resp.RequestMessage?.RequestUri);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? expected;
        if (total > MaxPackageBytes) throw new InvalidDataException("Update package is unexpectedly large.");
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long done = 0;
        int n;
        var lastReport = -1.0;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            done += n;
            if (done > MaxPackageBytes) throw new InvalidDataException("Update package is unexpectedly large.");
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            if (total > 0 && progress is not null)
            {
                var pct = Math.Min(1, (double)done / total);
                if (pct - lastReport >= 0.01)
                {
                    lastReport = pct;
                    progress.Report(pct);
                }
            }
        }
        if (expected > 0 && done != expected) throw new InvalidDataException("The download was incomplete.");
    }

    /// <summary>Updates are only fetched over HTTPS from GitHub. (Authenticity comes from the signature; this is defence in depth.)</summary>
    public static void EnsureTrusted(Uri? uri)
    {
        if (uri is null) return;
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Refusing to download updates from {uri.Host}.");
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ReadyMarker(string Version, string? Title, string? Notes, string? Page);
}
