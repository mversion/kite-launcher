using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Kite.Core.Settings;
using Kite.Core.Updates;

namespace Kite.App.Services;

/// <summary>
/// Checks GitHub Releases of mversion/kite-launcher for a newer version and installs it in place:
/// downloads the installer, verifies its size and SHA-256 (as published by GitHub), then runs it in
/// silent update mode, which closes Kite, replaces the files (installed or portable) and restarts it.
/// </summary>
public sealed class UpdateService
{
    public const string Owner = "mversion";
    public const string Repo = "kite-launcher";
    public const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";
    private const string LatestApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public static Version CurrentVersion { get; } = Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KiteLauncher", CurrentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    });

    public UpdateInfo? Available { get; private set; }
    public bool IsChecking { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Asks GitHub for the latest release. Returns the update if it's newer than this build.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        IsChecking = true;
        LastError = null;
        try
        {
            using var response = await Http.Value.GetAsync(LatestApi, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Available = null; // no releases published yet
                return null;
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            Available = GitHubRelease.Parse(await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false), CurrentVersion, ReleasesPage);
            Logger.Log(Available is null ? $"Update check: {CurrentVersion.ToString(3)} is the latest" : $"Update check: {Available.Version} is available");
            return Available;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LastError = ex is TaskCanceledException ? "GitHub didn't answer in time" : ex.Message;
            Logger.Log($"Update check failed: {LastError}");
            return null;
        }
        finally { IsChecking = false; }
    }

    /// <summary>Downloads the installer to %TEMP% and verifies it. Throws if it doesn't match what GitHub published.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!update.DownloadUrl.StartsWith($"https://github.com/{Owner}/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected download location - update cancelled.");

        var folder = Path.Combine(Path.GetTempPath(), "KiteLauncherUpdate");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, update.AssetName);

        using (var response = await Http.Value.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (update.Size > 0) progress?.Report(Math.Min(1, (double)done / update.Size));
            }
        }

        var size = new FileInfo(target).Length;
        if (update.Size > 0 && size != update.Size)
        {
            File.Delete(target);
            throw new InvalidDataException($"The download is incomplete ({size:N0} of {update.Size:N0} bytes).");
        }
        if (update.Sha256 is { } expected)
        {
            await using var check = File.OpenRead(target);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != expected)
            {
                check.Close();
                File.Delete(target);
                throw new InvalidDataException("The download doesn't match the checksum GitHub published - update cancelled.");
            }
        }
        else Logger.Log("Update has no published SHA-256; verified size only");
        return target;
    }

    /// <summary>
    /// Runs the installer in update mode. It asks this Kite to exit, replaces the files in the same
    /// folder (keeping installed/portable mode, shortcuts and autostart as they are) and restarts Kite.
    /// </summary>
    public static void RunInstaller(string setupPath, KitePaths paths)
    {
        var args = $"--silent --update --folder \"{paths.AppDirectory}\"" + (paths.IsPortable ? " --portable" : "");
        Logger.Log($"Starting update: {Path.GetFileName(setupPath)} {args}");
        Process.Start(new ProcessStartInfo(setupPath, args) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() })?.Dispose();
    }
}
