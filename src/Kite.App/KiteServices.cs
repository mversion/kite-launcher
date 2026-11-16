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

    public void CopySecretToClipboard(string text, TimeSpan clearAfter) => _dispatcher.Invoke(() =>
    {
        // These formats tell Windows not to keep the copy in clipboard history (Win+V) or sync it to
        // other devices, and clipboard managers that honour the convention to skip it.
        var data = new DataObject(DataFormats.UnicodeText, text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream([0, 0, 0, 0]));
        data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream([0, 0, 0, 0]));
        data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream([0, 0, 0, 0]));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(data, copy: true); break; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); }
        }

        var clear = new DispatcherTimer { Interval = clearAfter };
        clear.Tick += (_, _) =>
        {
            clear.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear(); }
            catch (System.Runtime.InteropServices.COMException) { /* busy: leave it */ }
        };
        clear.Start();
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
