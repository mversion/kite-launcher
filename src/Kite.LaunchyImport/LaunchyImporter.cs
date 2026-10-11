using Kite.Core.Search;
using Kite.Core.Settings;
using Kite.Plugins.Builtin.Calcy;
using Kite.Plugins.Builtin.Runner;
using Kite.Plugins.Builtin.Weby;

namespace Kite.LaunchyImport;

public sealed record ImportedAlias(string Typed, string Key, string Name);

public sealed class ImportResult
{
    public required AppSettings Settings { get; init; }
    public List<ImportedAlias> Aliases { get; } = [];
    public List<string> Warnings { get; } = [];
    public int DirectoryCount => Settings.Directories.Count;

    public string Summary =>
        $"{Settings.Directories.Count} directories, {Aliases.Count} learned shortcuts, " +
        $"{CountOf("weby", "sites")} web sites, {CountOf("runner", "commands")} runner commands";

    private int CountOf(string plugin, string array) =>
        Settings.Plugins.TryGetValue(plugin, out var o) && o[array] is System.Text.Json.Nodes.JsonArray a ? a.Count : 0;
}

/// <summary>
/// Converts a Launchy 2.x launchy.ini into Kite settings and learned shortcuts.
/// The source file is only ever read.
/// </summary>
public static class LaunchyImporter
{
    public static readonly string DefaultIniPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Launchy", "launchy.ini");

    public static readonly string DefaultInstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Launchy");

    public static ImportResult Import(string iniPath, string? launchyInstallDir = null, AppSettings? baseSettings = null)
    {
        var ini = QSettingsIni.Load(iniPath);
        return Import(ini, launchyInstallDir ?? DefaultInstallDir, baseSettings);
    }

    public static ImportResult Import(QSettingsIni ini, string launchyInstallDir, AppSettings? baseSettings = null)
    {
        var settings = baseSettings ?? new AppSettings();
        var result = new ImportResult { Settings = settings };

        ImportGeneral(ini, settings.General, result);
        ImportDirectories(ini, settings, launchyInstallDir, result);
        ImportWeby(ini, settings);
        ImportRunner(ini, settings);
        ImportCalcy(ini, settings);
        ImportHistory(ini, result);
        return result;
    }

    /// <summary>Writes an import into the live settings and history.</summary>
    public static void Apply(ImportResult result, SettingsStore store, History history)
    {
        store.Replace(result.Settings);
        // Launchy has no timestamps; spread them so the original order breaks ties sensibly.
        var when = DateTime.UtcNow.AddDays(-1);
        foreach (var alias in result.Aliases)
            history.SetPrefix(alias.Typed, alias.Key, alias.Name, when: when = when.AddSeconds(1));
    }

    private static void ImportGeneral(QSettingsIni ini, GeneralSettings g, ImportResult result)
    {
        const string s = "GenOps";
        if (ini.GetInt(s, "hotkey") is { } hk)
        {
            var text = QtHotkey.ToText(hk);
            if (text is null) result.Warnings.Add($"Unrecognised Launchy hotkey code {hk}; keeping {g.Hotkey}");
            else g.Hotkey = text;
        }
        if (ini.GetString(s, "skin") is { Length: > 0 } skin) g.Skin = skin;
        if (ini.GetBool(s, "showtrayicon") is { } tray) g.ShowTrayIcon = tray;
        if (ini.GetBool(s, "hideiflostfocus") is { } hide) g.HideOnFocusLoss = hide;
        if (ini.GetInt(s, "alwayscenter") is { } center)
        {
            g.CenterHorizontally = (center & 1) != 0;
            g.CenterVertically = (center & 2) != 0;
        }
        if (ini.GetInt(s, "numviewable") is { } viewable and > 0) g.VisibleResults = viewable;
        if (ini.GetInt(s, "numresults") is { } results and > 0) g.MaxResults = results;
        if (ini.GetInt(s, "opaqueness") is { } opacity) g.Opacity = Math.Clamp(opacity, 20, 100);
        if (ini.GetInt(s, "updatetimer") is { } timer) g.RescanMinutes = timer;
        if (ini.GetBool(s, "showHiddenFiles") is { } hidden) g.ShowHiddenFiles = hidden;
        if (ini.GetInt(s, "dragmode") is { } drag) g.DragRequiresShift = drag == 1;
        // Launchy's fade sliders run 0-20; treat each step as 10 ms.
        if (ini.GetInt(s, "fadein") is { } fadeIn) g.FadeInMs = Math.Clamp(fadeIn * 10, 0, 300);
        if (ini.GetInt(s, "fadeout") is { } fadeOut) g.FadeOutMs = Math.Clamp(fadeOut * 10, 0, 300);
    }

