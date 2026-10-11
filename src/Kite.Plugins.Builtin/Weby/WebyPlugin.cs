using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kite.Plugins;

namespace Kite.Plugins.Builtin.Weby;

public sealed class WebSite
{
    public string Name { get; set; } = "";
    /// <summary>URL template; %1 is replaced by the query (Launchy-compatible).</summary>
    public string Query { get; set; } = "";
    public bool Default { get; set; }
}

/// <summary>
/// Web searches and e-mail. "google ⇥ cats" searches, "email ⇥ bob@x.com" composes a mail,
/// a typed URL opens directly, and unmatched text falls back to the default site.
/// E-mail addresses you've used are remembered and offered as completions.
/// </summary>
public sealed partial class WebyPlugin : IKitePlugin
{
    public const string PluginId = "weby";
    private const string FallbackPrefix = "fallback:";
    private const string UrlPrefix = "url:";
    private const int MaxRememberedAddresses = 200;

    private IPluginContext _ctx = null!;
    private List<WebSite> _sites = [];
    private List<string> _addresses = [];
    private readonly object _gate = new();

    public string Id => PluginId;
    public string Name => "Weby";
    public string Description => "Web searches, URLs and e-mail composition";

    public IReadOnlyList<WebSite> Sites => _sites;

    public static List<WebSite> DefaultSites() =>
    [
        new() { Name = "Google", Query = "https://www.google.com/search?q=%1", Default = true },
        new() { Name = "Bing", Query = "https://www.bing.com/search?q=%1" },
        new() { Name = "YouTube", Query = "https://www.youtube.com/results?search_query=%1" },
        new() { Name = "Wikipedia", Query = "https://en.wikipedia.org/wiki/Special:Search?search=%1&fulltext=Search" },
        new() { Name = "Amazon", Query = "https://www.amazon.com/s?k=%1" },
        new() { Name = "Maps", Query = "https://www.google.com/maps/search/%1" },
        new() { Name = "E-Mail", Query = "mailto:%1" },
    ];

    public void Init(IPluginContext context)
    {
        _ctx = context;
        var sitesNode = context.Settings["sites"];
        _sites = sitesNode is null
            ? DefaultSites()
            : sitesNode.Deserialize<List<WebSite>>(JsonOptions) ?? DefaultSites();
        if (sitesNode is null)
        {
            context.Settings["sites"] = JsonSerializer.SerializeToNode(_sites, JsonOptions);
            context.SaveSettings();
        }
        LoadAddresses();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IEnumerable<CatItem> GetCatalog() =>
        _sites.Where(s => s.Name.Length > 0).Select(s => new CatItem
        {
            Name = s.Name,
            Target = s.Name,
            Data = s.Query,
            Description = s.Query,
        });

    public Task<IReadOnlyList<CatItem>> GetResults(Query query, CancellationToken ct)
    {
        var results = new List<CatItem>();
        if (query.Owner is { } owner)
        {
            if (IsMail(owner.Data) && query.Text.Length > 0)
                results.AddRange(SuggestAddresses(query.Text, owner.Data!));
            return Task.FromResult<IReadOnlyList<CatItem>>(results);
        }

        var text = query.Text.Trim();
        if (text.Length == 0) return Task.FromResult<IReadOnlyList<CatItem>>(results);

        if (LooksLikeUrl(text))
        {
            var url = text.Contains("://") ? text : "https://" + text;
            results.Add(new CatItem { Name = $"Open {url}", Target = UrlPrefix + url, Description = "Open in browser", Score = 5 });
        }

        var def = DefaultSite;
        if (def is not null)
            results.Add(new CatItem
            {
                Name = $"Search {def.Name} for “{text}”",
                Target = FallbackPrefix + text,
                Data = def.Query,
                Description = def.Query.Replace("%1", text),
                Score = -100,
            });

        return Task.FromResult<IReadOnlyList<CatItem>>(results);
    }

    public bool Launch(CatItem item, Query query, LaunchMode mode)
    {
        if (item.Target.StartsWith(UrlPrefix, StringComparison.Ordinal))
        {
            _ctx.OpenUri(item.Target[UrlPrefix.Length..]);
            return true;
        }

        string? template;
        string argument;
        if (item.Target.StartsWith(FallbackPrefix, StringComparison.Ordinal))
        {
            template = item.Data;
            argument = item.Target[FallbackPrefix.Length..];
        }
        else if (query.Owner is { } owner && !string.Equals(owner.Key, item.Key, StringComparison.OrdinalIgnoreCase))
        {
            // A suggestion row (e.g. a remembered e-mail address) under an owner site.
            template = owner.Data;
            argument = item.Target;
        }
        else
        {
            template = item.Data ?? _sites.FirstOrDefault(s => s.Name == item.Target)?.Query;
            argument = query.ArgumentText;
        }

        if (string.IsNullOrEmpty(template)) return false;
        var url = BuildUrl(template, argument);
        if (IsMail(template) && argument.Contains('@')) RememberAddress(argument);
        _ctx.OpenUri(url);
        return true;
    }

    public static string BuildUrl(string template, string argument)
    {
        argument = argument.Trim();
        if (argument.Length == 0) return HomeUrl(template);
        var encoded = IsMail(template)
            ? argument.Replace(" ", "%20")
            : Uri.EscapeDataString(argument);
        return template.Contains("%1") ? template.Replace("%1", encoded) : template + encoded;
    }

    /// <summary>Launching a site without a query opens its home page (or a blank mail).</summary>
    public static string HomeUrl(string template)
    {
        if (IsMail(template)) return "mailto:";
        var bare = template.Replace("%1", "");
        return Uri.TryCreate(bare, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}/" : bare;
    }

    private static bool IsMail(string? template) => template?.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) == true;

