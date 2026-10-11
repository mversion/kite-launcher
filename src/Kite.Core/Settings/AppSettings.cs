using System.Text.Json.Nodes;

namespace Kite.Core.Settings;

public sealed class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public List<CatalogDirectory> Directories { get; set; } = DefaultDirectories();

    /// <summary>Index Microsoft Store / UWP apps and everything else in shell:AppsFolder.</summary>
    public bool IndexAppsFolder { get; set; } = true;

    /// <summary>Per-plugin settings, keyed by plugin id.</summary>
    public Dictionary<string, JsonObject> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plugin ids that are switched off.</summary>
    public HashSet<string> DisabledPlugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static List<CatalogDirectory> DefaultDirectories() =>
    [
        new() { Path = @"%ProgramData%\Microsoft\Windows\Start Menu", Types = ["*.lnk"] },
        new() { Path = @"%AppData%\Microsoft\Windows\Start Menu", Types = ["*.lnk"] },
        new() { Path = @"%AppData%\Microsoft\Internet Explorer\Quick Launch", Types = ["*.lnk"] },
    ];
}

public sealed class GeneralSettings
{
    /// <summary>Global hotkey, e.g. "Alt+Space" or "Ctrl+Shift+K".</summary>
    public string Hotkey { get; set; } = "Alt+Space";
    public string Skin { get; set; } = "mversion";
    public bool ShowTrayIcon { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideOnFocusLoss { get; set; } = true;
    public bool CenterHorizontally { get; set; } = true;
    public bool CenterVertically { get; set; } = true;
    /// <summary>
    /// Dragged position as a fraction (0-1) of the monitor's work area, so the launcher keeps its
    /// relative spot on whichever monitor it opens on. Used for an axis that isn't centred.
    /// </summary>
    public double? PositionRelX { get; set; }
    public double? PositionRelY { get; set; }
    /// <summary>Which monitor the launcher opens on.</summary>
    public OpenOnMonitor OpenOn { get; set; } = OpenOnMonitor.ActiveWindow;
    /// <summary>Launchy's "only allow dragging whilst Shift is pressed".</summary>
    public bool DragRequiresShift { get; set; }
    public int MaxResults { get; set; } = 10;
    public int VisibleResults { get; set; } = 6;
    /// <summary>Delay before the result list appears (Launchy's "auto open delay"). 0 = instantly.</summary>
    public int ResultsDelayMs { get; set; } = 0;
    public int MaxHistoryItems { get; set; } = 500;
    public int FadeInMs { get; set; } = 80;
    public int FadeOutMs { get; set; } = 60;
    /// <summary>0-100.</summary>
    public int Opacity { get; set; } = 100;
    public int RescanMinutes { get; set; } = 10;
    public bool ShowHiddenFiles { get; set; }
    public bool StartWithWindows { get; set; }
    /// <summary>Give unused memory back to Windows shortly after the launcher hides.</summary>
    public bool ReleaseMemoryWhenHidden { get; set; } = true;
    /// <summary>Render without the GPU: avoids loading the graphics driver into Kite (less memory). Applies on restart.</summary>
    public bool SoftwareRendering { get; set; } = true;
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<OpenOnMonitor>))]
public enum OpenOnMonitor
{
    /// <summary>The monitor showing the window you were working in.</summary>
    ActiveWindow,
    /// <summary>The monitor under the mouse pointer.</summary>
    Cursor,
    Primary,
}

public sealed class CatalogDirectory
{
    public string Path { get; set; } = "";
    /// <summary>Glob patterns such as "*.lnk", "*.exe", "*.*".</summary>
    public List<string> Types { get; set; } = ["*.lnk"];
    public bool IncludeExecutables { get; set; }
    public bool IncludeDirectories { get; set; }
    public int Depth { get; set; } = 100;
}