    private static void ImportDirectories(QSettingsIni ini, AppSettings settings, string installDir, ImportResult result)
    {
        var dirs = ini.GetArray("directories", "");
        if (dirs.Count == 0) return;
        settings.Directories = [];
        foreach (var d in dirs)
        {
            if (!d.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)) continue;
            var path = name.Replace('/', '\\');
            // Launchy resolves relative entries (e.g. "Utilities\") against its own folder.
            if (!Path.IsPathRooted(Environment.ExpandEnvironmentVariables(path)))
                path = Path.Combine(installDir, path).TrimEnd('\\');
            var types = d.TryGetValue("types", out var t)
                ? QSettingsIni.ParseList(t).Where(x => x.Length > 0).ToList()
                : [];
            settings.Directories.Add(new CatalogDirectory
            {
                Path = path,
                Types = types,
                IncludeExecutables = d.TryGetValue("indexExes", out var exes) && exes.Equals("true", StringComparison.OrdinalIgnoreCase),
                IncludeDirectories = d.TryGetValue("indexDirs", out var dd) && dd.Equals("true", StringComparison.OrdinalIgnoreCase),
                Depth = d.TryGetValue("depth", out var depth) && int.TryParse(depth, out var dep) ? dep : 100,
            });
        }
        if (settings.Directories.Count == 0) result.Warnings.Add("No directories found in launchy.ini");
    }

    private static void ImportWeby(QSettingsIni ini, AppSettings settings)
    {
        var sites = ini.GetArray("weby", "sites")
            .Where(s => s.TryGetValue("name", out var n) && n.Length > 0 && s.ContainsKey("query"))
            .Select(s => new WebSite
            {
                Name = s["name"],
                Query = s["query"],
                Default = s.TryGetValue("default", out var def) && def.Equals("true", StringComparison.OrdinalIgnoreCase),
            })
            .ToList();
        if (sites.Count == 0) return;
        if (!sites.Any(s => s.Default)) sites[0].Default = true;
        settings.Plugins[WebyPlugin.PluginId] = WebyPlugin.CreateSettings(sites);
    }

    private static void ImportRunner(QSettingsIni ini, AppSettings settings)
    {
        var cmds = ini.GetArray("runner", "cmds")
            .Where(c => c.TryGetValue("name", out var n) && n.Length > 0)
            .Select(c => new RunnerCommand
            {
                Name = c["name"],
                File = c.GetValueOrDefault("file", ""),
                Args = c.GetValueOrDefault("args", ""),
            })
            .ToList();
        if (cmds.Count > 0) settings.Plugins[RunnerPlugin.PluginId] = RunnerPlugin.CreateSettings(cmds);
    }

    private static void ImportCalcy(QSettingsIni ini, AppSettings settings)
    {
        if (!ini.HasSection("calcy")) return;
        var o = new CalcyOptions
        {
            Rounding = ini.GetInt("calcy", "outputRounding") ?? 10,
            GroupSeparator = ini.GetBool("calcy", "outputGroupSeparator") ?? true,
            CopyToClipboard = ini.GetBool("calcy", "copyToClipboard") ?? true,
            CommaDecimal = ini.GetBool("calcy", "useCommaForDecimal") ?? false,
        };
        settings.Plugins[CalcyPlugin.PluginId] = CalcyPlugin.CreateSettings(o);
    }

    private static void ImportHistory(QSettingsIni ini, ImportResult result)
    {
        foreach (var (typed, _) in ini.GetSection("History"))
        {
            var parts = ini.GetList("History", typed);
            if (parts.Count < 2) continue;
            var name = parts[0];
            var target = parts[^1];
            string key;
            if (target.EndsWith(".weby", StringComparison.OrdinalIgnoreCase))
                key = $"{WebyPlugin.PluginId}:{target[..^".weby".Length]}";
            else if (target.Length > 2 && (target[1] == ':' || target.StartsWith("//") || target.StartsWith(@"\\")))
                key = target.Replace('/', '\\');
            else
                continue; // transient plugin results such as calculator answers
            result.Aliases.Add(new ImportedAlias(typed, key, name));
        }
    }
}

/// <summary>Converts Qt key-sequence integers (Launchy's hotkey setting) to "Alt+Space"-style text.</summary>
public static class QtHotkey
{
    private const int Shift = 0x02000000, Ctrl = 0x04000000, Alt = 0x08000000, Meta = 0x10000000;

    public static string? ToText(int code)
    {
        var key = code & 0x01FFFFFF;
        var name = KeyName(key);
        if (name is null) return null;
        var parts = new List<string>();
        if ((code & Ctrl) != 0) parts.Add("Ctrl");
        if ((code & Alt) != 0) parts.Add("Alt");
        if ((code & Shift) != 0) parts.Add("Shift");
        if ((code & Meta) != 0) parts.Add("Win");
        parts.Add(name);
        return string.Join('+', parts);
    }

    private static string? KeyName(int key) => key switch
    {
        0x20 => "Space",
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x41 and <= 0x5A => ((char)key).ToString(),
        0x60 => "`",
        0x01000001 => "Tab",
        0x01000004 or 0x01000005 => "Enter",
        0x01000000 => "Escape",
        >= 0x01000030 and <= 0x01000047 => "F" + (key - 0x01000030 + 1),
        0x01000010 => "Home",
        0x01000011 => "End",
        0x01000006 => "Insert",
        _ => null,
    };
}
