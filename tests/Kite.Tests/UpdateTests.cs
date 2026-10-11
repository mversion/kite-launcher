using System.Text.Json;
using Kite.Core.Updates;

namespace Kite.Tests;

public class UpdateTests
{
    private static string Release(string tag, bool draft = false, bool prerelease = false, string assetName = "KiteLauncherSetup-0.4.0.exe", string? digest = "sha256:ABCDEF") => $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{draft.ToString().ToLowerInvariant()}},
          "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
          "html_url": "https://github.com/mversion/kite-launcher/releases/tag/{{tag}}",
          "body": "What's new",
          "assets": [
            { "name": "KiteLauncher-0.4.0-portable.zip", "size": 10, "browser_download_url": "https://github.com/mversion/kite-launcher/releases/download/{{tag}}/KiteLauncher-0.4.0-portable.zip" },
            { "name": "{{assetName}}", "size": 12345, "digest": {{(digest is null ? "null" : $"\"{digest}\"")}},
              "browser_download_url": "https://github.com/mversion/kite-launcher/releases/download/{{tag}}/{{assetName}}" }
          ]
        }
        """;

    private static UpdateInfo? Parse(string json, string current = "0.3.0") =>
        GitHubRelease.Parse(JsonDocument.Parse(json), Version.Parse(current), "https://example/releases");

    [Fact]
    public void Newer_release_with_installer_is_offered()
    {
        var u = Parse(Release("v0.4.0"));
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 4, 0), u!.Version);
        Assert.Equal("KiteLauncherSetup-0.4.0.exe", u.AssetName);
        Assert.Equal(12345, u.Size);
        Assert.Equal("abcdef", u.Sha256);
        Assert.EndsWith("/v0.4.0", u.PageUrl);
    }

    [Theory]
    [InlineData("v0.3.0")]   // same version
    [InlineData("v0.2.9")]   // older
    [InlineData("nightly")]  // not a version
    public void Same_older_or_odd_tags_are_ignored(string tag) => Assert.Null(Parse(Release(tag)));

    [Fact]
    public void Drafts_prereleases_and_missing_installers_are_ignored()
    {
        Assert.Null(Parse(Release("v0.4.0", draft: true)));
        Assert.Null(Parse(Release("v0.4.0", prerelease: true)));
        Assert.Null(Parse(Release("v0.4.0", assetName: "something-else.exe")));
    }

    [Fact]
    public void Missing_digest_still_parses()
    {
        var u = Parse(Release("v1.0.0", digest: null));
        Assert.NotNull(u);
        Assert.Null(u!.Sha256);
    }
}
