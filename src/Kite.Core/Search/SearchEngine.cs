using Kite.Core.Catalog;
using Kite.Core.Plugins;
using Kite.Plugins;

namespace Kite.Core.Search;

/// <summary>Combines catalog matching, learned history and plugin results into one ranked list.</summary>
public sealed class SearchEngine
{
    private const int LearnedExactBoost = 100_000;
    private const int LearnedPrefixBoost = 20_000;

    private readonly Func<CatalogSnapshot> _catalog;
    private readonly History _history;
    private readonly PluginHost _plugins;

    public SearchEngine(Func<CatalogSnapshot> catalog, History history, PluginHost plugins)
    {
        _catalog = catalog;
        _history = history;
        _plugins = plugins;
    }

    /// <summary>Synchronous catalog search; fast enough to run on every keystroke.</summary>
    public List<CatItem> SearchCatalog(string text, int max)
    {
        var q = History.Normalize(text);
        if (q.Length == 0) return [];
        var snapshot = _catalog();

        // Learned prefixes: exact hit wins outright, longer learned prefixes ("note" while typing "no") get a boost.
        var boosts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (prefix, entry) in _history.PrefixesStartingWith(q))
        {
            var b = prefix.Length == q.Length
                ? LearnedExactBoost
                : LearnedPrefixBoost + Math.Min(entry.Count, 100) * 10 - (prefix.Length - q.Length) * 100;
            if (!boosts.TryGetValue(entry.Key, out var existing) || b > existing) boosts[entry.Key] = b;
        }

        var now = DateTime.UtcNow;
        var scored = new List<(CatItem Item, int Score)>(64);
        foreach (var e in snapshot.Entries)
        {
            var s = Matcher.Score(e, q);
            boosts.TryGetValue(e.Item.Key, out var boost);
            if (s == 0 && boost == 0) continue;
            scored.Add((e.Item, s * 10 + boost + UsageBonus(e.Item.Key, now)));
        }

        // A learned target that isn't indexed (e.g. its folder was removed from the catalog) still works if it exists.
        foreach (var (key, boost) in boosts)
        {
            if (snapshot.ByKey.ContainsKey(key) || !Path.IsPathRooted(key)) continue;
            if (File.Exists(key) || Directory.Exists(key))
                scored.Add((DirectoryScanner.ItemForFile(key), boost));
        }

        scored.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : string.Compare(a.Item.Name, b.Item.Name, StringComparison.OrdinalIgnoreCase));
        var result = new List<CatItem>(Math.Min(max, scored.Count));
        for (var i = 0; i < scored.Count && result.Count < max; i++) result.Add(scored[i].Item);
        return result;
    }

    private int UsageBonus(string key, DateTime now)
    {
        var usage = _history.GetUsage(key);
        if (usage is null) return 0;
        var age = now - usage.LastUsed;
        var recency = age < TimeSpan.FromDays(3) ? 150 : age < TimeSpan.FromDays(30) ? 50 : 0;
        return Math.Min(usage.Count, 100) * 4 + recency;
    }

    /// <summary>Asynchronous plugin results for the query (all plugins, or only the owner's plugin).</summary>
    public Task<IReadOnlyList<CatItem>> SearchPlugins(Query query, CancellationToken ct) => _plugins.QueryAsync(query, ct);

    /// <summary>
    /// Final ordering without an owner: positive-score plugin results, then catalog matches, then
    /// the remaining plugin results (fallbacks such as "Search Google for ...").
    /// </summary>
    public static List<CatItem> Merge(IReadOnlyList<CatItem> catalog, IReadOnlyList<CatItem> plugin, int max)
    {
        var result = new List<CatItem>(catalog.Count + plugin.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plugin.Where(p => p.Score > 0).OrderByDescending(p => p.Score))
            if (seen.Add(p.Key)) result.Add(p);
        foreach (var c in catalog)
        {
            if (result.Count >= max) break;
            if (seen.Add(c.Key)) result.Add(c);
        }
        foreach (var p in plugin.Where(p => p.Score <= 0).OrderByDescending(p => p.Score))
            if (seen.Add(p.Key)) result.Add(p);
        return result;
    }

    /// <summary>
    /// Ordering with an owner chip: suggestions the plugin is confident about, then the owner itself
    /// (Enter = launch owner with the typed arguments), then other suggestions.
    /// </summary>
    public static List<CatItem> MergeOwner(CatItem owner, Query query, IReadOnlyList<CatItem> plugin)
    {
        var ownerRow = owner with
        {
            Description = query.ArgumentText.Length == 0 ? owner.Description : $"{owner.Name}  ▸  {query.ArgumentText}",
        };
        var result = new List<CatItem>();
        result.AddRange(plugin.Where(p => p.Score > 0).OrderByDescending(p => p.Score));
        result.Add(ownerRow);
        result.AddRange(plugin.Where(p => p.Score <= 0).OrderByDescending(p => p.Score));
        return result;
    }
}
