using WorldTime.Core;
using Xunit;

namespace WorldTime.Core.Tests;

public class UpdateTests
{
    private static AppVersion V(string s) => AppVersion.TryParse(s, out var v) ? v : throw new ArgumentException(s);

    [Theory]
    [InlineData("0.1.0", 0, 1, 0, null)]
    [InlineData("v0.2.0", 0, 2, 0, null)]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1")]
    [InlineData("0.2.0+9f5fc110a", 0, 2, 0, null)]
    [InlineData("1.2.3.0", 1, 2, 3, null)]
    public void Parses_versions(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(AppVersion.TryParse(text, out var v));
        Assert.Equal(new AppVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    [InlineData("1.2.3-")]
    public void Rejects_garbage(string text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void Orders_like_semver()
    {
        string[] ordered = ["0.1.0", "0.1.1", "0.2.0-beta", "0.2.0-beta.2", "0.2.0-beta.10", "0.2.0-rc.1", "0.2.0", "0.10.0", "1.0.0"];
        for (int i = 1; i < ordered.Length; i++)
            Assert.True(V(ordered[i - 1]) < V(ordered[i]), $"{ordered[i - 1]} < {ordered[i]}");
        Assert.Equal(0, V("v0.2.0").CompareTo(V("0.2.0+abc")));
    }

    private static string Release(string tag, bool pre = false, bool draft = false, bool withInstaller = true, bool withChecksum = true)
    {
        var v = tag.TrimStart('v');
        var assets = new List<string>
        {
            $$"""{"name":"WorldTimeFlyout-{{v}}-win-x64.zip","browser_download_url":"https://github.com/x/y/releases/download/{{tag}}/WorldTimeFlyout-{{v}}-win-x64.zip","size":1}""",
        };
        if (withInstaller)
            assets.Add($$"""{"name":"WorldTimeFlyout-Setup-{{v}}-win-x64.exe","browser_download_url":"https://github.com/x/y/releases/download/{{tag}}/WorldTimeFlyout-Setup-{{v}}-win-x64.exe","size":30000000}""");
        if (withChecksum)
            assets.Add($$"""{"name":"WorldTimeFlyout-Setup-{{v}}-win-x64.exe.sha256","browser_download_url":"https://github.com/x/y/releases/download/{{tag}}/WorldTimeFlyout-Setup-{{v}}-win-x64.exe.sha256","size":101}""");
        return $$"""
            {"tag_name":"{{tag}}","html_url":"https://github.com/x/y/releases/tag/{{tag}}","draft":{{(draft ? "true" : "false")}},
             "prerelease":{{(pre ? "true" : "false")}},"assets":[{{string.Join(",", assets)}}],"body":"notes"}
            """;
    }

    [Fact]
    public void Offers_a_newer_release_with_installer_and_checksum()
    {
        var offer = UpdateFeed.Choose(Release("v0.2.0"), V("0.1.0"));
        Assert.NotNull(offer);
        Assert.Equal(V("0.2.0"), offer.Version);
        Assert.Equal("WorldTimeFlyout-Setup-0.2.0-win-x64.exe", offer.Installer.Name);
        Assert.EndsWith(".exe.sha256", offer.Checksum.Url);
        Assert.Equal("https://github.com/x/y/releases/tag/v0.2.0", offer.PageUrl);
    }

    [Fact]
    public void Ignores_same_older_prerelease_draft_and_incomplete_releases()
    {
        Assert.Null(UpdateFeed.Choose(Release("v0.2.0"), V("0.2.0")));
        Assert.Null(UpdateFeed.Choose(Release("v0.1.0"), V("0.2.0")));
        Assert.Null(UpdateFeed.Choose(Release("v0.3.0", pre: true), V("0.2.0")));
        Assert.Null(UpdateFeed.Choose(Release("v0.3.0-beta.1"), V("0.2.0")));
        Assert.Null(UpdateFeed.Choose(Release("v0.3.0", draft: true), V("0.2.0")));
        Assert.Null(UpdateFeed.Choose(Release("v0.3.0", withInstaller: false), V("0.2.0")));
        Assert.Null(UpdateFeed.Choose(Release("v0.3.0", withChecksum: false), V("0.2.0")));
        Assert.Null(UpdateFeed.Choose("not json", V("0.2.0")));
        Assert.Null(UpdateFeed.Choose("""{"message":"Not Found"}""", V("0.2.0")));
    }

    [Fact]
    public void A_prerelease_build_is_offered_the_final_release()
    {
        Assert.NotNull(UpdateFeed.Choose(Release("v0.2.0"), V("0.2.0-beta.3")));
    }

    [Fact]
    public void Only_https_downloads_or_local_test_feeds()
    {
        var plainHttp = Release("v0.3.0").Replace("https://github.com", "http://example.com");
        Assert.Null(UpdateFeed.Choose(plainHttp, V("0.2.0")));
        var local = Release("v0.3.0").Replace("https://github.com", "http://127.0.0.1:8765");
        Assert.NotNull(UpdateFeed.Choose(local, V("0.2.0")));
    }

    [Fact]
    public void Reads_checksum_files()
    {
        const string hash = "efad79fe3404bc1c8795862eb5c1b8e97391758cc7298062e090dc503b6e502f";
        Assert.Equal(hash, UpdateFeed.ParseChecksum($"{hash}  WorldTimeFlyout-Setup-0.2.0-win-x64.exe\n", "WorldTimeFlyout-Setup-0.2.0-win-x64.exe"));
        Assert.Equal(hash, UpdateFeed.ParseChecksum(hash.ToUpperInvariant() + " *WorldTimeFlyout-Setup-0.2.0-win-x64.exe", "WorldTimeFlyout-Setup-0.2.0-win-x64.exe"));
        Assert.Equal(hash, UpdateFeed.ParseChecksum(hash, "anything.exe"));
        Assert.Null(UpdateFeed.ParseChecksum($"{hash}  other.exe", "WorldTimeFlyout-Setup-0.2.0-win-x64.exe"));
        Assert.Null(UpdateFeed.ParseChecksum("not a hash", "x.exe"));
    }
}
