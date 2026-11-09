using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldTime.Core;

/// <summary>A release version: 1.2.3 or 1.2.3-beta.1 (a leading "v" and "+build" are ignored).</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string? Pre = null) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0) return false;
        }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var n = new int[3];
        for (int i = 0; i < Math.Min(3, parts.Length); i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        version = new AppVersion(n[0], n[1], n[2], pre);
        return true;
    }

    public bool IsPrerelease => Pre is not null;

    /// <summary>Semantic-version order: 1.0.0-beta &lt; 1.0.0-beta.2 &lt; 1.0.0 &lt; 1.0.1.</summary>
    public int CompareTo(AppVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre is null) return other.Pre is null ? 0 : 1;
        if (other.Pre is null) return -1;
        var a = Pre.Split('.');
        var b = other.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = int.TryParse(a[i], out int ai), bn = int.TryParse(b[i], out int bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => Pre is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";
}

public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A newer release that can be installed: its installer and the installer's checksum file.</summary>
public sealed record UpdateOffer(AppVersion Version, string PageUrl, ReleaseAsset Installer, ReleaseAsset Checksum);

/// <summary>
/// Reads the GitHub "latest release" response and decides whether it is an
/// update. Pure, so the decision is unit-tested; the app does the networking.
/// </summary>
public static class UpdateFeed
{
    public const string Owner = "Th3EvilGod";
    public const string Repo = "world-time-flyout";
    public const string LatestReleaseApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public static string ReleasePage(AppVersion v) => $"https://github.com/{Owner}/{Repo}/releases/tag/v{v}";

    /// <summary>The installer each release carries (built by CI).</summary>
    public static string InstallerName(AppVersion v) => $"WorldTimeFlyout-Setup-{v}-win-x64.exe";

    /// <summary>
    /// The offer when the release is newer than <paramref name="current"/> and has
    /// both the installer and its .sha256; otherwise null. Drafts and
    /// pre-releases are never offered.
    /// </summary>
    public static UpdateOffer? Choose(string json, AppVersion current)
    {
        GitHubRelease? release;
        try { release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease); }
        catch (JsonException) { return null; }
        if (release is null || release.Draft || release.Prerelease) return null;
        if (!AppVersion.TryParse(release.TagName, out var version) || version.IsPrerelease) return null;
        if (!(version > current)) return null;

        string installerName = InstallerName(version);
        ReleaseAsset? installer = null, checksum = null;
        foreach (var a in release.Assets ?? [])
        {
            if (a.Name is null || a.Url is null) continue;
            if (string.Equals(a.Name, installerName, StringComparison.OrdinalIgnoreCase))
                installer = new ReleaseAsset(a.Name, a.Url, a.Size);
            else if (string.Equals(a.Name, installerName + ".sha256", StringComparison.OrdinalIgnoreCase))
                checksum = new ReleaseAsset(a.Name, a.Url, a.Size);
        }
        if (installer is null || checksum is null) return null;
        if (!IsWebUrl(installer.Url) || !IsWebUrl(checksum.Url)) return null;
        return new UpdateOffer(version, release.HtmlUrl ?? ReleasePage(version), installer, checksum);
    }

    /// <summary>The SHA-256 from a "hash  file name" line (sha256sum / CI format), lower-case hex.</summary>
    public static string? ParseChecksum(string text, string fileName)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 64) continue;
            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var rest = line[64..].Trim().TrimStart('*');
            if (rest.Length == 0 || string.Equals(rest, fileName, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? Url { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
