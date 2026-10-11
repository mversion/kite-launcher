using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Kite.App.Skins;
using Kite.Core.Catalog;
using Kite.Core.Settings;
using Kite.Core.Skins;
using Kite.Plugins;
using Kite.Plugins.Builtin.Runner;
using Kite.Plugins.Builtin.Weby;
using Microsoft.Win32;

namespace Kite.App.Settings;

[Flags]
internal enum Apply
{
    None = 0,
    Save = 1,
    /// <summary>Tray, topmost, autostart, launcher layout, rescan timer.</summary>
    Runtime = 2,
    /// <summary>Plugin settings: re-init plugins and refresh their catalog entries.</summary>
    Plugins = 4,
    /// <summary>Directory settings: needs a disk rescan.</summary>
    Catalog = 8,
    History = 16,
}

/// <summary>
/// Modern, searchable settings. Every option is a card; typing in the search box filters cards
/// across all sections instantly. Changes are saved and applied as you make them.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ISettingsHost _host;
    private readonly List<Section> _sections = [];
    private readonly DispatcherTimer _applyTimer;
    private readonly DispatcherTimer _toastTimer;
    private Apply _pending;
    private bool _catalogDirty;
    private Section? _current;

    private KiteServices Services => _host.Services;
    private AppSettings Cfg => Services.Settings.Current;

    private sealed class Section(string title, string glyph, string subtitle)
    {
        public string Title { get; } = title;
        public string Glyph { get; } = glyph;
        public string Subtitle { get; } = subtitle;
        public List<Entry> Entries { get; } = [];
        public ListBoxItem? NavItem { get; set; }
    }

    private sealed record Entry(string SearchText, FrameworkElement Card, Func<string>? DynamicSearchText = null)
    {
        public bool Matches(string[] tokens)
        {
            var text = DynamicSearchText is null ? SearchText : SearchText + " " + DynamicSearchText().ToLowerInvariant();
            return tokens.All(t => text.Contains(t, StringComparison.Ordinal));
        }
    }

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        VersionText.Text = "v" + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1");

        _applyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) => Flush(), Dispatcher);
        _applyTimer.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(3.5), DispatcherPriority.Normal, (_, _) =>
        {
            _toastTimer!.Stop();
            Toast.Visibility = Visibility.Collapsed;
        }, Dispatcher);
        _toastTimer.Stop();

        BuildGeneral();
        BuildAppearance();
        BuildCatalog();
        BuildPlugins();
        BuildShortcuts();
        BuildAbout();

        foreach (var s in _sections)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = s.Glyph, FontFamily = Icons, FontSize = 15, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = s.Title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            s.NavItem = new ListBoxItem { Content = row, Tag = s };
            Nav.Items.Add(s.NavItem);
        }
        Nav.SelectedIndex = 0;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        };
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            UpdateStatus();
            Dispatcher.BeginInvoke(ContentScroll.ScrollToTop, DispatcherPriority.ContextIdle);
        };
        Closed += (_, _) =>
        {
            Flush();
            _host.SuspendHotkey(false);
            if (_catalogDirty) _host.Rescan();
        };
        Services.Catalog.RebuildingChanged += OnRebuildingChanged;
        Closed += (_, _) => Services.Catalog.RebuildingChanged -= OnRebuildingChanged;
    }

    public void ShowSection(string title)
    {
        var s = _sections.FirstOrDefault(x => x.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (s?.NavItem is not null) Nav.SelectedItem = s.NavItem;
    }

    // =================================================================== sections

    private void BuildGeneral()
    {
        var s = AddSection("General", "\uE80F", "How Kite opens, where it appears and how it behaves.");
        var g = Cfg.General;

        Add(s, "Hotkey", "The key combination that shows or hides Kite. Click the box and press the new combination.",
            "shortcut keyboard key alt space launch open global", HotkeyEditor());

        Add(s, "Open on monitor", "Which screen the launcher appears on when you press the hotkey.",
            "display screen multi monitor active window mouse cursor primary",
            Combo([("Monitor with the active window", OpenOnMonitor.ActiveWindow), ("Monitor with the mouse pointer", OpenOnMonitor.Cursor), ("Primary monitor", OpenOnMonitor.Primary)],
                  g.OpenOn, v => g.OpenOn = v, Apply.Save));

        var centreH = Toggle(g.CenterHorizontally, v => g.CenterHorizontally = v, Apply.Save);
        var centreV = Toggle(g.CenterVertically, v => g.CenterVertically = v, Apply.Save);
        var recentre = new Button { Content = "Re-centre" };
        recentre.Click += (_, _) =>
        {
            g.PositionRelX = g.PositionRelY = null;
            centreH.IsChecked = centreV.IsChecked = true;
            Schedule(Apply.Save);
            ShowToast("Kite will open centred again");
        };
        Add(s, "Position", "Drag the launcher by its frame (or anywhere while holding Shift) to move it. The spot is remembered relative to the monitor, so it lands in the same place on any screen.",
            "move drag place location centre center position reset", recentre);
        Add(s, "Centre horizontally", "Always open in the middle of the screen, left to right. Turns off when you drag the launcher.", "center centre horizontal position",
            centreH);
        Add(s, "Centre vertically", "Always open at the same height (about a third of the way down). Turns off when you drag the launcher.", "center centre vertical position",
            centreV);
        Add(s, "Only drag while Shift is held", "Prevents accidental moves; Shift+drag still moves the launcher.", "drag move shift lock",
            Toggle(g.DragRequiresShift, v => g.DragRequiresShift = v, Apply.Save));
        Add(s, "Hide when Kite loses focus", "Close the launcher when you click somewhere else.", "focus hide close deactivate click away",
            Toggle(g.HideOnFocusLoss, v => g.HideOnFocusLoss = v, Apply.Save));
        Add(s, "Keep on top", "Show the launcher above all other windows.", "always on top topmost",
            Toggle(g.AlwaysOnTop, v => g.AlwaysOnTop = v, Apply.Runtime));
        Add(s, "Show tray icon", "The kite icon in the notification area (it reappears automatically if the hotkey can't be registered).",
            "tray notification area icon system", Toggle(g.ShowTrayIcon, v => g.ShowTrayIcon = v, Apply.Runtime));
        Add(s, "Start with Windows", "Launch Kite automatically when you sign in.", "startup boot login autostart run",
            Toggle(g.StartWithWindows, v => g.StartWithWindows = v, Apply.Runtime));

        Add(s, "Maximum results", "How many matches to list.", "results suggestions count number list",
            Number(g.MaxResults, 1, 50, v => g.MaxResults = v, Apply.Save));
        Add(s, "Visible rows", "Rows shown before the list scrolls.", "results rows height visible suggestions",
            Number(g.VisibleResults, 1, 20, v => g.VisibleResults = v, Apply.Runtime));
        Add(s, "Delay before results appear", "Launchy's \"auto open delay\". 0 shows matches instantly as you type.",
            "delay auto open wait results suggestions ms milliseconds",
            SliderEditor(g.ResultsDelayMs, 0, 2000, 50, v => g.ResultsDelayMs = v, Apply.Save, v => v == 0 ? "Instant" : $"{v} ms"));
        Add(s, "Learned shortcuts to keep", "Kite remembers what you launch for each thing you type. Older, unpinned ones are forgotten past this limit.",
            "history learned shortcuts aliases limit memory", Number(g.MaxHistoryItems, 50, 5000, v => g.MaxHistoryItems = v, Apply.Runtime));
        Add(s, "Release memory while hidden", "A moment after the launcher hides, Kite hands unused memory back to Windows so it stays small in the background. It still opens instantly.",
            "memory ram footprint trim working set performance resources", Toggle(g.ReleaseMemoryWhenHidden, v => g.ReleaseMemoryWhenHidden = v, Apply.Save));
        Add(s, "Software rendering", "Draw without the graphics card. Uses noticeably less memory and is plenty fast for a launcher; turn off if animations stutter. Takes effect after restarting Kite.",
            "gpu graphics hardware acceleration rendering memory performance driver", Toggle(g.SoftwareRendering, v => g.SoftwareRendering = v, Apply.Save));
    }

    private void BuildAppearance()
    {
        var s = AddSection("Appearance", "\uE790", "Skins, transparency and animation.");
        var g = Cfg.General;

        var newSkin = new Button { Content = "New skin…", Style = (Style)FindResource("S.AccentButton") };
        newSkin.Click += (_, _) => CreateSkin();
        var import = new Button { Content = "Import…", Margin = new Thickness(8, 0, 0, 0), ToolTip = "A Kite skin .zip, or a skin folder (pick its skin.json) - including original Launchy skins (pick style.qss)" };
        import.Click += (_, _) => ImportSkin();
        var openFolder = new Button { Content = "", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Open your skins folder", FontFamily = Icons };
        openFolder.Click += (_, _) => { Directory.CreateDirectory(_host.SkinLibrary.UserRoot); _host.OpenFolder(_host.SkinLibrary.UserRoot); };
        var reload = new Button { Content = "", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Reload the active skin from disk (Shift+F5)", FontFamily = Icons };
        reload.Click += (_, _) => { _host.ReloadSkin(); ShowToast("Skin reloaded"); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(newSkin);
        buttons.Children.Add(import);
        buttons.Children.Add(openFolder);
        buttons.Children.Add(reload);

        _gallery = new WrapPanel();
        RenderGallery();
        Add(s, "Skin", "Click a skin to use it. Hover a skin to edit, duplicate, rename, export or delete it. Built-in skins are read-only - duplicate one to make it your own.",
            "skin theme colour color look style dark light glass new create edit editor designer import export delete duplicate rename launchy qss zip",
            buttons, _gallery,
            dynamicSearch: () => string.Join(' ', _host.SkinLibrary.List().Select(x => x.Name)));

        Add(s, "Opacity", "How solid the launcher is.", "opacity transparency translucent alpha see through",
            SliderEditor(g.Opacity, 20, 100, 5, v => g.Opacity = v, Apply.Save, v => $"{v}%"));
        Add(s, "Fade in", "Animation when the launcher appears.", "fade animation appear show speed",
            SliderEditor(g.FadeInMs, 0, 300, 10, v => g.FadeInMs = v, Apply.Save, v => v == 0 ? "Off" : $"{v} ms"));
        Add(s, "Fade out", "Animation when the launcher hides.", "fade animation disappear hide speed",
            SliderEditor(g.FadeOutMs, 0, 300, 10, v => g.FadeOutMs = v, Apply.Save, v => v == 0 ? "Off" : $"{v} ms"));
    }

    private Border? _directoryList;

    private void BuildCatalog()
    {
        var s = AddSection("Catalog", "\uE8B7", "Where Kite looks for things to launch.");
        var g = Cfg.General;

        _directoryList = new Border();
        RenderDirectories();

        var add = new Button { Content = "Add folder…", Style = (Style)FindResource("S.AccentButton") };
        add.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Add a folder to the catalog" };
            if (dlg.ShowDialog(this) != true) return;
            Cfg.Directories.Add(new CatalogDirectory { Path = dlg.FolderName, Types = ["*.lnk"], IncludeExecutables = true });
            RenderDirectories();
            Schedule(Apply.Catalog);
        };
        var rescan = new Button { Content = "Rescan now", Margin = new Thickness(8, 0, 0, 0) };
        rescan.Click += (_, _) => { Flush(); _catalogDirty = false; _host.Rescan(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(add);
        buttons.Children.Add(rescan);

        Add(s, "Folders", "Each folder is scanned for the file types listed (e.g. *.lnk, *.exe, *.*), down to the given depth. Separate types with commas.",
            "directories folders paths scan index types extensions lnk exe depth subfolders", buttons, _directoryList,
            dynamicSearch: () => string.Join(' ', Cfg.Directories.Select(d => d.Path)));

        Add(s, "Microsoft Store and system apps", AppsFolderDescription(),
            "store uwp apps paint calculator windows apps appsfolder start menu all apps",
            Toggle(Cfg.IndexAppsFolder, v => Cfg.IndexAppsFolder = v, Apply.Catalog));
        Add(s, "Include hidden files", "Also index files and folders marked hidden.", "hidden files system",
            Toggle(g.ShowHiddenFiles, v => g.ShowHiddenFiles = v, Apply.Catalog));
        Add(s, "Rescan automatically", "Minutes between background rescans. 0 turns it off (use F5 in the launcher instead).",
            "rescan refresh update timer interval minutes automatic catalog",
            Number(g.RescanMinutes, 0, 1440, v => g.RescanMinutes = v, Apply.Runtime, "minutes"));
    }

    private string AppsFolderDescription()
    {
        var text = "Finds apps that have no shortcut file, such as Paint, Calculator and anything installed from the Microsoft Store.";
        if (Services.Catalog.LastScan.TryGetValue(CatalogService.AppsFolderStatKey, out var stat))
            text += $" Last scan added {stat.Items} apps.";
        return text;
    }

    private void RenderDirectories()
    {
        if (_directoryList is null) return;
        var list = new StackPanel();
        foreach (var dir in Cfg.Directories.ToList())
            list.Children.Add(DirectoryRow(dir));
        if (Cfg.Directories.Count == 0)
            list.Children.Add(new TextBlock { Text = "No folders yet - add one below.", Foreground = Sub, Margin = new Thickness(0, 4, 0, 0) });
        _directoryList.Child = list;
    }

    private FrameworkElement DirectoryRow(CatalogDirectory d)
    {
        var path = Input(d.Path, v => { d.Path = v; Schedule(Apply.Catalog); }, "Folder path, e.g. %AppData%\\Microsoft\\Windows\\Start Menu");
        var browse = IconButton("\uE8DA", "Browse…", () =>
        {
            var resolved = DirectoryScanner.ResolvePath(d.Path, Services.Paths.AppDirectory);
            var dlg = new OpenFolderDialog { Title = "Choose folder" };
            if (Directory.Exists(resolved)) dlg.InitialDirectory = resolved;
            if (dlg.ShowDialog(this) == true) path.Text = dlg.FolderName;
        });
        var remove = IconButton("\uE74D", "Remove folder", () =>
        {
            Cfg.Directories.Remove(d);
            RenderDirectories();
            Schedule(Apply.Catalog);
        });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(path);
        Grid.SetColumn(browse, 1);
        Grid.SetColumn(remove, 2);
        browse.Margin = new Thickness(4, 0, 0, 0);
        top.Children.Add(browse);
        top.Children.Add(remove);

        var types = Input(string.Join(", ", d.Types), v =>
        {
            d.Types = v.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Schedule(Apply.Catalog);
        }, "*.lnk, *.exe");
        types.Width = 170;

        var bottom = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(Label("Types"));
        bottom.Children.Add(types);
        bottom.Children.Add(Label("Depth", 18));
        bottom.Children.Add(Number(d.Depth, 0, 100, v => d.Depth = v, Apply.Catalog));
        var exes = Toggle(d.IncludeExecutables, v => d.IncludeExecutables = v, Apply.Catalog, "Executables");
        exes.Margin = new Thickness(18, 0, 0, 0);
        bottom.Children.Add(exes);
        var dirs = Toggle(d.IncludeDirectories, v => d.IncludeDirectories = v, Apply.Catalog, "Folders");
        dirs.Margin = new Thickness(18, 0, 0, 0);
        bottom.Children.Add(dirs);

        var stat = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0), Foreground = Faint };
        var resolvedPath = DirectoryScanner.ResolvePath(d.Path, Services.Paths.AppDirectory);
        if (d.Path.Length > 0 && !Directory.Exists(resolvedPath.Length == 2 ? resolvedPath + "\\" : resolvedPath))
        {
            stat.Text = "⚠ Folder not found";
            stat.Foreground = (Brush)FindResource("S.Danger");
        }
        else if (Services.Catalog.LastScan.TryGetValue(d.Path, out var st))
        {
            stat.Text = st.Error is null
                ? $"{st.Items:N0} items · scanned in {FormatDuration(st.Milliseconds)}"
                : "⚠ " + st.Error;
            if (st.Error is not null) stat.Foreground = (Brush)FindResource("S.Danger");
        }
        else stat.Text = "Not scanned yet";

        var panel = new StackPanel();
        panel.Children.Add(top);
        panel.Children.Add(bottom);
        panel.Children.Add(stat);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x23)),
            BorderBrush = (Brush)FindResource("S.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel,
        };
    }

    private void OnRebuildingChanged(bool busy) => Dispatcher.BeginInvoke(() =>
    {
        UpdateStatus();
        // Refresh item counts unless the user is typing in one of the folder rows.
        if (!busy && _directoryList is not null && !_directoryList.IsKeyboardFocusWithin) RenderDirectories();
    });

    private void BuildPlugins()
    {
        var s = AddSection("Plugins", "\uEA86", "Built-in and installed plugins and their options.");

        foreach (var p in Services.Plugins.All)
        {
            var plugin = p;
            Add(s, plugin.Name, string.IsNullOrEmpty(plugin.Description) ? $"Plugin id: {plugin.Id}" : plugin.Description,
                $"plugin enable disable {plugin.Id}",
                Toggle(!Cfg.DisabledPlugins.Contains(plugin.Id), v =>
                {
                    if (v) Cfg.DisabledPlugins.Remove(plugin.Id);
                    else Cfg.DisabledPlugins.Add(plugin.Id);
                }, Apply.Plugins));
        }

        Add(s, "Web searches", "Type a site's name, press Tab, then your search - e.g. \"google ⇥ kites\". Put %1 where the search text goes. The default site is offered for anything Kite can't find. E-mail (mailto:%1) remembers addresses you've used.",
            "weby web search sites google bing youtube url email mailto default", null, WebyEditor(),
            dynamicSearch: () => string.Join(' ', ReadWebSites().Select(w => w.Name)));

        Add(s, "Custom commands", "Runner commands: type the name, press Tab, then the input. $$ in the arguments marks where your input goes - e.g. cmd.exe with /K $$ runs \"cmd ⇥ dir\".",
            "runner commands custom programs arguments cmd $$ run", null, RunnerEditor(),
            dynamicSearch: () => string.Join(' ', ReadCommands().Select(c => c.Name)));

        var calc = PluginSection("calcy");
        Add(s, "Calculator: decimal places", "Answers are rounded to this many places.", "calcy calculator rounding decimals precision",
            Number(calc["rounding"]?.GetValue<int>() ?? 10, 0, 15, v => calc["rounding"] = v, Apply.Plugins));
        Add(s, "Calculator: thousands separator", "Show 1,234,567 instead of 1234567.", "calcy calculator group separator thousands comma",
            Toggle(calc["groupSeparator"]?.GetValue<bool>() ?? true, v => calc["groupSeparator"] = v, Apply.Plugins));
        Add(s, "Calculator: copy on Enter", "Pressing Enter on an answer copies it to the clipboard.", "calcy calculator clipboard copy enter",
            Toggle(calc["copyToClipboard"]?.GetValue<bool>() ?? true, v => calc["copyToClipboard"] = v, Apply.Plugins));
        Add(s, "Calculator: comma as decimal point", "Type 1,5 instead of 1.5 (function arguments are then separated with ;).", "calcy calculator comma decimal european locale",
            Toggle(calc["commaDecimal"]?.GetValue<bool>() ?? false, v => calc["commaDecimal"] = v, Apply.Plugins));
    }

    private JsonObject PluginSection(string id)
    {
        if (!Cfg.Plugins.TryGetValue(id, out var section))
            Cfg.Plugins[id] = section = new JsonObject();
        return section;
    }

    private List<WebSite> ReadWebSites() =>
        PluginSection(WebyPlugin.PluginId)["sites"]?.Deserialize<List<WebSite>>(WebJson) ?? WebyPlugin.DefaultSites();

    private List<RunnerCommand> ReadCommands() =>
        PluginSection(RunnerPlugin.PluginId)["commands"]?.Deserialize<List<RunnerCommand>>(WebJson) ?? RunnerPlugin.DefaultCommands();

    private FrameworkElement WebyEditor()
    {
        var sites = ReadWebSites();
        var host = new StackPanel();
        void Save()
        {
            Cfg.Plugins[WebyPlugin.PluginId] = WebyPlugin.CreateSettings(sites);
            Schedule(Apply.Plugins);
        }
        void Render()
        {
            host.Children.Clear();
            host.Children.Add(ColumnHeaders(("Name", 150), ("URL  (%1 = search text)", -1), ("Default", 70), ("", 34)));
            foreach (var site in sites)
            {
                var row = RowGrid(150, -1, 70, 34);
                var name = Input(site.Name, v => { site.Name = v; Save(); }, "Name");
                var url = Input(site.Query, v => { site.Query = v; Save(); }, "https://example.com/search?q=%1");
                url.Margin = new Thickness(6, 0, 0, 0);
                var def = new RadioButton { Style = (Style)FindResource("S.Radio"), GroupName = "webyDefault", IsChecked = site.Default, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Used when nothing else matches" };
                def.Checked += (_, _) => { foreach (var x in sites) x.Default = ReferenceEquals(x, site); Save(); };
                var del = IconButton("\uE74D", "Remove site", () => { sites.Remove(site); Save(); Render(); });
                Place(row, name, 0); Place(row, url, 1); Place(row, def, 2); Place(row, del, 3);
                host.Children.Add(row);
            }
            var add = new Button { Content = "Add site", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            add.Click += (_, _) => { sites.Add(new WebSite { Name = "New site", Query = "https://" }); Save(); Render(); };
            host.Children.Add(add);
        }
        Render();
        return host;
    }

    private FrameworkElement RunnerEditor()
    {
        var commands = ReadCommands();
        var host = new StackPanel();
        void Save()
        {
            Cfg.Plugins[RunnerPlugin.PluginId] = RunnerPlugin.CreateSettings(commands);
            Schedule(Apply.Plugins);
        }
        void Render()
        {
            host.Children.Clear();
            host.Children.Add(ColumnHeaders(("Name", 120), ("Program", -1), ("", 34), ("Arguments", 150), ("", 34)));
            foreach (var cmd in commands)
            {
                var row = RowGrid(120, -1, 34, 150, 34);
                var name = Input(cmd.Name, v => { cmd.Name = v; Save(); }, "Name");
                var file = Input(cmd.File, v => { cmd.File = v; Save(); }, "C:\\Path\\program.exe");
                file.Margin = new Thickness(6, 0, 0, 0);
                var browse = IconButton("\uE8DA", "Browse…", () =>
                {
                    var dlg = new OpenFileDialog { Title = "Choose program", Filter = "Programs|*.exe;*.bat;*.cmd;*.lnk;*.ps1|All files|*.*" };
                    if (dlg.ShowDialog(this) == true) file.Text = dlg.FileName;
                });
                var args = Input(cmd.Args, v => { cmd.Args = v; Save(); }, "/K $$");
                args.Margin = new Thickness(6, 0, 0, 0);
                var del = IconButton("\uE74D", "Remove command", () => { commands.Remove(cmd); Save(); Render(); });
                Place(row, name, 0); Place(row, file, 1); Place(row, browse, 2); Place(row, args, 3); Place(row, del, 4);
                host.Children.Add(row);
            }
            var add = new Button { Content = "Add command", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            add.Click += (_, _) => { commands.Add(new RunnerCommand { Name = "new", Args = "$$" }); Save(); Render(); };
            host.Children.Add(add);
        }
        Render();
        return host;
    }

    private void BuildShortcuts()
    {
        var s = AddSection("Shortcuts", "\uE765", "What Kite has learned: the text you type and the item it opens.");

        var filter = Input("", null, "Filter shortcuts…");
        filter.Width = 260;
        var count = new TextBlock { Foreground = Faint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        void Render()
        {
            list.Children.Clear();
            var f = filter.Text.Trim();
            var all = Services.History.AllPrefixes()
                .Where(p => f.Length == 0 || p.Key.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Value.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Value.Pinned).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            count.Text = $"{all.Count} shortcut{(all.Count == 1 ? "" : "s")}";
            foreach (var (typed, entry) in all.Take(400))
            {
                var row = RowGrid(130, 24, -1, 60, 34, 34);
                var t = new TextBlock { Text = typed, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Kite.Accent"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var arrow = new TextBlock { Text = "→", Foreground = Faint, VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = entry.Key };
                var uses = new TextBlock { Text = entry.Pinned ? "pinned" : $"×{entry.Count}", Foreground = Faint, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                var pin = IconButton(entry.Pinned ? "\uE841" : "\uE718", entry.Pinned ? "Unpin (allow learning to replace it)" : "Pin (never replaced or forgotten)", () =>
                {
                    entry.Pinned = !entry.Pinned;
                    Schedule(Apply.History);
                    Render();
                });
                if (entry.Pinned) pin.Foreground = (Brush)FindResource("Kite.Accent");
                var del = IconButton("\uE74D", "Forget this shortcut", () =>
                {
                    Services.History.RemovePrefix(typed);
                    Schedule(Apply.History);
                    Render();
                });
                Place(row, t, 0); Place(row, arrow, 1); Place(row, name, 2); Place(row, uses, 3); Place(row, pin, 4); Place(row, del, 5);
                row.Margin = new Thickness(0, 0, 0, 2);
                list.Children.Add(row);
            }
            if (all.Count > 400)
                list.Children.Add(new TextBlock { Text = $"…and {all.Count - 400} more - use the filter.", Foreground = Faint, Margin = new Thickness(0, 6, 0, 0) });
        }
        filter.TextChanged += (_, _) => Render();

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(filter);
        header.Children.Add(count);
        var body = new StackPanel();
        body.Children.Add(AddShortcutRow(Render));
        body.Children.Add(header);
        body.Children.Add(list);
        Render();

        Add(s, "Learned shortcuts", "Pinned shortcuts are never replaced by learning or forgotten. Add your own to make a word always open something.",
            "history learned aliases shortcuts typed prefix remembered pin forget alias keyword", null, body,
            dynamicSearch: () => string.Join(' ', Services.History.AllPrefixes().Select(p => p.Key + " " + p.Value.Name)));
    }

    /// <summary>"Add shortcut" row: type the word, pick the item from catalog suggestions.</summary>
    private FrameworkElement AddShortcutRow(Action refresh)
    {
        CatItem? chosen = null;
        var typed = Input("", null, "When I type…");
        typed.Width = 130;
        var target = Input("", null, "…open (start typing to search)");
        target.Margin = new Thickness(8, 0, 0, 0);
        var add = new Button { Content = "Add", Style = (Style)FindResource("S.AccentButton"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };

        var suggestions = new ListBox { Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)), BorderThickness = new Thickness(0), MaxHeight = 260, Padding = new Thickness(2) };
        suggestions.ItemContainerStyle = SuggestionItemStyle();
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border { Child = suggestions, CornerRadius = new CornerRadius(6), BorderBrush = (Brush)FindResource("S.InputBorder"), BorderThickness = new Thickness(1), Background = suggestions.Background, Margin = new Thickness(0, 4, 0, 0) },
        };

        void UpdateAdd() => add.IsEnabled = chosen is not null && typed.Text.Trim().Length > 0;
        var picking = false;
        target.TextChanged += (_, _) =>
        {
            if (picking) return;
            chosen = null;
            UpdateAdd();
            var items = Services.Search.SearchCatalog(target.Text, 8);
            suggestions.Items.Clear();
            foreach (var item in items)
            {
                var panel = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };
                panel.Children.Add(new TextBlock { Text = item.Name, FontSize = 13 });
                panel.Children.Add(new TextBlock { Text = item.Description ?? item.Target, FontSize = 11, Foreground = Faint, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 });
                suggestions.Items.Add(new ListBoxItem { Content = panel, Tag = item });
            }
            popup.MinWidth = target.ActualWidth;
            popup.IsOpen = items.Count > 0;
        };
        target.PreviewKeyDown += (_, e) =>
        {
            if (!popup.IsOpen) return;
            if (e.Key == Key.Down) { suggestions.SelectedIndex = Math.Min(suggestions.Items.Count - 1, suggestions.SelectedIndex + 1); e.Handled = true; }
            else if (e.Key == Key.Up) { suggestions.SelectedIndex = Math.Max(0, suggestions.SelectedIndex - 1); e.Handled = true; }
            else if (e.Key == Key.Enter && suggestions.Items.Count > 0) { Pick((ListBoxItem)(suggestions.SelectedItem ?? suggestions.Items[0])); e.Handled = true; }
            else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
        };
        suggestions.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(suggestions, (DependencyObject)e.OriginalSource) is ListBoxItem li) Pick(li);
        };
        void Pick(ListBoxItem li)
        {
            chosen = (CatItem)li.Tag;
            picking = true;
            target.Text = chosen.Name;
            target.CaretIndex = target.Text.Length;
            picking = false;
            popup.IsOpen = false;
            UpdateAdd();
        }
        typed.TextChanged += (_, _) => UpdateAdd();
        add.Click += (_, _) =>
        {
            if (chosen is null) return;
            Services.History.SetPrefix(typed.Text, chosen.Key, chosen.Name, pinned: true);
            Schedule(Apply.History);
            ShowToast($"\u201c{typed.Text.Trim()}\u201d now opens {chosen.Name}");
            typed.Text = "";
            picking = true; target.Text = ""; picking = false;
            chosen = null;
            UpdateAdd();
            refresh();
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(row, typed, 0); Place(row, target, 1); Place(row, add, 2);
        return row;
    }

    private void BuildAbout()
    {
        var s = AddSection("About", "\uE946", "Files, import and keyboard reference.");
        var p = Services.Paths;

        Add(s, "Keyboard", string.Join("\n",
                "Enter - launch   ·   Tab - pass text to the item (\"email ⇥ bob@x.com\")",
                "Ctrl+Enter - open containing folder   ·   Ctrl+Shift+Enter - run as administrator",
                "Shift+Enter - alternate action   ·   Alt+1…9 - launch that row   ·   Backspace on empty - remove a chip",
                "F5 - rebuild catalog   ·   Shift+F5 - reload skin   ·   Ctrl+, - settings   ·   Esc - hide",
                "Right-click - menu   ·   drag the frame (or Shift+drag) - move"),
            "keyboard shortcuts keys help enter tab ctrl alt f5 escape reference", null);

        Add(s, "Data folder", p.DataDirectory + (p.IsPortable ? "  (portable mode)" : ""), "data folder appdata portable location files",
            ActionButton("Open", () => _host.OpenFolder(p.DataDirectory)));
        Add(s, "settings.json", "Every option on these pages lives in this file, along with a few advanced ones. Changes made in a text editor apply when you save.",
            "json settings file advanced edit text", ActionButton("Edit", () => _host.OpenSettingsJson()));
        Add(s, "Log", "Startup, catalog scans and errors.", "log diagnostics errors debug troubleshooting",
            ActionButton("Open log", () => _host.OpenFile(p.LogFile)));
        Add(s, "Import from Launchy", "Bring over directories, hotkey, Weby sites, Runner commands, Calcy options and learned shortcuts from launchy.ini. Launchy's files are only read.",
            "launchy import migrate ini history", ActionButton("Import…", () => _host.ImportLaunchy()));
    }

    // =================================================================== search & navigation

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: Section section }) return;
        _current = section;
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = ""; // clicking a section leaves search mode (TextChanged re-renders)
            return;
        }
        Rebuild();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Rebuild();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    /// <summary>Shows the current section, or - while searching - every matching card grouped by section.</summary>
    private void Rebuild()
    {
        ContentPanel.Children.Clear();
        var q = SearchBox.Text.Trim().ToLowerInvariant();
        ClearSearch.Visibility = q.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (q.Length == 0)
        {
            foreach (var s in _sections) if (s.NavItem is not null) s.NavItem.Opacity = 1;
            var section = _current ?? _sections[0];
            ContentPanel.Children.Add(new TextBlock { Text = section.Title, FontSize = 28, FontWeight = FontWeights.SemiBold });
            ContentPanel.Children.Add(new TextBlock { Text = section.Subtitle, Foreground = Sub, FontSize = 13, Margin = new Thickness(0, 4, 0, 20), TextWrapping = TextWrapping.Wrap });
            foreach (var entry in section.Entries) ContentPanel.Children.Add(entry.Card);
            ContentScroll.ScrollToTop();
            return;
        }

        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var total = 0;
        ContentPanel.Children.Add(new TextBlock { FontSize = 13, Foreground = Sub, Margin = new Thickness(0, 0, 0, 14) });
        foreach (var s in _sections)
        {
            var matches = s.Entries.Where(e => e.Matches(tokens)).ToList();
            if (s.NavItem is not null) s.NavItem.Opacity = matches.Count > 0 ? 1 : 0.35;
            if (matches.Count == 0) continue;
            total += matches.Count;
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, total == matches.Count ? 0 : 18, 0, 8) };
            header.Children.Add(new TextBlock { Text = s.Glyph, FontFamily = Icons, FontSize = 13, Foreground = Sub, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = s.Title.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Sub, VerticalAlignment = VerticalAlignment.Center });
            ContentPanel.Children.Add(header);
            foreach (var m in matches) ContentPanel.Children.Add(m.Card);
        }
        ((TextBlock)ContentPanel.Children[0]).Text = total == 0
            ? $"No settings match \u201c{SearchBox.Text.Trim()}\u201d."
            : $"{total} setting{(total == 1 ? "" : "s")} match \u201c{SearchBox.Text.Trim()}\u201d";
        ContentScroll.ScrollToTop();
    }

    // =================================================================== applying changes

    private void Schedule(Apply kind)
    {
        _pending |= kind | Apply.Save;
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void Flush()
    {
        _applyTimer.Stop();
        if (_pending == Apply.None) return;
        var p = _pending;
        _pending = Apply.None;
        try
        {
            Services.Settings.Save();
            if (p.HasFlag(Apply.Runtime)) _host.ApplyRuntime();
            if (p.HasFlag(Apply.Plugins)) _host.PluginsChanged();
            if (p.HasFlag(Apply.History)) Services.SaveHistorySoon();
            if (p.HasFlag(Apply.Catalog)) _catalogDirty = true;
        }
        catch (Exception ex)
        {
            ShowToast("Couldn't save: " + ex.Message, error: true);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var c = Services.Catalog;
        var lines = new List<string>();
        if (c.IsRebuilding) lines.Add("Rescanning catalog…");
        else
        {
            lines.Add($"{c.Current.Count:N0} items in catalog");
            if (c.LastRebuilt is { } when) lines.Add($"Scanned {when:HH:mm} in {FormatDuration(c.LastRebuildMilliseconds)}");
        }
        lines.Add(_catalogDirty ? "Folder changes apply on rescan (or when you close this window)" : "Changes are saved automatically");
        StatusText.Text = string.Join("\n", lines);
    }

    private void ShowToast(string text, bool error = false)
    {
        ToastText.Text = text;
        ToastText.Foreground = error ? (Brush)FindResource("S.Danger") : (Brush)FindResource("S.Text");
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // =================================================================== building blocks

    private FontFamily Icons => (FontFamily)FindResource("S.Icons");
    private Brush Sub => (Brush)FindResource("S.SubText");
    private Brush Faint => (Brush)FindResource("S.Faint");

    private Section AddSection(string title, string glyph, string subtitle)
    {
        var s = new Section(title, glyph, subtitle);
        _sections.Add(s);
        return s;
    }

    /// <param name="right">Compact editor placed to the right of the text.</param>
    /// <param name="below">Wide editor placed under the text.</param>
    private void Add(Section s, string title, string description, string keywords, FrameworkElement? right,
                     FrameworkElement? below = null, Func<string>? dynamicSearch = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), LineHeight = 18 });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(text);
        if (right is not null)
        {
            right.Margin = new Thickness(24, 0, 0, 0);
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        if (below is not null)
        {
            below.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(below, 1);
            Grid.SetColumnSpan(below, 2);
            grid.Children.Add(below);
        }

        var card = new Border
        {
            Background = (Brush)FindResource("S.Card"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 5),
            Child = grid,
        };
        card.MouseEnter += (_, _) => card.Background = (Brush)FindResource("S.CardHover");
        card.MouseLeave += (_, _) => card.Background = (Brush)FindResource("S.Card");

        var search = $"{s.Title} {title} {description} {keywords}".ToLowerInvariant();
        s.Entries.Add(new Entry(search, card, dynamicSearch));
    }

    private CheckBox Toggle(bool value, Action<bool> set, Apply kind, string? label = null)
    {
        var cb = new CheckBox { Style = (Style)FindResource("S.Toggle"), IsChecked = value };
        void Label() => cb.Content = label ?? (cb.IsChecked == true ? "On" : "Off");
        Label();
        cb.Checked += (_, _) => { set(true); Label(); Schedule(kind); };
        cb.Unchecked += (_, _) => { set(false); Label(); Schedule(kind); };
        return cb;
    }

    private FrameworkElement Number(int value, int min, int max, Action<int> set, Apply kind, string? suffix = null)
    {
        var box = new TextBox { Text = value.ToString(), Width = 72, HorizontalContentAlignment = HorizontalAlignment.Right, ToolTip = $"{min}–{max}" };
        var normal = box.BorderBrush;
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, out var v) && v >= min && v <= max)
            {
                box.ClearValue(BorderBrushProperty);
                set(v);
                Schedule(kind);
            }
            else box.BorderBrush = (Brush)FindResource("S.Danger");
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down) || !int.TryParse(box.Text, out var v)) return;
            box.Text = Math.Clamp(v + (e.Key == Key.Up ? 1 : -1), min, max).ToString();
            box.CaretIndex = box.Text.Length;
            e.Handled = true;
        };
        if (suffix is null) return box;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = suffix, Foreground = Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        return panel;
    }

    private FrameworkElement SliderEditor(int value, int min, int max, int step, Action<int> set, Apply kind, Func<int, string> format)
    {
        var label = new TextBlock { Text = format(value), Foreground = Sub, Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 2, TickFrequency = step, IsSnapToTickEnabled = true };
        slider.ValueChanged += (_, _) =>
        {
            var v = (int)slider.Value;
            label.Text = format(v);
            set(v);
            Schedule(kind);
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(label);
        panel.Children.Add(slider);
        return panel;
    }

    private ComboBox Combo<T>(IEnumerable<(string Label, T Value)> options, T current, Action<T> set, Apply kind)
    {
        var combo = new ComboBox { MinWidth = 250 };
        foreach (var (label, value) in options)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            combo.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(value, current)) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T v }) { set(v); Schedule(kind); }
        };
        return combo;
    }

    private TextBox Input(string text, Action<string>? changed, string hint)
    {
        var box = new TextBox { Text = text, Tag = hint };
        if (changed is not null) box.TextChanged += (_, _) => changed(box.Text);
        return box;
    }

    private Button IconButton(string glyph, string tooltip, Action click)
    {
        var b = new Button { Style = (Style)FindResource("S.IconButton"), Content = glyph, ToolTip = tooltip, VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => click();
        return b;
    }

    private Button ActionButton(string text, Action click)
    {
        var b = new Button { Content = text, MinWidth = 90 };
        b.Click += (_, _) => click();
        return b;
    }

    private TextBlock Label(string text, double leftMargin = 0) =>
        new() { Text = text, Foreground = Sub, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(leftMargin, 0, 8, 0) };

    /// <summary>Grid with fixed-width columns; -1 means "take the remaining space".</summary>
    private static Grid RowGrid(params double[] widths)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var w in widths)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        return g;
    }

    private FrameworkElement ColumnHeaders(params (string Text, double Width)[] columns)
    {
        var g = RowGrid(columns.Select(c => c.Width).ToArray());
        for (var i = 0; i < columns.Length; i++)
        {
            var t = new TextBlock { Text = columns[i].Text, FontSize = 11.5, Foreground = Faint, Margin = new Thickness(i == 0 ? 2 : 8, 0, 0, 0) };
            Place(g, t, i);
        }
        return g;
    }

    private static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private Style SuggestionItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(ForegroundProperty, FindResource("S.Text")));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 3, 6, 3)));
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        return style;
    }

    private FrameworkElement HotkeyEditor()
    {
        var box = new TextBox { Text = Cfg.General.Hotkey, Width = 190, IsReadOnly = true, Cursor = Cursors.Hand, Tag = "Press keys…", HorizontalContentAlignment = HorizontalAlignment.Center, IsReadOnlyCaretVisible = false };
        var status = new TextBlock { FontSize = 11.5, Foreground = Faint, Margin = new Thickness(0, 6, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
        status.Text = "Click, then press a combination";

        box.GotKeyboardFocus += (_, _) =>
        {
            _host.SuspendHotkey(true);
            status.Text = "Listening… (Esc to cancel)";
            status.Foreground = (Brush)FindResource("Kite.Accent");
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            _host.SuspendHotkey(false);
            if (!ReferenceEquals(status.Foreground, FindResource("S.Danger"))) { status.Text = "Click, then press a combination"; status.Foreground = Faint; }
            box.Text = Cfg.General.Hotkey;
        };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, SearchBox); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab)
                return;

            var mods = Keyboard.Modifiers;
            var winDown = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (winDown) parts.Add("Win");
            var isFunctionKey = key is >= Key.F1 and <= Key.F24;
            if (parts.Count == 0 && !isFunctionKey)
            {
                status.Text = "Use at least one of Ctrl, Alt, Shift or Win";
                status.Foreground = (Brush)FindResource("S.Danger");
                return;
            }
            parts.Add(KeyName(key));
            var text = string.Join('+', parts);
            box.Text = text;

            var error = _host.ApplyHotkey(text);
            if (error is null)
            {
                Cfg.General.Hotkey = text;
                Schedule(Apply.Save);
                status.Text = $"Saved - {text} now opens Kite";
                status.Foreground = (Brush)FindResource("S.Ok");
            }
            else
            {
                status.Text = error;
                status.Foreground = (Brush)FindResource("S.Danger");
            }
        };

        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(status);
        return panel;
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "NumPad" + (key - Key.NumPad0),
        Key.Oem3 => "`",
        Key.Return => "Enter",
        _ => key.ToString(),
    };

    // ------------------------------------------------------------------ skins

    private WrapPanel? _gallery;
    private readonly Dictionary<string, SkinEditorWindow> _editors = new(StringComparer.OrdinalIgnoreCase);

    private void RenderGallery()
    {
        if (_gallery is null) return;
        _gallery.Children.Clear();
        foreach (var info in _host.SkinLibrary.List())
            _gallery.Children.Add(SkinTile(info));
    }

    private void CreateSkin()
    {
        var name = InputDialog.Ask(this, "New skin", "Name for the new skin (it starts as a copy of the skin you're using now):", "My skin");
        if (name is null) return;
        var current = _host.SkinLibrary.Find(Cfg.General.Skin);
        var info = current is null ? _host.SkinLibrary.Create(name, new Skin()) : _host.SkinLibrary.Duplicate(current, name);
        var skin = SkinLibrary.Clone(info.Skin);
        skin.Author = Environment.UserName;
        skin.Description = "";
        SkinLibrary.Write(info.Folder, skin);
        RenderGallery();
        OpenEditor(_host.SkinLibrary.Find(info.Name) ?? info);
    }

    private void ImportSkin()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import a skin",
            Filter = "Kite or Launchy skin|*.zip;skin.json;style.qss|Kite skin package (*.zip)|*.zip|Kite skin folder (skin.json)|skin.json|Launchy skin folder (style.qss)|style.qss",
        };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? dlg.FileName : Path.GetDirectoryName(dlg.FileName)!;
        try
        {
            var info = _host.SkinLibrary.Import(path);
            RenderGallery();
            ShowToast($"Imported “{info.Name}” - click it to use it");
        }
        catch (Exception ex)
        {
            ShowToast("Import failed: " + ex.Message, error: true);
        }
    }

    private void OpenEditor(SkinInfo info)
    {
        if (info.BuiltIn)
        {
            var answer = MessageBox.Show(this, $"“{info.Name}” is a built-in skin and can't be changed.\n\nMake an editable copy and edit that?",
                "Edit skin", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            info = _host.SkinLibrary.Duplicate(info, info.Name + " (custom)");
            RenderGallery();
        }
        if (_editors.TryGetValue(info.Folder, out var open))
        {
            open.Activate();
            return;
        }
        var editor = new SkinEditorWindow(_host, info) { Owner = this, Icon = Icon };
        editor.Saved += RenderGallery;
        editor.Closed += (_, _) => _editors.Remove(info.Folder);
        _editors[info.Folder] = editor;
        editor.Show();
    }

    private void DuplicateSkin(SkinInfo info)
    {
        var name = InputDialog.Ask(this, "Duplicate skin", "Name for the copy:", info.Name + " copy");
        if (name is null) return;
        var copy = _host.SkinLibrary.Duplicate(info, name);
        RenderGallery();
        ShowToast($"Created “{copy.Name}”");
    }

    private void RenameSkin(SkinInfo info)
    {
        var name = InputDialog.Ask(this, "Rename skin", "New name:", info.Name);
        if (name is null || name == info.Name) return;
        if (_editors.ContainsKey(info.Folder)) { ShowToast("Close the skin editor first", error: true); return; }
        try
        {
            var wasActive = info.Name.Equals(Cfg.General.Skin, StringComparison.OrdinalIgnoreCase);
            var renamed = _host.SkinLibrary.Rename(info, name);
            if (wasActive) _host.ApplySkin(renamed.Name);
            RenderGallery();
        }
        catch (Exception ex) { ShowToast("Rename failed: " + ex.Message, error: true); }
    }

    private void ExportSkin(SkinInfo info)
    {
        var dlg = new SaveFileDialog { Title = "Export skin", FileName = info.Name + ".zip", Filter = "Kite skin package (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _host.SkinLibrary.Export(info, dlg.FileName);
            ShowToast($"Exported to {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) { ShowToast("Export failed: " + ex.Message, error: true); }
    }

    private void DeleteSkin(SkinInfo info)
    {
        var answer = MessageBox.Show(this, $"Delete the skin “{info.Name}”?\n\nIt will be moved to the Recycle Bin.", "Delete skin",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        if (_editors.TryGetValue(info.Folder, out var editor)) editor.Close();
        try
        {
            if (info.Name.Equals(Cfg.General.Skin, StringComparison.OrdinalIgnoreCase))
            {
                var fallback = _host.SkinLibrary.List().FirstOrDefault(s => s.BuiltIn && s.Name.Equals("mversion", StringComparison.OrdinalIgnoreCase))
                               ?? _host.SkinLibrary.List().FirstOrDefault(s => s.Folder != info.Folder);
                if (fallback is not null) _host.ApplySkin(fallback.Name);
            }
            _host.SkinLibrary.Delete(info);
            RenderGallery();
            ShowToast($"Deleted “{info.Name}” (it's in the Recycle Bin)");
        }
        catch (Exception ex) { ShowToast("Delete failed: " + ex.Message, error: true); }
    }

    private FrameworkElement SkinTile(SkinInfo info)
    {
        var skin = info.Skin;
        var name = info.Name;
        var selected = name.Equals(Cfg.General.Skin, StringComparison.OrdinalIgnoreCase);
        var accent = SkinManager.Brush(skin.Accent, Brushes.DeepPink);

        // Miniature launcher: frame, accent strip, input text and a highlighted result row.
        var preview = new Grid { Height = 74 };
        var frame = new Border
        {
            Background = SkinManager.Background(skin.Background),
            BorderBrush = SkinManager.Brush(skin.BorderColor, Brushes.Transparent),
            BorderThickness = new Thickness(Math.Min(1, skin.BorderThickness)),
            CornerRadius = new CornerRadius(Math.Min(skin.CornerRadius / 2, 8)),
            ClipToBounds = true,
        };
        if (!string.IsNullOrEmpty(skin.BackgroundImage) && File.Exists(Path.Combine(info.Folder, skin.BackgroundImage)))
        {
            try
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(Path.Combine(info.Folder, skin.BackgroundImage));
                img.DecodePixelWidth = 360;
                img.EndInit();
                frame.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
            }
            catch { /* keep the colour background */ }
        }
        var inner = new DockPanel();
        if (skin.AccentWidth > 0)
        {
            var strip = new Border { Width = Math.Max(2, skin.AccentWidth / 1.5), Background = accent };
            DockPanel.SetDock(strip, Dock.Left);
            inner.Children.Add(strip);
        }
        var stack = new StackPanel { Margin = new Thickness(10, 8, 10, 6) };
        stack.Children.Add(new TextBlock
        {
            Text = "launch…",
            FontSize = 14,
            FontFamily = new FontFamily(skin.InputFont),
            FontWeight = FontWeights.SemiBold,
            Foreground = SkinManager.Brush(skin.InputColor, Brushes.White),
        });
        var sel = string.IsNullOrEmpty(skin.SelectedBackground) ? accent : SkinManager.Brush(skin.SelectedBackground, accent);
        stack.Children.Add(new Border
        {
            Background = sel,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(6, 3, 6, 3),
            Child = new TextBlock { Text = "Paint", FontSize = 10.5, Foreground = SkinManager.Brush(skin.SelectedColor, Brushes.White) },
        });
        inner.Children.Add(stack);
        frame.Child = inner;
        preview.Children.Add(frame);

        // Hover actions over the preview
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0), Visibility = Visibility.Collapsed };
        Button Action(string glyph, string tip, Action run)
        {
            var b = new Button { Content = glyph, ToolTip = tip, FontFamily = Icons, FontSize = 12, Padding = new Thickness(6), Margin = new Thickness(2, 0, 0, 0), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x26, 0x26, 0x26)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)) };
            b.Click += (_, e) => { e.Handled = true; run(); };
            return b;
        }
        actions.Children.Add(Action("", info.BuiltIn ? "Edit a copy" : "Edit", () => OpenEditor(info)));
        actions.Children.Add(Action("", "Duplicate", () => DuplicateSkin(info)));
        if (!info.BuiltIn) actions.Children.Add(Action("", "Rename", () => RenameSkin(info)));
        actions.Children.Add(Action("", "Export as .zip", () => ExportSkin(info)));
        if (!info.BuiltIn) actions.Children.Add(Action("", "Delete", () => DeleteSkin(info)));
        preview.Children.Add(actions);

        var caption = new Grid { Margin = new Thickness(2, 8, 2, 0) };
        caption.ColumnDefinitions.Add(new ColumnDefinition());
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new StackPanel();
        names.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        names.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(skin.Author) ? skin.Description : skin.Author, FontSize = 11, Foreground = Faint, TextTrimming = TextTrimming.CharacterEllipsis });
        caption.Children.Add(names);
        var badge = new Border
        {
            CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 1, 0, 0),
            Background = selected ? (Brush)FindResource("Kite.Accent") : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Child = new TextBlock { Text = selected ? "In use" : info.BuiltIn ? "Built-in" : "Custom", FontSize = 10, Foreground = selected ? Brushes.White : Sub },
        };
        Grid.SetColumn(badge, 1);
        caption.Children.Add(badge);

        var content = new StackPanel();
        content.Children.Add(preview);
        content.Children.Add(caption);

        var tile = new Border
        {
            Width = 196,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            BorderThickness = new Thickness(2),
            BorderBrush = selected ? (Brush)FindResource("Kite.Accent") : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = content,
            ToolTip = string.IsNullOrEmpty(skin.Description) ? null : skin.Description,
        };
        tile.MouseEnter += (_, _) => { actions.Visibility = Visibility.Visible; if (!selected) tile.BorderBrush = (Brush)FindResource("S.InputBorder"); };
        tile.MouseLeave += (_, _) => { actions.Visibility = Visibility.Collapsed; if (!selected) tile.BorderBrush = Brushes.Transparent; };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (IsWithinButton(e.OriginalSource as DependencyObject)) return; // an action button was clicked
            _host.ApplySkin(name);
            RenderGallery();
        };
        tile.MouseRightButtonUp += (_, _) => OpenEditor(info);
        return tile;
    }

    private static bool IsWithinButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ButtonBase) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private static string FormatDuration(long ms) => ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0} s";

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
