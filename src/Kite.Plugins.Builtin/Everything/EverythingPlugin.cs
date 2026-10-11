using System.Diagnostics;
using System.Text.Json.Nodes;
using Kite.Plugins;

namespace Kite.Plugins.Builtin.Everything;

public sealed class EverythingOptions
{
    public int MaxResults { get; set; } = 30;
    /// <summary>Typing this prefix searches Everything without Tab ("?invoice"). Empty turns it off.</summary>
    public string Prefix { get; set; } = "?";
    /// <summary>When true, Enter opens the Everything window and the file results come second.</summary>
    public bool EnterOpensEverything { get; set; }
    public bool MatchPath { get; set; }
    public EverythingSort Sort { get; set; } = EverythingSort.NameAscending;
}

/// <summary>
/// Inline file search powered by Everything (voidtools). Works on top of the existing Everything
/// shortcut: "e ⇥ invoice" lists matching files live as you type. Enter opens the selected file,
/// Shift+Enter opens the Everything window with the same search, Ctrl+Enter shows it in Explorer.
/// </summary>
public sealed class EverythingPlugin : IKitePlugin
{
    public const string PluginId = "everything";
    private const string SearchTarget = "everything:search";
    private const string OpenTarget = "everything:open";
    private const string StartTarget = "everything:start";
    private const string ResultTag = "result";

    private readonly Func<IEverythingClient> _clientFactory;
    private IEverythingClient? _client;
    private IPluginContext _ctx = null!;

    public EverythingPlugin() : this(() => new EverythingClient()) { }

    /// <summary>For tests: supply a fake client.</summary>
    public EverythingPlugin(Func<IEverythingClient> clientFactory) => _clientFactory = clientFactory;

    public string Id => PluginId;
    public string Name => "Everything";
    public string Description => "Instant file search via Everything - results appear right in Kite";
    public EverythingOptions Options { get; private set; } = new();

    private IEverythingClient Client => _client ??= _clientFactory();

    public void Init(IPluginContext context)
    {
        _ctx = context;
        Options = ReadOptions(context.Settings);
    }

    public static EverythingOptions ReadOptions(JsonObject s) => new()
    {
        MaxResults = Math.Clamp(s["maxResults"]?.GetValue<int>() ?? 30, 1, 500),
        Prefix = s["prefix"]?.GetValue<string>() ?? "?",
        EnterOpensEverything = s["enterOpensEverything"]?.GetValue<bool>() ?? false,
        MatchPath = s["matchPath"]?.GetValue<bool>() ?? false,
        Sort = Enum.TryParse<EverythingSort>(s["sort"]?.GetValue<string>(), true, out var sort) ? sort : EverythingSort.NameAscending,
    };

    public IEnumerable<CatItem> GetCatalog() =>
    [
        new CatItem
        {
            Name = "Everything search",
            Target = SearchTarget,
            Description = "Search files and folders with Everything - press Tab, then type",
        },
    ];

