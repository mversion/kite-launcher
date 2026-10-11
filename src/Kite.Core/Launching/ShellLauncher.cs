using System.Diagnostics;
using Kite.Core.Catalog;
using Kite.Core.Native;
using Kite.Plugins;

namespace Kite.Core.Launching;

/// <summary>Launches files, shortcuts, URIs and Store apps.</summary>
public static class ShellLauncher
{
    public static void Launch(string target, string? arguments, LaunchMode mode)
    {
        arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments;

        if (mode == LaunchMode.OpenContainingFolder)
        {
            Reveal(target);
            return;
        }

        if (target.StartsWith(AppsFolderSource.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            LaunchAppsFolderItem(target[AppsFolderSource.Prefix.Length..], arguments, mode);
            return;
        }

        var psi = new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            Arguments = arguments ?? "",
        };
        // Shortcuts carry their own working directory; for plain files use the file's folder.
        if (!target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            psi.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
        if (mode == LaunchMode.RunAsAdmin) psi.Verb = "runas";
        Process.Start(psi)?.Dispose();
    }

    public static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

    /// <summary>Opens Explorer with the item selected.</summary>
    public static void Reveal(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
    }

    private static void LaunchAppsFolderItem(string id, string? arguments, LaunchMode mode)
    {
        // Packaged (Store) apps accept arguments through IApplicationActivationManager.
        if (AppsFolderSource.IsPackagedAppId(id) && mode != LaunchMode.RunAsAdmin)
        {
            try
            {
                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                manager.ActivateApplication(id, arguments, 0, out _);
                return;
            }
            catch (Exception)
            {
                // Fall back to the shell below.
            }
        }

        var psi = new ProcessStartInfo("explorer.exe", $"\"{AppsFolderSource.Prefix}{id}\"") { UseShellExecute = true };
        Process.Start(psi)?.Dispose();
    }
}
