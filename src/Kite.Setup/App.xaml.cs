using System.IO;
using System.Windows;

namespace Kite.Setup;

/// <summary>
/// Interactive by default. Silent use:
///   KiteSetup.exe --silent [--portable] [--folder "path"] [--desktop] [--no-autostart] [--no-launch]
///   "Uninstall Kite.exe" --uninstall --silent [--remove-data]
/// Errors from silent runs go to %TEMP%\KiteSetup.log and the exit code is non-zero.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var args = e.Args;
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        var uninstall = Has("--uninstall");

        // The uninstaller lives inside the folder it deletes, so it re-runs itself from %TEMP%.
        if (uninstall && Installer.RelaunchFromTempIfNeeded(args))
        {
            Shutdown();
            return;
        }

        if (Has("--silent"))
        {
            Shutdown(RunSilent(args, uninstall, Has));
            return;
        }

        var window = new SetupWindow(uninstall ? SetupMode.Uninstall : SetupMode.Install, args);
        window.Closed += (_, _) => Shutdown();
        MainWindow = window;
        window.Show();
    }

    private static int RunSilent(string[] args, bool uninstall, Func<string, bool> has)
    {
        var progress = new Progress<(double, string)>();
        try
        {
            if (uninstall)
            {
                var folder = Installer.FolderFromArgs(args) ?? throw new InvalidOperationException("Kite isn't installed.");
                Installer.Uninstall(folder, has("--remove-data"), progress);
                Installer.ScheduleSelfDelete();
            }
            else
            {
                var portable = has("--portable");
                var folderIndex = Array.FindIndex(args, a => a.Equals("--folder", StringComparison.OrdinalIgnoreCase));
                var folder = folderIndex >= 0 && folderIndex + 1 < args.Length
                    ? args[folderIndex + 1]
                    : portable ? Installer.DefaultPortableFolder : Installer.DefaultInstallFolder;
                Installer.Install(new InstallOptions(portable, folder, has("--desktop"), !portable && !has("--no-autostart"), !has("--no-launch")), progress);
            }
            return 0;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "KiteSetup.log"), $"{DateTime.Now:s} {ex}\r\n"); } catch { }
            return 1;
        }
    }
}