    /// <summary>
    /// The user's own Everything shortcut/exe (e.g. learned as "e") also acts as the search owner,
    /// as does a custom command (Runner) that launches Everything.exe.
    /// </summary>
    public bool HandlesOwner(CatItem owner)
    {
        if (owner.PluginId == PluginId) return true;
        if (owner.PluginId is not null)
            return IsEverythingExe(owner.Data) || IsEverythingExe(owner.IconPath);
        var file = Path.GetFileName(owner.Target);
        return file.Equals("Everything.exe", StringComparison.OrdinalIgnoreCase)
            || file.Equals("Everything.lnk", StringComparison.OrdinalIgnoreCase)
            || (owner.Name.Equals("Everything", StringComparison.OrdinalIgnoreCase) &&
                (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsEverythingExe(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetFileName(path.Trim('"')).Equals("Everything.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The search text for this query, or null when the query isn't meant for Everything.</summary>
    public string? SearchTextFor(Query query)
    {
        if (query.Owner is { } owner) return HandlesOwner(owner) ? query.ArgumentText.Trim() : null;
        var prefix = Options.Prefix;
        if (prefix.Length > 0 && query.Text.StartsWith(prefix, StringComparison.Ordinal))
            return query.Text[prefix.Length..].Trim();
        return null;
    }

    public async Task<IReadOnlyList<CatItem>> GetResults(Query query, CancellationToken ct)
    {
        var search = SearchTextFor(query);
        if (search is null) return [];
        var prefixMode = query.Owner is null;

        if (!Client.IsAvailable)
            return [new CatItem { Name = "Everything isn't running", Target = StartTarget, Description = "Press Enter to start Everything", Score = 2000 }];

        if (search.Length == 0)
            return prefixMode ? [new CatItem { Name = "Search with Everything…", Target = OpenTarget, Description = "Type what you're looking for", Score = 2000 }] : [];

        // Let fast typists finish a word before hitting Everything (it's cancelled by the next keystroke).
        await Task.Delay(40, ct).ConfigureAwait(false);
        var found = await Client.SearchAsync(search, Options.MaxResults, Options.Sort, Options.MatchPath, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var items = new List<CatItem>(found.Count + 1);
        for (var i = 0; i < found.Count; i++)
        {
            var r = found[i];
            items.Add(new CatItem
            {
                Name = r.Name,
                Target = r.FullPath,
                Description = r.IsFolder ? $"Folder · {r.Folder}" : r.Folder,
                Detail = FormatDetail(r.Modified, r.Size, DateTime.Now),
                IconPath = r.FullPath,
                Data = ResultTag,
                // Positive scores list above the "open in Everything" row; negative below it.
                Score = Options.EnterOpensEverything ? -(i + 1) : 1000 - i,
            });
        }

        if (prefixMode)
            items.Add(new CatItem
            {
                Name = $"Search Everything for “{search}”",
                Target = OpenTarget,
                Description = found.Count == 0 ? "No matches here - open Everything (Shift+Enter)" : "Open the Everything window (Shift+Enter)",
                Score = Options.EnterOpensEverything ? 2000 : -1000,
            });
        else if (found.Count == 0)
            items.Add(new CatItem { Name = "No matching files", Target = OpenTarget, Description = "Enter opens Everything with this search", Score = -1 });
        return items;
    }

    public bool Launch(CatItem item, Query query, LaunchMode mode)
    {
        var search = SearchTextFor(query) ?? "";

        if (item.Target == StartTarget)
        {
            var exe = OwnerExecutable(query) ?? EverythingClient.LocateExecutable();
            if (exe is null) return false;
            _ctx.RunProgram(exe);
            return true;
        }

        var isResult = item.Data == ResultTag;
        if (!isResult || mode == LaunchMode.Alternate)
        {
            // The owner row, the "open" row, or Shift+Enter on a result: show it in Everything.
            var exe = OwnerExecutable(query) ?? EverythingClient.LocateExecutable();
            if (exe is null) return false;
            _ctx.RunProgram(exe, search.Length > 0 ? $"-search \"{search.Replace("\"", "\\\"")}\"" : null);
            return true;
        }

        switch (mode)
        {
            case LaunchMode.OpenContainingFolder:
                Process.Start("explorer.exe", $"/select,\"{item.Target}\"")?.Dispose();
                break;
            case LaunchMode.RunAsAdmin:
                _ctx.RunProgram(item.Target, null, runAsAdmin: true);
                break;
            default:
                _ctx.OpenUri(item.Target);
                break;
        }
        return true;
    }

    /// <summary>"Today 14:32 · 1.2 MB", "Yesterday 09:10", "Mon 16:05", "3 Oct 14:32", "3 Oct 2024".</summary>
    public static string? FormatDetail(DateTime? modified, long? size, DateTime now)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string? when = null;
        if (modified is { } m)
        {
            var time = m.ToString("t", culture);
            var days = (now.Date - m.Date).Days;
            when = days switch
            {
                0 => $"Today {time}",
                1 => $"Yesterday {time}",
                > 1 and < 7 => $"{m.ToString("ddd", culture)} {time}",
                _ when m.Year == now.Year => $"{m.ToString("d MMM", culture)} {time}",
                _ => m.ToString("d MMM yyyy", culture),
            };
        }
        var bytes = size is { } s ? FormatSize(s) : null;
        return (when, bytes) switch
        {
            (null, null) => null,
            (null, _) => bytes,
            (_, null) => when,
            _ => $"{when}  ·  {bytes}",
        };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    /// <summary>Prefer the user's own Everything shortcut (the owner chip) so portable installs work too.</summary>
    private string? OwnerExecutable(Query query) =>
        query.Owner is { PluginId: null } owner && HandlesOwner(owner) && File.Exists(owner.Target) ? owner.Target : null;

    public static JsonObject CreateSettings(EverythingOptions o) => new()
    {
        ["maxResults"] = o.MaxResults,
        ["prefix"] = o.Prefix,
        ["enterOpensEverything"] = o.EnterOpensEverything,
        ["matchPath"] = o.MatchPath,
        ["sort"] = o.Sort.ToString(),
    };
}
