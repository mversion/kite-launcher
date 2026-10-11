using System.Collections.Concurrent;
using System.Diagnostics;
using Kite.Core.Plugins;
using Kite.Core.Settings;
using Kite.Plugins;

namespace Kite.Core.Catalog;

/// <summary>Result of scanning one source during the last rebuild.</summary>
public sealed record ScanStat(int Items, long Milliseconds, string? Error = null);

/// <summary>Builds, caches and periodically refreshes the catalog.</summary>
public sealed class CatalogService : IDisposable
{
    public const string AppsFolderStatKey = "shell:AppsFolder";

    private readonly SettingsStore _settings;
    private readonly PluginHost _plugins;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private Timer? _timer;
    private volatile CatalogSnapshot _current = CatalogSnapshot.Empty;
    /// <summary>Directory + AppsFolder items from the last scan, kept so plugin entries can be refreshed without rescanning disks.</summary>
    private List<CatItem> _fileItems = [];

    public CatalogService(SettingsStore settings, PluginHost plugins, Action<string> log)
    {
        _settings = settings;
        _plugins = plugins;
        _log = log;
    }

    public CatalogSnapshot Current => _current;
    public event Action<CatalogSnapshot>? Changed;
    /// <summary>Raised with true when a rebuild starts and false when it ends (drives the spinner).</summary>
    public event Action<bool>? RebuildingChanged;
    public bool IsRebuilding { get; private set; }
    public DateTime? LastRebuilt { get; private set; }
    public long LastRebuildMilliseconds { get; private set; }

    /// <summary>Per-source statistics from the last rebuild, keyed by the configured directory path.</summary>
    public ConcurrentDictionary<string, ScanStat> LastScan { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads the on-disk cache for an instant start; returns false if there is none.</summary>
    public bool LoadCache()
    {
        try
        {
            var items = Json.Load<List<CatItem>>(_settings.Paths.CatalogCacheFile);
            if (items is null) return false;
            _fileItems = items.Where(i => i.PluginId is null).ToList();
            Publish(new CatalogSnapshot(_fileItems.Concat(_plugins.CollectCatalog())));
            return true;
        }
        catch (Exception ex)
        {
            _log($"Catalog cache unreadable: {ex.Message}");
            return false;
        }
    }

    public void StartAutoRefresh()
    {
        var minutes = _settings.Current.General.RescanMinutes;
        _timer?.Dispose();
        if (minutes <= 0) return;
        var period = TimeSpan.FromMinutes(minutes);
        _timer = new Timer(_ => _ = RebuildAsync(), null, period, period);
    }

    /// <summary>Re-collects plugin catalog entries (Weby sites, Runner commands, ...) without touching the disk scan.</summary>
    public void RefreshPluginItems() => Publish(new CatalogSnapshot(_fileItems.Concat(_plugins.CollectCatalog())));

    public async Task RebuildAsync()
    {
        if (!await _rebuildLock.WaitAsync(0)) return; // a rebuild is already running
        IsRebuilding = true;
        RebuildingChanged?.Invoke(true);
        try
        {
            var sw = Stopwatch.StartNew();
            var fileItems = await Task.Run(() => ScanFiles(_settings.Current));
            _fileItems = fileItems;
            var snapshot = new CatalogSnapshot(fileItems.Concat(_plugins.CollectCatalog()));
            LastRebuilt = DateTime.Now;
            LastRebuildMilliseconds = sw.ElapsedMilliseconds;
            Publish(snapshot);
            Json.Save(_settings.Paths.CatalogCacheFile, fileItems);
            _log($"Catalog rebuilt: {snapshot.Count} items in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            _log($"Catalog rebuild failed: {ex}");
        }
        finally
        {
            IsRebuilding = false;
            _rebuildLock.Release();
            RebuildingChanged?.Invoke(false);
        }
    }

    private List<CatItem> ScanFiles(AppSettings settings)
    {
        var baseDir = _settings.Paths.AppDirectory;
        var hidden = settings.General.ShowHiddenFiles;
        LastScan.Clear();

        var items = settings.Directories
            .AsParallel()
            .AsOrdered()
            .SelectMany(d =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var found = DirectoryScanner.Scan(d, baseDir, hidden);
                    LastScan[d.Path] = new ScanStat(found.Count, sw.ElapsedMilliseconds);
                    _log($"  {d.Path}: {found.Count} items in {sw.ElapsedMilliseconds} ms");
                    return found;
                }
                catch (Exception ex)
                {
                    LastScan[d.Path] = new ScanStat(0, sw.ElapsedMilliseconds, ex.Message);
                    _log($"Scan failed for {d.Path}: {ex.Message}");
                    return [];
                }
            })
            .ToList();

        var names = new HashSet<string>(items.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);

        if (settings.IndexAppsFolder)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Only add apps that aren't already covered by a shortcut with the same name.
                var added = 0;
                foreach (var app in AppsFolderSource.Enumerate())
                    if (names.Add(app.Name))
                    {
                        items.Add(app);
                        added++;
                    }
                LastScan[AppsFolderStatKey] = new ScanStat(added, sw.ElapsedMilliseconds);
                _log($"  shell:AppsFolder: {added} additional apps in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                LastScan[AppsFolderStatKey] = new ScanStat(0, sw.ElapsedMilliseconds, ex.Message);
                _log($"AppsFolder enumeration failed: {ex.Message}");
            }
        }
        return items;
    }

    private void Publish(CatalogSnapshot snapshot)
    {
        _current = snapshot;
        Changed?.Invoke(snapshot);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _rebuildLock.Dispose();
    }
}
