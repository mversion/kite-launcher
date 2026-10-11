using System.Text.Json.Nodes;

namespace Kite.Plugins;

/// <summary>
/// Plugin contract. Each member corresponds to a Launchy 2.x message so legacy
/// plugins can be adapted 1:1:
/// <list type="bullet">
/// <item><see cref="Id"/>/<see cref="Name"/> - MSG_GET_ID / MSG_GET_NAME</item>
/// <item><see cref="Init"/> - MSG_INIT</item>
/// <item><see cref="GetCatalog"/> - MSG_GET_CATALOG</item>
/// <item><see cref="GetResults"/> - MSG_GET_LABELS + MSG_GET_RESULTS</item>
/// <item><see cref="Launch"/> - MSG_LAUNCH_ITEM</item>
/// <item><see cref="OnShow"/>/<see cref="OnHide"/> - MSG_LAUNCHY_SHOW / MSG_LAUNCHY_HIDE</item>
/// </list>
/// </summary>
public interface IKitePlugin
{
    string Id { get; }
    string Name { get; }
    string Description => "";

    void Init(IPluginContext context);

    /// <summary>Static entries merged into the main catalog (rebuilt on rescan).</summary>
    IEnumerable<CatItem> GetCatalog() => Array.Empty<CatItem>();

    /// <summary>
    /// Dynamic results for a query. Called on a background thread for every keystroke;
    /// honour <paramref name="ct"/>. Return nothing when the query isn't relevant.
    /// </summary>
    Task<IReadOnlyList<CatItem>> GetResults(Query query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CatItem>>(Array.Empty<CatItem>());

    /// <summary>Launch an item owned by this plugin. Return false if not handled.</summary>
    bool Launch(CatItem item, Query query, LaunchMode mode);

    void OnShow() { }
    void OnHide() { }
}

/// <summary>Services the host provides to plugins.</summary>
public interface IPluginContext
{
    /// <summary>The plugin's own settings section (persisted in settings.json). Mutate and call <see cref="SaveSettings"/>.</summary>
    JsonObject Settings { get; }
    void SaveSettings();

    /// <summary>A writable per-plugin folder for caches and data.</summary>
    string DataDirectory { get; }

    /// <summary>Directory containing Kite.exe.</summary>
    string AppDirectory { get; }

    void RunProgram(string file, string? arguments = null, bool runAsAdmin = false);
    void OpenUri(string uri);
    void CopyToClipboard(string text);
    void Log(string message);

    /// <summary>Ask the host to rebuild the catalog (e.g. after plugin settings changed).</summary>
    void RequestCatalogRebuild();
}
