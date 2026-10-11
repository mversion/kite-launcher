using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kite.Core.Updates;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string AssetName, string DownloadUrl, long Size, string? Sha256);

/// <summary>Reads GitHub's "latest release" JSON and picks out Kite Launcher's installer.</summary>
public static partial class GitHubRelease
{
    /// <summary>Returns the update if the release is newer than <paramref name="current"/> and carries an installer.</summary>
    public static UpdateInfo? Parse(JsonDocument doc, Version current, string fallbackPage)
    {
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        if (version <= current) return null;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        if (!root.TryGetProperty("assets", out var assets)) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!SetupAssetName().IsMatch(name)) continue;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
                digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d[7..].ToLowerInvariant();
            return new UpdateInfo(
                version,
                tag,
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var page) ? page.GetString() ?? fallbackPage : fallbackPage,
                name,
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                sha);
        }
        return null;
    }

    [GeneratedRegex(@"^KiteLauncherSetup-[\d.]+\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex SetupAssetName();
}
