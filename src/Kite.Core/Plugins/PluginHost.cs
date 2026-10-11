using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Kite.Core.Launching;
using Kite.Core.Settings;
using Kite.Plugins;

namespace Kite.Core.Plugins;

/// <summary>What the UI layer provides to plugins (clipboard, rebuild, logging).</summary>
public interface IHostServices
{
    void CopyToClipboard(string text);
    void RequestCatalogRebuild();
    void Log(string message);
}

/// <summary>Owns plugin instances and fans queries out to them.</summary>
public sealed class PluginHost
{
    private readonly List<IKitePlugin> _plugins = [];
    private readonly SettingsStore _settings;
    private readonly IHostServices _host;

    public PluginHost(SettingsStore settings, IHostServices host)
    {
        _settings = settings;
        _host = host;
    }

    public IReadOnlyList<IKitePlugin> All => _plugins;
    public IEnumerable<IKitePlugin> Enabled => _plugins.Where(p => !_settings.Current.DisabledPlugins.Contains(p.Id));

    public IKitePlugin? Get(string? id) => id is null ? null : Enabled.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public void Register(IKitePlugin plugin)
    {
        if (_plugins.Any(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _host.Log($"Duplicate plugin id '{plugin.Id}' ignored");
            return;
        }
        _plugins.Add(plugin);
    }

    /// <summary>Loads third-party plugins from &lt;dir&gt;\&lt;name&gt;\*.dll, each in its own load context.</summary>
    public void LoadFrom(string pluginsRoot)
    {
        if (!Directory.Exists(pluginsRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(pluginsRoot))
        {
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                try
                {
                    var context = new PluginLoadContext(dll);
                    var assembly = context.LoadFromAssemblyPath(dll);
                    foreach (var type in assembly.GetExportedTypes().Where(t => typeof(IKitePlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
                        Register((IKitePlugin)Activator.CreateInstance(type)!);
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
                {
                    // Not a .NET plugin assembly (e.g. a native dependency); skip quietly.
                }
                catch (Exception ex)
                {
                    _host.Log($"Failed to load plugin {dll}: {ex.Message}");
                }
            }
        }
    }

    public void InitAll()
    {
        foreach (var p in _plugins)
        {
            try { p.Init(new PluginContext(p.Id, _settings, _host)); }
            catch (Exception ex) { _host.Log($"Plugin {p.Id} failed to init: {ex.Message}"); }
        }
    }

    public IEnumerable<CatItem> CollectCatalog()
    {
        var items = new List<CatItem>();
        foreach (var p in Enabled)
        {
            try
            {
                foreach (var item in p.GetCatalog())
                    items.Add(item.PluginId is null ? item with { PluginId = p.Id } : item);
            }
            catch (Exception ex) { _host.Log($"Plugin {p.Id} catalog failed: {ex.Message}"); }
        }
        return items;
    }

    public async Task<IReadOnlyList<CatItem>> QueryAsync(Query query, CancellationToken ct)
    {
        var targets = query.Owner?.PluginId is { } ownerId
            ? Enabled.Where(p => string.Equals(p.Id, ownerId, StringComparison.OrdinalIgnoreCase)).ToList()
            : Enabled.ToList();
        if (query.Owner is not null && query.Owner.PluginId is null) return [];

        var tasks = targets.Select(async p =>
        {
            try
            {
                var results = await p.GetResults(query, ct).ConfigureAwait(false);
                return results.Select(r => r.PluginId is null ? r with { PluginId = p.Id } : r).ToList();
            }
            catch (OperationCanceledException) { return []; }
            catch (Exception ex)
            {
                _host.Log($"Plugin {p.Id} query failed: {ex.Message}");
                return new List<CatItem>();
            }
        });
        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return all.SelectMany(r => r).ToList();
    }

    public bool Launch(CatItem item, Query query, LaunchMode mode)
    {
        var plugin = Get(item.PluginId);
        if (plugin is null) return false;
        return plugin.Launch(item, query, mode);
    }

    public void NotifyShow()
    {
        foreach (var p in Enabled) { try { p.OnShow(); } catch { /* plugin bug must not break the UI */ } }
    }

    public void NotifyHide()
    {
        foreach (var p in Enabled) { try { p.OnHide(); } catch { /* plugin bug must not break the UI */ } }
    }

    private sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Share the plugin API with the host so interface types match.
            if (name.Name == typeof(IKitePlugin).Assembly.GetName().Name) return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

internal sealed class PluginContext(string pluginId, SettingsStore settings, IHostServices host) : IPluginContext
{
    public JsonObject Settings
    {
        get
        {
            var all = settings.Current.Plugins;
            if (!all.TryGetValue(pluginId, out var section))
                all[pluginId] = section = new JsonObject();
            return section;
        }
    }

    public void SaveSettings() => settings.Save();

    public string DataDirectory
    {
        get
        {
            var dir = Path.Combine(settings.Paths.PluginDataRoot, pluginId);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public string AppDirectory => settings.Paths.AppDirectory;

    public void RunProgram(string file, string? arguments = null, bool runAsAdmin = false) =>
        ShellLauncher.Launch(file, arguments, runAsAdmin ? LaunchMode.RunAsAdmin : LaunchMode.Default);

    public void OpenUri(string uri) => ShellLauncher.OpenUri(uri);
    public void CopyToClipboard(string text) => host.CopyToClipboard(text);
    public void Log(string message) => host.Log($"[{pluginId}] {message}");
    public void RequestCatalogRebuild() => host.RequestCatalogRebuild();
}
