using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kite.Setup;

/// <summary>
/// Works out which hotkey Kite will use after setup, and whether Launchy is holding it - so setup
/// can say the right thing instead of promising Alt+Space.
/// </summary>
public static partial class Hotkeys
{
    public const string Default = "Alt+Space";

    private static string LaunchyIni =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Launchy", "launchy.ini");

    /// <summary>
    /// Kite keeps an existing setting; on a first run it imports Launchy's hotkey; otherwise Alt+Space.
    /// </summary>
    public static string Expected(bool portable, string folder)
    {
        var settings = portable ? Path.Combine(folder, "settings.json") : Path.Combine(Installer.DataFolder, "settings.json");
        try
        {
            if (File.Exists(settings) && JsonNode.Parse(File.ReadAllText(settings))?["general"]?["hotkey"]?.GetValue<string>() is { Length: > 0 } existing)
                return existing;
        }
        catch { /* unreadable settings - fall through */ }
        return LaunchyHotkey() ?? Default;
    }

    public static bool LaunchyRunning => Process.GetProcessesByName("Launchy").Length > 0;

    /// <summary>Launchy's hotkey from launchy.ini, e.g. "Ctrl+Space", or null if Launchy isn't set up.</summary>
    public static string? LaunchyHotkey()
    {
        try
        {
            if (!File.Exists(LaunchyIni)) return null;
            var m = HotkeyLine().Match(File.ReadAllText(LaunchyIni));
            return m.Success && int.TryParse(m.Groups[1].Value, out var code) ? FromQt(code) : null;
        }
        catch { return null; }
    }

    /// <summary>Qt key-sequence integer (Launchy's format) → "Ctrl+Space"-style text.</summary>
    public static string? FromQt(int code)
    {
        var key = code & 0x01FFFFFF;
        string? name = key switch
        {
            0x20 => "Space",
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
            0x60 => "`",
            >= 0x01000030 and <= 0x01000047 => "F" + (key - 0x01000030 + 1),
            _ => null,
        };
        if (name is null) return null;
        var parts = new List<string>();
        if ((code & 0x04000000) != 0) parts.Add("Ctrl");
        if ((code & 0x08000000) != 0) parts.Add("Alt");
        if ((code & 0x02000000) != 0) parts.Add("Shift");
        if ((code & 0x10000000) != 0) parts.Add("Win");
        parts.Add(name);
        return string.Join('+', parts);
    }

    /// <summary>True when Launchy is running and owns the hotkey Kite is going to use.</summary>
    public static bool ConflictsWithLaunchy(string hotkey) =>
        LaunchyRunning && string.Equals(LaunchyHotkey(), hotkey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks Launchy to close, then makes sure it has (it lives in the tray, so it may need a push).</summary>
    public static void CloseLaunchy()
    {
        foreach (var p in Process.GetProcessesByName("Launchy"))
        {
            try
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); }
            }
            catch { /* already gone or not ours to close */ }
            finally { p.Dispose(); }
        }
    }

    [GeneratedRegex(@"(?im)^\s*hotkey\s*=\s*(\d+)\s*$")]
    private static partial Regex HotkeyLine();
}
