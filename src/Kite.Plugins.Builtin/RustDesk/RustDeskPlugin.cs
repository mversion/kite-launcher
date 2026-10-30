using System.Text.RegularExpressions;
using Kite.Plugins;

namespace Kite.Plugins.Builtin.RustDesk;

/// <summary>A RustDesk session as RustDesk keeps it: config\peers\&lt;id&gt;.toml.</summary>
public sealed record RustDeskPeer(string Id, string? Alias, string? Username, string? Hostname, string? Platform)
{
    /// <summary>What you type to find it: the alias set in RustDesk, else the computer name, else the ID.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Alias) ? Alias.Trim() :
        !string.IsNullOrWhiteSpace(Hostname) ? Hostname.Trim() :
        RustDeskPlugin.FormatId(Id);
}

/// <summary>
/// Your RustDesk sessions in the catalog by computer name or alias: type "nuci7", press Enter, and
/// RustDesk connects to it (rustdesk.exe --connect &lt;id&gt;). Reads the session list RustDesk keeps;
/// saved passwords in those files are never read.
/// </summary>
public sealed partial class RustDeskPlugin : IKitePlugin, IDisposable
{
    public const string PluginId = "rustdesk";
    private IPluginContext _ctx = null!;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public string Id => PluginId;
    public string Name => "RustDesk";
    public string Description => "Connect to your RustDesk sessions by computer name or alias";

    /// <summary>Optional settings: "exePath" (rustdesk.exe) and "peersFolder" (RustDesk's config\peers).</summary>
    public string? ExePath => _ctx.Settings["exePath"]?.GetValue<string>() is { Length: > 0 } p ? p : LocateExecutable();
    public string PeersFolder => _ctx.Settings["peersFolder"]?.GetValue<string>() is { Length: > 0 } p ? Environment.ExpandEnvironmentVariables(p) : DefaultPeersFolder;

    public static string DefaultPeersFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustDesk", "config", "peers");

    public void Init(IPluginContext context)
    {
        _ctx = context;
        Dispose(); // plugins are re-initialised when their settings change
        WatchPeers();
    }

    public IEnumerable<CatItem> GetCatalog()
    {
        var exe = ExePath;
        return exe is null ? [] : CreateItems(ReadPeers(PeersFolder), exe);
    }

    public static IEnumerable<CatItem> CreateItems(IEnumerable<RustDeskPeer> peers, string exe) =>
        peers.Select(p => new CatItem
        {
            Name = p.DisplayName,
            Target = p.Id,
            Description = string.Join(" · ", new[]
            {
                "RustDesk",
                p.Username is { Length: > 0 } && p.Hostname is { Length: > 0 } ? $"{p.Username}@{p.Hostname}" : p.Hostname,
                $"ID {FormatId(p.Id)}",
            }.Where(s => !string.IsNullOrEmpty(s))),
            IconPath = exe,
            Data = exe,
        });

    public bool Launch(CatItem item, Query query, LaunchMode mode)
    {
        if (mode == LaunchMode.OpenContainingFolder || !IsSafeId(item.Target)) return false;
        var exe = item.Data is { Length: > 0 } d && File.Exists(d) ? d : ExePath;
        if (exe is null) return false;
        _ctx.RunProgram(exe, $"--connect {item.Target}");
        return true;
    }

    /// <summary>Every session file in the folder; unreadable or oddly named files are skipped.</summary>
    public static List<RustDeskPeer> ReadPeers(string folder)
    {
        var peers = new List<RustDeskPeer>();
        if (!Directory.Exists(folder)) return peers;
        foreach (var file in Directory.EnumerateFiles(folder, "*.toml"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!IsSafeId(id)) continue;
            try { peers.Add(ParsePeer(id, File.ReadLines(file))); }
            catch (IOException) { /* RustDesk is writing it; next rebuild picks it up */ }
            catch (UnauthorizedAccessException) { }
        }
        return peers;
    }

    /// <summary>
    /// Reads only the single-line string values it needs ([info] username/hostname/platform and
    /// [options] alias); everything else in the file, including saved passwords, is ignored.
    /// </summary>
    public static RustDeskPeer ParsePeer(string id, IEnumerable<string> lines)
    {
        string? section = null, alias = null, username = null, hostname = null, platform = null;
        foreach (var line in lines)
        {
            var header = SectionLine().Match(line);
            if (header.Success) { section = header.Groups[1].Value; continue; }
            var kv = StringValueLine().Match(line);
            if (!kv.Success) continue;
            var (key, value) = (kv.Groups[1].Value, kv.Groups[3].Value);
            switch (section, key)
            {
                case ("options", "alias"): alias = value; break;
                case ("info", "username"): username = value; break;
                case ("info", "hostname"): hostname = value; break;
                case ("info", "platform"): platform = value; break;
            }
        }
        return new RustDeskPeer(id, alias, username, hostname, platform);
    }

    /// <summary>"187922594" → "187 922 594", "21060257" → "21 060 257", as RustDesk shows them. Non-numeric IDs are unchanged.</summary>
    public static string FormatId(string id)
    {
        if (id.Length <= 3 || !id.All(char.IsAsciiDigit)) return id;
        var head = id.Length % 3;
        var groups = new List<string>();
        if (head > 0) groups.Add(id[..head]);
        for (var i = head; i < id.Length; i += 3) groups.Add(id.Substring(i, 3));
        return string.Join(' ', groups);
    }

    /// <summary>IDs go on a command line, so only plain ones are accepted (digits, letters, '-', '_').</summary>
    public static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && SafeId().IsMatch(id);

    /// <summary>The usual install locations of rustdesk.exe.</summary>
    public static string? LocateExecutable()
    {
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "RustDesk", "rustdesk.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>New or renamed sessions show up without waiting for the next scheduled rescan.</summary>
    private void WatchPeers()
    {
        var folder = PeersFolder;
        if (!Directory.Exists(folder)) return;
        try
        {
            _debounce = new Timer(_ => _ctx.RequestCatalogRebuild(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(folder, "*.toml") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler changed = (_, _) => _debounce.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            _watcher.Changed += changed; // e.g. an alias set in RustDesk
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (s, e) => changed(s, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _ctx.Log($"RustDesk: can't watch {folder}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _watcher = null;
        _debounce = null;
    }

    [GeneratedRegex(@"^\s*\[([^\]]+)\]\s*$")] private static partial Regex SectionLine();
    [GeneratedRegex(@"^\s*([A-Za-z0-9_]+)\s*=\s*(['""])(.*)\2\s*$")] private static partial Regex StringValueLine();
    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")] private static partial Regex SafeId();
}
