using System.Globalization;
using System.Text.Json;

namespace Sentinel.Core.Updates;

/// <summary>A release version (major.minor.patch with an optional pre-release suffix). Ordering follows SemVer 2 precedence for the parts used here.</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0) return false;
        }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var nums = new int[3];
        for (var i = 0; i < Math.Min(3, parts.Length); i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return false;
        version = new AppVersion(nums[0], nums[1], nums[2], pre);
        return true;
    }

    public static AppVersion FromAssembly(Version? v) => v is null ? default : new AppVersion(v.Major, v.Minor, Math.Max(0, v.Build));

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release ranks above any pre-release of the same version.
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record ReleaseInfo(AppVersion Version, string Title, string Notes, Uri Page, DateTimeOffset? Published, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The package for a runtime (e.g. win-x64) and its detached signature, if both are attached to the release.</summary>
    public (ReleaseAsset Package, ReleaseAsset Signature)? PackageFor(string runtime)
    {
        var name = UpdateFeed.PackageName(Version, runtime);
        var pkg = Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var sig = Assets.FirstOrDefault(a => a.Name.Equals(name + UpdateFeed.SignatureSuffix, StringComparison.OrdinalIgnoreCase));
        return pkg is null || sig is null ? null : (pkg, sig);
    }
}

/// <summary>Parses the GitHub Releases API response. All text from the feed is untrusted and sanitised for display.</summary>
public static class UpdateFeed
{
    public const string Repository = "Arnav-Dugad/sentinel";
    public const string SignatureSuffix = ".sig";
    public static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases");

    public static string PackageName(AppVersion version, string runtime) => $"Sentinel-{version}-{runtime}.zip";

    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        if (!AppVersion.TryParse(Str(root, "tag_name"), out var version)) return null;
        if (!Uri.TryCreate(Str(root, "html_url"), UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps) page = ReleasesPage;
        DateTimeOffset? published = DateTimeOffset.TryParse(Str(root, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p) ? p : null;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray().Take(64))
            {
                var name = Str(a, "name");
                if (string.IsNullOrEmpty(name) || !Uri.TryCreate(Str(a, "browser_download_url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) continue;
                var size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
                assets.Add(new ReleaseAsset(name, url, size));
            }
        }
        var title = Privacy.Redactor.SanitizeUntrusted(Str(root, "name") ?? $"Sentinel {version}", 120);
        var notes = CleanNotes(Str(root, "body"));
        return new ReleaseInfo(version, string.IsNullOrWhiteSpace(title) ? $"Sentinel {version}" : title, notes, page, published, assets);
    }

    /// <summary>Release notes are Markdown from the internet: keep plain text lines only, bounded in length.</summary>
    public static string CleanNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => Privacy.Redactor.SanitizeUntrusted(l.TrimEnd(), 300))
            .Select(l => l.TrimStart('#', ' ').Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal))
            .Take(60);
        var text = string.Join('\n', lines).Trim();
        return text.Length > 4000 ? text[..4000] + "…" : text;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
