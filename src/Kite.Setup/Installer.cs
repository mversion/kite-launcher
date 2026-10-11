using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Kite.Setup;

public sealed record InstallOptions(bool Portable, string Folder, bool DesktopShortcut, bool StartWithWindows, bool LaunchWhenDone);

/// <summary>
/// Per-user install (no admin): files in %LocalAppData%\Programs\Kite, Start menu shortcut,
/// an entry in Settings &gt; Apps, optional autostart. Portable mode just unpacks the files and
/// drops a "portable" marker so Kite keeps everything in its own folder.
/// </summary>
public static class Installer
{
    public const string PayloadName = "Kite.payload.zip";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Kite";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string UninstallerName = "Uninstall Kite.exe";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo(PayloadName) is not null;

    public static string DefaultInstallFolder =>
        ExistingInstallFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Kite");

    /// <summary>A PortableApps folder on any drive if there is one, otherwise Documents.</summary>
    public static string DefaultPortableFolder
    {
        get
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var apps = Path.Combine(drive.RootDirectory.FullName, "PortableApps");
                    if (Directory.Exists(apps)) return Path.Combine(apps, "Kite");
                }
                catch (IOException) { }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kite Portable");
        }
    }

    public static string? ExistingInstallFolder
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;
        }
    }

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kite");

    private static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Kite.lnk");
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Kite.lnk");

    // ================================================================= install

    public static void Install(InstallOptions o, IProgress<(double Fraction, string Status)> progress)
    {
        var folder = Path.GetFullPath(o.Folder);
        var exe = Path.Combine(folder, "Kite.exe");

        progress.Report((0.05, "Closing Kite if it's running…"));
        CloseRunningKite();

        progress.Report((0.1, "Copying files…"));
        Directory.CreateDirectory(folder);
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName)
                             ?? throw new InvalidOperationException("This setup file doesn't contain Kite (it was built without a payload)."))
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                if (!target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue; // never write outside the chosen folder
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                progress.Report((0.1 + 0.7 * (i + 1) / entries.Count, $"Copying {entry.FullName}"));
            }
        }

        if (o.Portable)
        {
            File.WriteAllText(Path.Combine(folder, "portable"), "Kite keeps its settings, history and skins in this folder while this file exists.\r\n");
        }
        else
        {
            progress.Report((0.85, "Creating shortcuts…"));
            var portableMarker = Path.Combine(folder, "portable");
            if (File.Exists(portableMarker)) File.Delete(portableMarker);

            var self = Environment.ProcessPath;
            if (self is not null && !Path.GetFullPath(self).Equals(Path.Combine(folder, UninstallerName), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, Path.Combine(folder, UninstallerName), overwrite: true);

            ShellLink.Create(StartMenuShortcut, exe, "Kite - fast keystroke launcher", folder);
            if (o.DesktopShortcut) ShellLink.Create(DesktopShortcut, exe, "Kite - fast keystroke launcher", folder);
            else if (ShellLink.PointsInto(DesktopShortcut, folder)) File.Delete(DesktopShortcut);

            progress.Report((0.92, "Registering with Windows…"));
            RegisterUninstall(folder);
            SetAutostart(exe, o.StartWithWindows);
        }

        if (o.LaunchWhenDone)
        {
            progress.Report((0.97, "Starting Kite…"));
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = folder })?.Dispose();
        }
        progress.Report((1, "Done"));
    }

    private static void RegisterUninstall(string folder)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Kite");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "mversion");
        key.SetValue("DisplayIcon", Path.Combine(folder, "Kite.exe"));
        key.SetValue("InstallLocation", folder);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("UninstallString", $"\"{Path.Combine(folder, UninstallerName)}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var sizeKb = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
        key.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Writes the Run entry and tells Kite about the choice via setup.json (Kite owns the
    /// setting and would otherwise undo an autostart entry it didn't know about).
    /// </summary>
    private static void SetAutostart(string exe, bool enabled)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) run.SetValue("Kite", $"\"{exe}\"");
            else if ((run.GetValue("Kite") as string)?.Contains(Path.GetDirectoryName(exe)!, StringComparison.OrdinalIgnoreCase) == true)
                run.DeleteValue("Kite", throwOnMissingValue: false);
        }
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(Path.Combine(DataFolder, "setup.json"), new JsonObject { ["startWithWindows"] = enabled }.ToJsonString());
    }

    /// <summary>Asks a running Kite to exit ("Kite.exe --exit"), then makes sure it's gone.</summary>
    public static void CloseRunningKite()
    {
        var running = Process.GetProcessesByName("Kite");
        if (running.Length == 0) return;
        foreach (var p in running)
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null) Process.Start(new ProcessStartInfo(path, "--exit") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            }
            catch { /* fall through to kill */ }
        }
        foreach (var p in running)
        {
            try
            {
                if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(3000); }
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    // ================================================================= uninstall

    /// <summary>
    /// When started from inside the install folder, copies itself to %TEMP% and relaunches from
    /// there so the whole folder (including this exe) can be removed. Returns true if relaunched.
    /// </summary>
    public static bool RelaunchFromTempIfNeeded(string[] args)
    {
        if (args.Any(a => a == "--from-temp")) return false;
        var self = Environment.ProcessPath;
        if (self is null) return false;
        var folder = Path.GetDirectoryName(self)!;
        // Only needed when running from an installed copy (not e.g. a downloaded KiteSetup.exe).
        if (!File.Exists(Path.Combine(folder, "Kite.exe"))) return false;
        var temp = Path.Combine(Path.GetTempPath(), $"KiteUninstall-{Guid.NewGuid():N}.exe");
        File.Copy(self, temp);
        var passThrough = string.Join(' ', args.Where(a => a.StartsWith("--") && a != "--uninstall"));
        Process.Start(new ProcessStartInfo(temp, $"--uninstall --from-temp --folder \"{folder}\" {passThrough}".Trim()) { UseShellExecute = false })?.Dispose();
        return true;
    }

    public static string? FolderFromArgs(string[] args)
    {
        var i = Array.IndexOf(args, "--folder");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : ExistingInstallFolder;
    }

    public static void Uninstall(string folder, bool removeUserData, IProgress<(double Fraction, string Status)> progress)
    {
        progress.Report((0.1, "Closing Kite…"));
        CloseRunningKite();

        progress.Report((0.3, "Removing shortcuts and registry entries…"));
        foreach (var lnk in new[] { StartMenuShortcut, DesktopShortcut })
            if (ShellLink.PointsInto(lnk, folder)) File.Delete(lnk);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            if ((run?.GetValue("Kite") as string)?.Contains(folder, StringComparison.OrdinalIgnoreCase) == true)
                run!.DeleteValue("Kite", throwOnMissingValue: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        progress.Report((0.6, "Removing files…"));
        if (Directory.Exists(folder) && File.Exists(Path.Combine(folder, "Kite.exe")))
            Directory.Delete(folder, recursive: true);

        if (removeUserData && Directory.Exists(DataFolder))
        {
            progress.Report((0.85, "Moving your settings to the Recycle Bin…"));
            RecycleBin.Send(DataFolder);
        }
        progress.Report((1, "Done"));
    }

    /// <summary>Deletes this temporary uninstaller copy a moment after it exits.</summary>
    public static void ScheduleSelfDelete()
    {
        var self = Environment.ProcessPath;
        if (self is null || !self.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & del \"{self}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        })?.Dispose();
    }
}

internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public static void Send(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 3, // FO_DELETE
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = 0x40 | 0x10 | 0x4 | 0x400, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
        };
        SHFileOperation(ref op);
    }
}
