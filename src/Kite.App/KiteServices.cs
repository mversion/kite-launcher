using System.Windows;
using System.Windows.Threading;
using Kite.App.Services;
using Kite.Core.Catalog;
using Kite.Core.Launching;
using Kite.Core.Plugins;
using Kite.Core.Search;
using Kite.Core.Settings;

namespace Kite.App;

/// <summary>Everything the UI needs, wired together once at startup.</summary>
public sealed class KiteServices : IHostServices, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Timer _historySaveTimer;

    public KiteServices(KitePaths paths, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Paths = paths;
        Settings = new SettingsStore(paths);
        Settings.Load();
        History.MaxPrefixes = Math.Max(50, Settings.Current.General.MaxHistoryItems);
        History.Load(paths.HistoryFile);
        Plugins = new PluginHost(Settings, this);
        Catalog = new CatalogService(Settings, Plugins, Logger.Log);
        Search = new SearchEngine(() => Catalog.Current, History, Plugins);
        Launcher = new LaunchService(Plugins, History, key => Catalog.Current.ByKey.ContainsKey(key), SaveHistorySoon);
        _historySaveTimer = new Timer(_ => SaveHistoryNow());
    }

    public KitePaths Paths { get; }
    public SettingsStore Settings { get; }
    public History History { get; } = new();
    public PluginHost Plugins { get; }
    public CatalogService Catalog { get; }
    public SearchEngine Search { get; }
    public LaunchService Launcher { get; }
    public IconService Icons { get; } = new();

    public void SaveHistorySoon() => _historySaveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);

    public void SaveHistoryNow()
    {
        try { History.Save(Paths.HistoryFile); }
        catch (Exception ex) { Logger.Log($"Saving history failed: {ex.Message}"); }
    }

    public void CopyToClipboard(string text) => _dispatcher.Invoke(() =>
    {
        // The clipboard can be briefly locked by another process.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); }
        }
        Logger.Log("Clipboard busy; copy failed");
    });

    public void RequestCatalogRebuild() => _ = Catalog.RebuildAsync();

    public void Log(string message) => Logger.Log(message);

    public void Dispose()
    {
        _historySaveTimer.Dispose();
        SaveHistoryNow();
        Catalog.Dispose();
        Icons.Dispose();
    }
}
