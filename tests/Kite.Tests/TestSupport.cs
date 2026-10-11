using Kite.Core.Catalog;
using Kite.Core.Plugins;
using Kite.Core.Search;
using Kite.Core.Settings;
using Kite.Plugins;

namespace Kite.Tests;

internal sealed class NullHost : IHostServices
{
    public List<string> Clipboard { get; } = [];
    public void CopyToClipboard(string text) => Clipboard.Add(text);
    public void RequestCatalogRebuild() { }
    public void Log(string message) { }
}

internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kite-tests-" + Guid.NewGuid().ToString("N"));

    public string File(string relative, string content = "")
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

internal sealed class Harness : IDisposable
{
    private readonly TempDir _dir = new();

    public Harness(IEnumerable<CatItem> items)
    {
        var paths = new KitePaths(_dir.Path, _dir.Path);
        Settings = new SettingsStore(paths);
        Plugins = new PluginHost(Settings, new NullHost());
        Snapshot = new CatalogSnapshot(items);
        Engine = new SearchEngine(() => Snapshot, History, Plugins);
    }

    public SettingsStore Settings { get; }
    public PluginHost Plugins { get; }
    public History History { get; } = new();
    public CatalogSnapshot Snapshot { get; set; }
    public SearchEngine Engine { get; }

    public List<string> Names(string query, int max = 10) => Engine.SearchCatalog(query, max).Select(i => i.Name).ToList();

    public void Dispose() => _dir.Dispose();
}

internal static class Items
{
    public static CatItem App(string name, string? target = null) =>
        new() { Name = name, Target = target ?? $@"C:\Apps\{name}.lnk" };

    /// <summary>A catalog resembling the user's real one.</summary>
    public static List<CatItem> Sample() =>
    [
        App("Everything"),
        App("Excel"),
        App("Edge"),
        App("Ente Auth"),
        App("Visual Studio Code"),
        App("Visual Studio 2022"),
        App("Notepad++"),
        App("Notepad"),
        App("Nicotine+"),
        App("Adobe Photoshop 2026"),
        App("Adobe Bridge 2026"),
        App("PO - Chrome"),
        App("Paint", @"shell:AppsFolder\Microsoft.Paint_8wekyb3d8bbwe!App"),
        App("Paint 3D", @"shell:AppsFolder\Microsoft.MSPaint_8wekyb3d8bbwe!Microsoft.MSPaint"),
        App("PuTTYPortable"),
        App("Calculator", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        App("WinSCPPortable"),
        App("Windows Terminal"),
        App("monitor-off"),
    ];
}
