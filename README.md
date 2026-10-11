# Kite

A fast, skinnable keystroke launcher for Windows, built as a modern take on Launchy.

## Install

Run `KiteSetup-<version>.exe` and choose one of these:

- **Install on this PC.** Installs for your user only, in `%LocalAppData%\Programs\Kite`, without administrator rights. It adds a Start menu shortcut and an uninstall entry in Settings › Apps. Start with Windows is optional. Settings are kept in `%AppData%\Kite`.
- **Portable.** Copies Kite to any folder, for example `P:\PortableApps\Kite`. Settings, history and skins stay in that folder, and nothing is written to the registry.

Alternatively, unzip `Kite-<version>-portable.zip` anywhere and run `Kite.exe`.

Kite needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). If it's missing, Windows offers to download it.

On first run, Kite imports your Launchy setup from `launchy.ini`: folders, hotkey, Weby sites, Runner commands, Calcy options and learned shortcuts. Launchy's files are only read, never changed.

## Command line

| Command | Effect |
|---|---|
| `Kite.exe` | Starts Kite, or shows it if it's already running |
| `Kite.exe --settings` | Opens the settings window |
| `Kite.exe --exit` | Closes a running Kite |
| `KiteSetup.exe --silent [--portable] [--folder "path"] [--desktop] [--no-autostart] [--no-launch]` | Installs without showing any windows |
| `"Uninstall Kite.exe" --uninstall --silent [--remove-data]` | Uninstalls without showing any windows (`--remove-data` sends your settings to the Recycle Bin) |

## Build

```powershell
.\publish.ps1            # tests, then publish\Kite, KiteSetup-<v>.exe and Kite-<v>-portable.zip
.\publish.ps1 -SelfContained   # bundles the .NET runtime (no prerequisite, much larger)
```

The solution contains these projects:

- `src/Kite.Core`: catalog, search, history, settings and skins
- `src/Kite.Plugin.Abstractions`: the plugin API
- `src/Kite.Plugins.Builtin`: Calcy, Weby and Runner
- `src/Kite.LaunchyImport`: the Launchy importer
- `src/Kite.App`: the WPF launcher and settings window
- `src/Kite.Setup`: the installer
- `tests/Kite.Tests`: the tests

## Memory

Kite idles in the tray, and its memory use depends on how recently you used it:

- **While it's open:** about 30 MB.
- **About 1.5 seconds after the launcher hides:** Kite returns freed .NET memory to Windows, and reopening is still instant.
- **After 2 minutes hidden:** Kite also trims its working set to a few MB, as shown in Task Manager.

Both behaviours are controlled by **Settings › General › Release memory while hidden**. Software rendering is on by default because it keeps the graphics driver out of the process.
