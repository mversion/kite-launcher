using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Kite.App.Services;
using Kite.App.Settings;
using Kite.App.Skins;
using Kite.Core.Settings;
using Kite.LaunchyImport;
using Kite.Plugins.Builtin;
using Microsoft.Win32;

namespace Kite.App;

public partial class App : Application, ISettingsHost
{
    private const string MutexName = @"Local\Kite.Launcher.SingleInstance";
    private const string ShowEventName = @"Local\Kite.Launcher.Show";
    private const string SettingsEventName = @"Local\Kite.Launcher.Settings";
    /// <summary>Signalled by "Kite.exe --exit" (used by the installer/uninstaller to close Kite cleanly).</summary>
    private const string ExitEventName = @"Local\Kite.Launcher.Exit";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _settingsEvent;
    private EventWaitHandle? _exitEvent;
    private KiteServices _services = null!;
    private LauncherWindow _window = null!;
    private SkinManager _skins = null!;
    private HotkeyService _hotkey = null!;
    private TrayIcon _tray = null!;
    private FileSystemWatcher? _settingsWatcher;
    private DispatcherTimer? _settingsDebounce;
    private string? _lastSettingsText;
    private SettingsWindow? _settingsWindow;
    private bool _hotkeySuspended;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, MutexName, out var isFirst);
        if (isFirst && e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(); // nothing running to close
            return;
        }
        if (!isFirst)
        {
            // Already running: ask the existing instance to show itself (or its settings with --settings).
            var wantsSettings = e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
            var wantsExit = e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase));
            var signal = wantsExit ? ExitEventName : wantsSettings ? SettingsEventName : ShowEventName;
            try { EventWaitHandle.OpenExisting(signal).Set(); } catch { /* it may be starting up */ }
            Shutdown();
            return;
        }

        var paths = KitePaths.ForCurrentProcess();
        Logger.Init(paths.LogFile);
        Logger.Log($"Kite starting (data: {paths.DataDirectory}{(paths.IsPortable ? ", portable" : "")})");
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Log($"Unhandled: {args.Exception}");
            args.Handled = true;
        };
        // Background-thread crashes bypass the dispatcher handler; log them before the process dies.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Log($"FATAL (terminating={args.IsTerminating}): {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Log($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        _services = new KiteServices(paths, Dispatcher);
        var firstRunImport = TryFirstRunImport();
        ApplySetupChoices();

        RegisterPlugins();
        _services.Plugins.InitAll();

        var sw = Stopwatch.StartNew();
        var cached = _services.Catalog.LoadCache();
        Logger.Log(cached ? $"Catalog cache loaded: {_services.Catalog.Current.Count} items in {sw.ElapsedMilliseconds} ms" : "No catalog cache yet");

        // Software rendering keeps the GPU driver out of the process (tens of MB) - plenty fast for a launcher.
        if (_services.Settings.Current.General.SoftwareRendering)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        _skins = new SkinManager(paths, Resources, Dispatcher);
        _skins.Load(_services.Settings.Current.General.Skin);

        _window = new LauncherWindow(_services, new LauncherCommands
        {
            RebuildCatalog = () => _ = _services.Catalog.RebuildAsync(),
            ReloadSkin = () => _skins.Reload(),
            OpenOptions = () => OpenSettingsWindow(),
            Exit = ExitApp,
            ListSkins = () => _skins.Available().Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase),
            SelectSkin = SelectSkin,
        });
        _skins.Applied += _ => _window.UpdateResultsMaxHeight();
        _window.Hidden += () => ScheduleTrim("launcher hidden");
        _window.Shown += CancelTrim;
        _services.Catalog.RebuildingChanged += busy => { if (!busy) Dispatcher.BeginInvoke(() => ScheduleTrim("catalog rebuilt")); };
        var hwnd = _window.EnsureHandle();
        _window.ApplySettings();

        _hotkey = new HotkeyService(hwnd);
        _hotkey.Pressed += () => _window.Toggle();

        _tray = new TrayIcon(new TrayActions
        {
            Show = () => _window.ShowLauncher(),
            OpenSettings = () => OpenSettingsWindow(),
            Rescan = () => _ = _services.Catalog.RebuildAsync(),
            ImportLaunchy = ImportLaunchyInteractive,
            OpenDataFolder = () => OpenFolder(paths.DataDirectory),
            OpenSkinsFolder = () => OpenFolder(_skins.CurrentFolder ?? paths.SkinRoots.Last()),
            ListSkins = () => _skins.Available().Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase),
            CurrentSkin = () => _services.Settings.Current.General.Skin,
            SelectSkin = SelectSkin,
            Exit = ExitApp,
        });

        ApplyRuntimeSettings(showHotkeyErrors: true);
        WatchSettingsFile();
        ListenForSecondInstance();

        _services.Catalog.Changed += snapshot => Dispatcher.BeginInvoke(() => _tray.SetTooltip($"Kite · {snapshot.Count} items"));
        _ = _services.Catalog.RebuildAsync();
        _services.Catalog.StartAutoRefresh();
        Dispatcher.BeginInvoke(_window.Prewarm, DispatcherPriority.ApplicationIdle);

        if (firstRunImport is not null)
            _tray.Notify("Imported your Launchy setup", firstRunImport);
        if (e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
            OpenSettingsWindow();
    }

    private void RegisterPlugins()
    {
        foreach (var plugin in BuiltinPlugins.CreateAll()) _services.Plugins.Register(plugin);
        _services.Plugins.Register(new KiteCommandsPlugin(new Dictionary<string, (string, Action)>
        {
            ["Kite Settings"] = ("Options: hotkey, skins, folders, plugins, shortcuts", () => Dispatcher.BeginInvoke(() => OpenSettingsWindow())),
            ["Kite Rescan Catalog"] = ("Re-index all directories and apps", () => _ = _services.Catalog.RebuildAsync()),
            ["Kite Skins Folder"] = ("Open the active skin's folder", () => Dispatcher.BeginInvoke(() => OpenFolder(_skins.CurrentFolder ?? _services.Paths.AppDirectory))),
            ["Kite Data Folder"] = ("Open where settings, history and logs live", () => OpenFolder(_services.Paths.DataDirectory)),
            ["Kite Exit"] = ("Quit Kite", () => Dispatcher.BeginInvoke(ExitApp)),
        }));
        // Third-party .NET plugins: <exe>\plugins\<name>\*.dll and <data>\plugins\<name>\*.dll
        _services.Plugins.LoadFrom(Path.Combine(_services.Paths.AppDirectory, "plugins"));
        if (!_services.Paths.IsPortable)
            _services.Plugins.LoadFrom(Path.Combine(_services.Paths.DataDirectory, "plugins"));
    }

    /// <summary>
    /// The installer leaves its choices (e.g. start with Windows) in setup.json next to the
    /// settings; apply them once - after the first-run Launchy import - and remove the file.
    /// </summary>
    private void ApplySetupChoices()
    {
        var file = Path.Combine(_services.Paths.DataDirectory, "setup.json");
        if (!File.Exists(file)) return;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file));
            if (node?["startWithWindows"]?.GetValue<bool>() is { } autostart)
            {
                _services.Settings.Current.General.StartWithWindows = autostart;
                _services.Settings.Save();
            }
            Logger.Log("Applied installer choices from setup.json");
        }
        catch (Exception ex) { Logger.Log($"setup.json ignored: {ex.Message}"); }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    /// <summary>On the very first run, bring over the user's Launchy configuration (read-only).</summary>
    private string? TryFirstRunImport()
    {
        if (_services.Settings.ExistedOnLoad || !File.Exists(LaunchyImporter.DefaultIniPath)) return null;
        try
        {
            var result = LaunchyImporter.Import(LaunchyImporter.DefaultIniPath);
            LaunchyImporter.Apply(result, _services.Settings, _services.History);
            _services.SaveHistoryNow();
            foreach (var w in result.Warnings) Logger.Log($"Import warning: {w}");
            Logger.Log($"Imported from Launchy: {result.Summary}");
            return result.Summary;
        }
        catch (Exception ex)
        {
            Logger.Log($"Launchy import failed: {ex}");
            return null;
        }
    }

    private void ImportLaunchyInteractive()
    {
        var path = LaunchyImporter.DefaultIniPath;
        if (!File.Exists(path))
        {
            var dlg = new OpenFileDialog { Title = "Locate launchy.ini", Filter = "launchy.ini|launchy.ini|INI files|*.ini" };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        var answer = MessageBox.Show(
            $"Import from\n{path}\n\nThis replaces Kite's directories, hotkey, skin and plugin settings, and adds Launchy's learned shortcuts. Launchy's own files are not changed.",
            "Import Launchy settings", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            var result = LaunchyImporter.Import(path);
            LaunchyImporter.Apply(result, _services.Settings, _services.History);
            _services.SaveHistoryNow();
            ReloadAll();
            _tray.Notify("Launchy settings imported", result.Summary);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed: {ex.Message}", "Kite", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Applies settings that can change at runtime (hotkey, tray, autostart, layout).</summary>
    private void ApplyRuntimeSettings(bool showHotkeyErrors)
    {
        var g = _services.Settings.Current.General;
        if (!_hotkey.Register(g.Hotkey, out var error))
        {
            Logger.Log($"Hotkey: {error}");
            if (showHotkeyErrors)
                _tray.Notify("Kite hotkey unavailable", $"{error}. Change it in Kite's settings (Ctrl+, or right-click the launcher), or click the tray icon to open Kite.", NotifyKind.Warning);
        }
        _hotkeyError = error;
        ApplyRuntime();
    }

    private string? _hotkeyError;

    // ---------------------------------------------------------------- memory

    private DispatcherTimer? _collectTimer;
    private DispatcherTimer? _trimTimer;
    private string _trimReason = "";

    /// <summary>How long Kite must sit hidden before its working set is released (re-opening then costs a little paging).</summary>
    private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromMinutes(2);

    private bool IsIdle => !_window.IsVisible && _settingsWindow is null && !_services.Catalog.IsRebuilding;

    /// <summary>
    /// Two-stage memory release once Kite is idle in the tray: shortly after hiding, freed .NET
    /// memory goes back to Windows (re-opening stays instant); after a couple of idle minutes the
    /// working set is trimmed too, so Kite takes only a few MB while you're not using it.
    /// </summary>
    private void ScheduleTrim(string reason)
    {
        if (!_services.Settings.Current.General.ReleaseMemoryWhenHidden) return;
        _trimReason = reason;
        _collectTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.ApplicationIdle, (_, _) =>
        {
            _collectTimer!.Stop();
            if (IsIdle) MemoryTrimmer.Collect();
        }, Dispatcher);
        _trimTimer ??= new DispatcherTimer(IdleTrimDelay, DispatcherPriority.ApplicationIdle, (_, _) =>
        {
            _trimTimer!.Stop();
            if (IsIdle) MemoryTrimmer.Trim(_trimReason);
        }, Dispatcher);
        _collectTimer.Stop();
        _collectTimer.Start();
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    private void CancelTrim()
    {
        _collectTimer?.Stop();
        _trimTimer?.Stop();
    }

    // ---------------------------------------------------------------- settings window (ISettingsHost)

    public KiteServices Services => _services;

    private void OpenSettingsWindow(string? section = null)
    {
        _window.HideLauncher();
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; ScheduleTrim("settings closed"); };
            _settingsWindow.Show();
        }
        if (section is not null) _settingsWindow.ShowSection(section);
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public Kite.Core.Skins.SkinLibrary SkinLibrary => _skins.Library;

    public void ApplySkin(string name) => SelectSkin(name);

    public void ReloadSkin() => _skins.Reload();

    public string? ApplyHotkey(string text)
    {
        var ok = _hotkey.Register(text, out var error);
        _hotkeyError = ok ? null : error;
        _hotkeySuspended = false;
        ApplyRuntime();
        return error;
    }

    public void SuspendHotkey(bool suspend)
    {
        if (suspend == _hotkeySuspended) return;
        _hotkeySuspended = suspend;
        if (suspend) _hotkey.Unregister();
        else
        {
            _hotkey.Register(_services.Settings.Current.General.Hotkey, out var error);
            _hotkeyError = error;
            ApplyRuntime();
        }
    }

    public void ApplyRuntime()
    {
        var g = _services.Settings.Current.General;
        _tray.Visible = g.ShowTrayIcon || _hotkeyError is not null; // never leave the user with no way in
        _services.History.MaxPrefixes = Math.Max(50, g.MaxHistoryItems);
        _window.ApplySettings();
        _services.Catalog.StartAutoRefresh();
        ApplyAutostart(g.StartWithWindows);
    }

    public void PluginsChanged()
    {
        _services.Plugins.InitAll();
        _services.Catalog.RefreshPluginItems();
    }

    public void Rescan() => _ = _services.Catalog.RebuildAsync();

    public void ImportLaunchy() => ImportLaunchyInteractive();

    public void OpenSettingsJson() => OpenSettingsFile();

    void ISettingsHost.OpenFolder(string path) => OpenFolder(path);

    public void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch { Process.Start("notepad.exe", $"\"{path}\"")?.Dispose(); }
    }

    private void ReloadAll()
    {
        // The settings window edits the live settings object, which is about to be replaced.
        _settingsWindow?.Close();
        _services.Settings.Load();
        _skins.Load(_services.Settings.Current.General.Skin);
        _services.Plugins.InitAll();
        ApplyRuntimeSettings(showHotkeyErrors: true);
        _services.Catalog.StartAutoRefresh();
        _ = _services.Catalog.RebuildAsync();
    }

    private void SelectSkin(string name)
    {
        _services.Settings.Current.General.Skin = name;
        _services.Settings.Save();
        _skins.Load(name);
    }

    // ---------------------------------------------------------------- settings.json live reload

    private void WatchSettingsFile()
    {
        var file = _services.Paths.SettingsFile;
        if (!File.Exists(file)) _services.Settings.Save();
        _lastSettingsText = SafeRead(file);
        _services.Settings.Saved += () => _lastSettingsText = SafeRead(file);

        _settingsWatcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _settingsDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, (_, _) =>
            {
                _settingsDebounce!.Stop();
                var text = SafeRead(file);
                if (text is null || text == _lastSettingsText) return; // our own write
                _lastSettingsText = text;
                Logger.Log("settings.json changed on disk; reloading");
                try { ReloadAll(); }
                catch (Exception ex)
                {
                    Logger.Log($"Settings reload failed: {ex.Message}");
                    _tray.Notify("settings.json has an error", ex.Message, NotifyKind.Error);
                }
            }, Dispatcher);
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        });
        _settingsWatcher.Changed += changed;
        _settingsWatcher.Created += changed;
        _settingsWatcher.Renamed += (s, e) => changed(s, e);
    }

    private static string? SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
    }

    private void OpenSettingsFile()
    {
        var file = _services.Paths.SettingsFile;
        if (!File.Exists(file)) _services.Settings.Save();
        _window.HideLauncher();
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose(); }
        catch { Process.Start("notepad.exe", $"\"{file}\"")?.Dispose(); }
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }

    private static void ApplyAutostart(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null) return;
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var value = $"\"{exe}\"";
            var existing = key.GetValue("Kite") as string;
            if (enabled && existing != value) key.SetValue("Kite", value);
            else if (!enabled && existing == value) key.DeleteValue("Kite", throwOnMissingValue: false);
        }
        catch (Exception ex) { Logger.Log($"Autostart update failed: {ex.Message}"); }
    }

    private void ListenForSecondInstance()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsEventName);
        var thread = new Thread(() =>
        {
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            var handles = new WaitHandle[] { _showEvent, _settingsEvent, _exitEvent };
            while (true)
            {
                var which = WaitHandle.WaitAny(handles);
                if (which == 0) Dispatcher.BeginInvoke(() => _window.ShowLauncher());
                else if (which == 1) Dispatcher.BeginInvoke(() => OpenSettingsWindow());
                else { Dispatcher.BeginInvoke(ExitApp); return; }
            }
        }) { IsBackground = true, Name = "Kite second-instance listener" };
        thread.Start();
    }

    private void ExitApp()
    {
        Logger.Log("Kite exiting");
        _window.IsExiting = true;
        _hotkey.Dispose();
        _tray.Dispose();
        _settingsWatcher?.Dispose();
        _skins.Dispose();
        _services.Dispose();
        _window.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
