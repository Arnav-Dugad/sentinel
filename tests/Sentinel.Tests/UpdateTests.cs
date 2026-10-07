using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sentinel.Core.Updates;
using Xunit;

namespace Sentinel.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("0.9.0", 0, 9, 0, null)]
    [InlineData("1.0", 1, 0, 0, null)]
    [InlineData("v2.0.0-beta.1", 2, 0, 0, "beta.1")]
    [InlineData("1.4.2+abc123", 1, 4, 2, null)]
    public void Parses(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(AppVersion.TryParse(text, out var v));
        Assert.Equal(new AppVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    [InlineData("1.0.0-")]
    public void RejectsGarbage(string text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void OrdersLikeSemVer()
    {
        static AppVersion V(string s) => AppVersion.TryParse(s, out var v) ? v : throw new FormatException(s);
        Assert.True(V("0.10.0") > V("0.9.9"));
        Assert.True(V("1.0.0") > V("1.0.0-rc.1"));
        Assert.True(V("1.0.1") > V("1.0.0"));
        Assert.Equal(0, V("v1.0.0").CompareTo(V("1.0.0")));
        Assert.Equal(new AppVersion(0, 9, 0), AppVersion.FromAssembly(new Version(0, 9, 0, 0)));
    }
}

public class UpdateFeedTests
{
    internal static string ReleaseJson(string tag, bool prerelease = false, string body = "## Fixes\n- **Faster** charts\n- `x`") => $$"""
        {
          "tag_name": "{{tag}}", "name": "Sentinel {{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/Arnav-Dugad/sentinel/releases/tag/{{tag}}",
          "published_at": "2026-10-07T10:00:00Z",
          "body": {{System.Text.Json.JsonSerializer.Serialize(body)}},
          "assets": [
            { "name": "Sentinel-{{tag.TrimStart('v')}}-win-x64.zip", "size": 0, "browser_download_url": "https://github.com/Arnav-Dugad/sentinel/releases/download/{{tag}}/Sentinel-{{tag.TrimStart('v')}}-win-x64.zip" },
            { "name": "Sentinel-{{tag.TrimStart('v')}}-win-x64.zip.sig", "size": 88, "browser_download_url": "https://github.com/Arnav-Dugad/sentinel/releases/download/{{tag}}/Sentinel-{{tag.TrimStart('v')}}-win-x64.zip.sig" },
            { "name": "evil.zip", "size": 1, "browser_download_url": "http://example.com/evil.zip" }
          ]
        }
        """;

    [Fact]
    public void ParsesReleaseAndFindsSignedPackage()
    {
        var r = UpdateFeed.ParseRelease(ReleaseJson("v1.2.0"))!;
        Assert.Equal(new AppVersion(1, 2, 0), r.Version);
        Assert.Equal(2, r.Assets.Count); // the non-HTTPS asset is dropped
        var pkg = r.PackageFor("win-x64");
        Assert.NotNull(pkg);
        Assert.EndsWith(".zip.sig", pkg!.Value.Signature.Name, StringComparison.Ordinal);
        Assert.Null(r.PackageFor("win-arm64"));
        Assert.DoesNotContain("**", r.Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("##", r.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void PreReleasesAndDraftsAreIgnored() => Assert.Null(UpdateFeed.ParseRelease(ReleaseJson("v2.0.0", prerelease: true)));

    [Fact]
    public void NotesAreSanitised() => Assert.DoesNotContain('‮', UpdateFeed.CleanNotes("ok‮exe"));
}

public class UpdateSignatureTests
{
    [Fact]
    public void SignedDataVerifiesAndTamperingIsDetected()
    {
        var (priv, pub) = UpdateSignature.CreateKeyPair();
        var data = Encoding.UTF8.GetBytes("package contents");
        var sig = UpdateSignature.Sign(new MemoryStream(data), priv);
        Assert.True(UpdateSignature.Verify(new MemoryStream(data), sig, pub));
        data[0] ^= 1;
        Assert.False(UpdateSignature.Verify(new MemoryStream(data), sig, pub));
    }

    [Fact]
    public void AnotherKeyIsRejected()
    {
        var (priv, _) = UpdateSignature.CreateKeyPair();
        var (_, otherPub) = UpdateSignature.CreateKeyPair();
        var data = Encoding.UTF8.GetBytes("x");
        Assert.False(UpdateSignature.Verify(new MemoryStream(data), UpdateSignature.Sign(new MemoryStream(data), priv), otherPub));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void MalformedSignaturesAreRejected(string sig)
    {
        var (_, pub) = UpdateSignature.CreateKeyPair();
        Assert.False(UpdateSignature.Verify(new MemoryStream([1, 2, 3]), sig, pub));
    }

    [Fact]
    public void OfficialKeyIsConfigured() => Assert.NotEqual("not configured", UpdateSignature.Fingerprint(UpdateSignature.OfficialPublicKeyPem));
}

public class UpdateClientTests
{
    [Theory]
    [InlineData("https://github.com/Arnav-Dugad/sentinel/releases/download/v1/x.zip", true)]
    [InlineData("https://objects.githubusercontent.com/abc", true)]
    [InlineData("https://release-assets.githubusercontent.com/abc", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://evil.example.com/x.zip", false)]
    [InlineData("https://github.com.evil.example/x.zip", false)]
    public void OnlyGitHubOverHttpsIsTrusted(string url, bool ok)
    {
        var ex = Record.Exception(() => UpdateClient.EnsureTrusted(new Uri(url)));
        Assert.Equal(ok, ex is null);
    }

    private static byte[] MakePackage(string exeText)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("Sentinel.exe").Open())) w.Write(exeText);
            using (var w = new StreamWriter(zip.CreateEntry("Assets/Sentinel.png").Open())) w.Write("png");
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task DownloadsVerifiesAndStages()
    {
        var ct = TestContext.Current.CancellationToken;
        var (priv, pub) = UpdateSignature.CreateKeyPair();
        var pkg = MakePackage("new build");
        var sig = UpdateSignature.Sign(new MemoryStream(pkg), priv);
        using var root = new TempDir();
        var client = new UpdateClient(new HttpClient(new FakeGitHub("v1.2.0", pkg, sig)), root.Path, pub, NullLogger<UpdateClient>.Instance);

        var latest = await client.GetLatestAsync(ct);
        Assert.Equal(new AppVersion(1, 2, 0), latest!.Version);
        var progress = new List<double>();
        var staged = await client.DownloadAndStageAsync(latest, "win-x64", new SyncProgress(progress.Add), ct);
        Assert.True(File.Exists(Path.Combine(staged.AppDirectory, "Sentinel.exe")));
        Assert.True(File.Exists(Path.Combine(staged.AppDirectory, "Assets", "Sentinel.png")));
        Assert.Contains(progress, p => p >= 0.99);

        Assert.Equal(new AppVersion(1, 2, 0), client.FindStaged(new AppVersion(1, 1, 0))!.Version);
        Assert.Null(client.FindStaged(new AppVersion(1, 2, 0)));
        client.CleanUp(new AppVersion(1, 2, 0));
        Assert.Empty(Directory.EnumerateDirectories(root.Path));
    }

    [Fact]
    public async Task TamperedPackageIsDiscarded()
    {
        var ct = TestContext.Current.CancellationToken;
        var (priv, pub) = UpdateSignature.CreateKeyPair();
        var pkg = MakePackage("genuine");
        var sig = UpdateSignature.Sign(new MemoryStream(pkg), priv);
        var tampered = MakePackage("malicious");
        using var root = new TempDir();
        var client = new UpdateClient(new HttpClient(new FakeGitHub("v1.2.0", tampered, sig)), root.Path, pub, NullLogger<UpdateClient>.Instance);
        var latest = await client.GetLatestAsync(ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAndStageAsync(latest!, "win-x64", null, ct));
        Assert.Null(client.FindStaged(new AppVersion(0, 1, 0)));
        Assert.False(Directory.Exists(Path.Combine(root.Path, "1.2.0")));
    }

    [Fact]
    public async Task NoReleaseYetIsNotAnError()
    {
        using var root = new TempDir();
        var client = new UpdateClient(new HttpClient(new FakeGitHub(null, [], "")), root.Path, "", NullLogger<UpdateClient>.Instance);
        Assert.Null(await client.GetLatestAsync(TestContext.Current.CancellationToken));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sentinel-update-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Serves a release feed, a package and its signature like GitHub would.</summary>
    private sealed class FakeGitHub(string? tag, byte[] package, string signature) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage resp;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                resp = tag is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UpdateFeedTests.ReleaseJson(tag)) };
            else if (path.EndsWith(".zip.sig", StringComparison.Ordinal))
                resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature) };
            else if (path.EndsWith(".zip", StringComparison.Ordinal))
                resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
            else
                resp = new HttpResponseMessage(HttpStatusCode.NotFound);
            resp.RequestMessage = request;
            return Task.FromResult(resp);
        }
    }
}