    private WebSite? DefaultSite => _sites.FirstOrDefault(s => s.Default) ?? _sites.FirstOrDefault(s => !IsMail(s.Query));

    [GeneratedRegex(@"^(https?://\S+|www\.\S+\.\S+|[a-z0-9-]+(\.[a-z0-9-]+)*\.(com|net|org|io|dev|gov|edu|au|nz|uk|de|nl|co|app|ai)(/\S*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public static bool LooksLikeUrl(string text) => !text.Contains(' ') && UrlRegex().IsMatch(text);

    private IEnumerable<CatItem> SuggestAddresses(string text, string template)
    {
        List<string> snapshot;
        lock (_gate) snapshot = [.. _addresses];
        var t = text.Trim();
        return snapshot
            .Where(a => a.Contains(t, StringComparison.OrdinalIgnoreCase) && !a.Equals(t, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.StartsWith(t, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(8)
            .Select((a, i) => new CatItem
            {
                Name = a,
                Target = a,
                Data = template,
                Description = "Compose e-mail · Tab to complete",
                Score = 50 - i,
            });
    }

    private string AddressFile => Path.Combine(_ctx.DataDirectory, "addresses.json");

    private void LoadAddresses()
    {
        try
        {
            if (File.Exists(AddressFile))
                _addresses = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(AddressFile)) ?? [];
        }
        catch (Exception ex) { _ctx.Log($"Could not read addresses: {ex.Message}"); }
    }

    private void RememberAddress(string address)
    {
        lock (_gate)
        {
            _addresses.RemoveAll(a => a.Equals(address, StringComparison.OrdinalIgnoreCase));
            _addresses.Insert(0, address.Trim());
            if (_addresses.Count > MaxRememberedAddresses) _addresses.RemoveRange(MaxRememberedAddresses, _addresses.Count - MaxRememberedAddresses);
            try { File.WriteAllText(AddressFile, JsonSerializer.Serialize(_addresses)); }
            catch (Exception ex) { _ctx.Log($"Could not save addresses: {ex.Message}"); }
        }
    }

    /// <summary>Settings shape written by the Launchy importer.</summary>
    public static JsonObject CreateSettings(IEnumerable<WebSite> sites) =>
        new() { ["sites"] = JsonSerializer.SerializeToNode(sites.ToList(), JsonOptions) };
}
