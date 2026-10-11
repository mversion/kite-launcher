namespace Kite.Plugins;

/// <summary>
/// A launchable entry. Mirrors Launchy's CatItem: a display name, a target
/// (file path, URI, AUMID or plugin-defined key) and the owning plugin.
/// </summary>
public sealed record CatItem
{
    /// <summary>Text shown to the user and matched against the query.</summary>
    public required string Name { get; init; }

    /// <summary>What gets launched: a file path, "shell:AppsFolder\&lt;AUMID&gt;", a URI, or a plugin key.</summary>
    public required string Target { get; init; }

    /// <summary>Owning plugin id; null means a plain catalog item launched by the core.</summary>
    public string? PluginId { get; init; }

    /// <summary>Secondary line (usually the path). Falls back to <see cref="Target"/>.</summary>
    public string? Description { get; init; }

    /// <summary>Short right-aligned extra (e.g. a file's date modified and size). Optional.</summary>
    public string? Detail { get; init; }

    /// <summary>Optional icon source: a file path whose icon is used, or a .png/.ico file.</summary>
    public string? IconPath { get; init; }

    /// <summary>Plugin-private payload (e.g. a Weby URL template). Not persisted for plugin results.</summary>
    public string? Data { get; init; }

    /// <summary>
    /// Placement hint for plugin results: positive scores are listed above catalog
    /// matches (highest first), zero or negative below them. Ignored for catalog items.
    /// </summary>
    public int Score { get; init; }

    /// <summary>Unique key used for history/usage tracking.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => PluginId is null ? Target : $"{PluginId}:{Target}";
}
