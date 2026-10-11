namespace Kite.Core.Settings;

/// <summary>
/// Resolves where Kite keeps its data. Portable mode (a "portable" marker file or
/// an existing settings.json next to Kite.exe) keeps everything beside the exe,
/// which suits PortableApps or a synced Google Drive folder.
/// </summary>
public sealed class KitePaths
{
    public KitePaths(string appDirectory, string? dataDirectoryOverride = null)
    {
        AppDirectory = appDirectory;
        IsPortable = dataDirectoryOverride is null &&
                     (File.Exists(Path.Combine(appDirectory, "portable")) ||
                      File.Exists(Path.Combine(appDirectory, "settings.json")));
        DataDirectory = dataDirectoryOverride
                        ?? (IsPortable
                            ? appDirectory
                            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kite"));
        Directory.CreateDirectory(DataDirectory);
    }

    public static KitePaths ForCurrentProcess() => new(AppContext.BaseDirectory.TrimEnd('\\', '/'));

    public string AppDirectory { get; }
    public string DataDirectory { get; }
    public bool IsPortable { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string HistoryFile => Path.Combine(DataDirectory, "history.json");
    public string CatalogCacheFile => Path.Combine(DataDirectory, "catalog.cache.json");
    public string PluginDataRoot => Path.Combine(DataDirectory, "plugindata");
    public string LogFile => Path.Combine(DataDirectory, "kite.log");

    /// <summary>Skin folders: user skins override bundled ones with the same name.</summary>
    public IEnumerable<string> SkinRoots
    {
        get
        {
            yield return Path.Combine(DataDirectory, "skins");
            yield return Path.Combine(AppDirectory, "skins");
        }
    }
}
