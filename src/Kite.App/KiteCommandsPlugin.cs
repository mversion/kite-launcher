using Kite.Plugins;

namespace Kite.App;

/// <summary>Kite's own commands in the catalog ("kite settings", "kite rescan", ...), like Launchy's Controly entries.</summary>
public sealed class KiteCommandsPlugin(IReadOnlyDictionary<string, (string Description, Action Run)> commands) : IKitePlugin
{
    public string Id => "kite";
    public string Name => "Kite Launcher";
    public string Description => "Kite's own commands";

    public void Init(IPluginContext context) { }

    public IEnumerable<CatItem> GetCatalog() =>
        commands.Select(c => new CatItem { Name = c.Key, Target = c.Key, Description = c.Value.Description });

    public bool Launch(CatItem item, Query query, LaunchMode mode)
    {
        if (!commands.TryGetValue(item.Target, out var command)) return false;
        command.Run();
        return true;
    }
}
