# Kite Launcher

A fast, skinnable keystroke launcher for Windows, built as a modern take on Launchy. By Bilgin Ozkan.

## Install

Download `KiteLauncherSetup-<version>.exe` from the [releases page](https://github.com/mversion/kite-launcher/releases), run it, and choose one of these:

- **Install on this PC.** Installs for your user only, in `%LocalAppData%\Programs\Kite`, without administrator rights. It adds a Start menu shortcut and an uninstall entry in Settings › Apps. Start with Windows is optional. Settings are kept in `%AppData%\Kite`.
- **Portable.** Copies Kite Launcher to any folder, for example `P:\PortableApps\Kite`. Settings, history and skins stay in that folder, and nothing is written to the registry.

Alternatively, unzip `KiteLauncher-<version>-portable.zip` anywhere and run `Kite.exe`.

Kite Launcher needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). If it's missing, Windows offers to download it.

On first run, Kite imports your Launchy setup from `launchy.ini`: folders, hotkey, Weby sites, Runner commands, Calcy options and learned shortcuts. Launchy's files are only read, never changed. If Launchy is still running with the same hotkey, setup offers to close it, and Kite takes over the hotkey as soon as it's free.

## Updates

Kite Launcher checks GitHub about once a day and shows a notification when a new version is out. To update:

1. Open **Settings › About** and click **Update now**.
2. Kite downloads the installer from this repository's releases and checks its size and SHA-256 against the values GitHub publishes.
3. Kite installs the new version over the current copy, whether it's installed or portable. Your shortcuts and start-with-Windows choice stay as they are.
4. Kite restarts.

Nothing is installed without your click, and automatic checks can be turned off in the same place.

## Command line

| Command | Effect |
|---|---|
| `Kite.exe` | Starts Kite, or shows it if it's already running |
| `Kite.exe --settings` | Opens the settings window |
| `Kite.exe --exit` | Closes a running Kite |
| `KiteLauncherSetup.exe --silent [--portable] [--folder "path"] [--desktop] [--no-autostart] [--no-launch] [--close-launchy]` | Installs without showing any windows (`--close-launchy` closes Launchy if it holds the same hotkey) |
| `KiteLauncherSetup.exe --silent --update --folder "path" [--portable]` | Updates in place, keeping shortcuts and autostart (this is what the built-in updater runs) |
| `"Uninstall Kite.exe" --uninstall --silent [--remove-data]` | Uninstalls without showing any windows (`--remove-data` sends your settings to the Recycle Bin) |

## Build

```powershell
.\publish.ps1                  # tests, then publish\Kite, KiteLauncherSetup-<v>.exe and KiteLauncher-<v>-portable.zip
.\publish.ps1 -SelfContained   # bundles the .NET runtime (no prerequisite, much larger)
```

To release a new version:

1. Bump `<Version>` in `Directory.Build.props`.
2. Run `.\publish.ps1`.
3. Create a GitHub release tagged `v<version>` and attach the two files from `publish\`. Installed copies will offer the update.

The solution contains these projects:

- `src/Kite.Core`: catalog, search, history, settings, skins and update parsing
- `src/Kite.Plugin.Abstractions`: the plugin API
- `src/Kite.Plugins.Builtin`: Calcy, Weby, Runner and Everything
- `src/Kite.LaunchyImport`: the Launchy importer
- `src/Kite.App`: the WPF launcher, settings window and updater
- `src/Kite.Setup`: the installer
- `tests/Kite.Tests`: the tests

## Memory

Kite idles in the tray, and its memory use depends on how recently you used it:

- **While it's open:** about 30 MB.
- **About 1.5 seconds after the launcher hides:** Kite returns freed .NET memory to Windows, and reopening is still instant.
- **After 2 minutes hidden:** Kite also trims its working set to a few MB, as shown in Task Manager.

Both behaviours are controlled by **Settings › General › Release memory while hidden**. Software rendering is on by default because it keeps the graphics driver out of the process.
