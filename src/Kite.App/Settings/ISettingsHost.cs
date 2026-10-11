using Kite.Core.Skins;

namespace Kite.App.Settings;

/// <summary>What the settings window needs from the running app to apply changes live.</summary>
public interface ISettingsHost
{
    KiteServices Services { get; }

    SkinLibrary SkinLibrary { get; }
    void ApplySkin(string name);
    void ReloadSkin();

    /// <summary>Registers a new global hotkey; returns an error message or null on success.</summary>
    string? ApplyHotkey(string text);
    /// <summary>Temporarily releases the hotkey so the hotkey box can capture it.</summary>
    void SuspendHotkey(bool suspend);

    /// <summary>Re-applies tray, topmost, autostart and launcher layout settings.</summary>
    void ApplyRuntime();
    /// <summary>Re-initialises plugins and refreshes their catalog entries (no disk rescan).</summary>
    void PluginsChanged();
    void Rescan();
    void ImportLaunchy();
    void OpenSettingsJson();
    void OpenFolder(string path);
    void OpenFile(string path);
}
