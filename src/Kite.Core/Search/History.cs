using System.Text.RegularExpressions;
using Kite.Core.Settings;
using Kite.Plugins;

namespace Kite.Core.Search;

/// <summary>
/// Launch history: Launchy-style learned prefixes ("e" → Everything) plus usage counts.
/// Launching item X after typing "p" teaches p → X; next time "p" puts X first.
/// </summary>
public sealed class History
{
    private readonly object _gate = new();
    private HistoryData _data = new();

    public sealed class HistoryData
    {
        /// <summary>Lower-cased typed text → learned item.</summary>
        public Dictionary<string, LearnedPrefix> Prefixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Item key → usage.</summary>
        public Dictionary<string, Usage> Usage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Owner key + search text ("everything ⇥ password reset") → the result picked for it.</summary>
        public Dictionary<string, Pick> Picks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A result chosen from an owner's results (e.g. a file from "e ⇥ invoice"), enough to show it again.</summary>
    public sealed class Pick
    {
        public string Search { get; set; } = "";
        public string Name { get; set; } = "";
        public string Target { get; set; } = "";
        public string? Description { get; set; }
        public string? PluginId { get; set; }
        public string? Data { get; set; }
        public int Count { get; set; }
        public DateTime LastUsed { get; set; }

        public string ItemKey => PluginId is null ? Target : $"{PluginId}:{Target}";

        public CatItem ToItem() => new()
        {
            Name = Name, Target = Target, Description = Description, PluginId = PluginId, Data = Data, IconPath = Target,
        };
    }

    public sealed class LearnedPrefix
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public DateTime LastUsed { get; set; }
        /// <summary>Pinned aliases are never overwritten by learning or pruned.</summary>
        public bool Pinned { get; set; }
    }

    public sealed class Usage
    {
        public int Count { get; set; }
        public DateTime LastUsed { get; set; }
    }

    public int MaxPrefixes { get; set; } = 500;

    public void Load(string path)
    {
        var loaded = Json.Load<HistoryData>(path);
        lock (_gate)
        {
            _data = loaded ?? new HistoryData();
            // JSON loses the comparer; rebuild case-insensitive dictionaries.
            _data.Prefixes = new(_data.Prefixes, StringComparer.OrdinalIgnoreCase);
            _data.Usage = new(_data.Usage, StringComparer.OrdinalIgnoreCase);
            _data.Picks = new(_data.Picks ?? new(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(string path)
    {
        lock (_gate) Json.Save(path, _data);
    }

    public static string Normalize(string typed) => typed.Trim().ToLowerInvariant();

    public LearnedPrefix? Lookup(string typed)
    {
        lock (_gate) return _data.Prefixes.GetValueOrDefault(Normalize(typed));
    }

    public Usage? GetUsage(string key)
    {
        lock (_gate) return _data.Usage.GetValueOrDefault(key);
    }

    /// <summary>Learned items whose prefix key starts with <paramref name="typedLower"/> (e.g. "no" → "not", "note").</summary>
    public List<(string Prefix, LearnedPrefix Entry)> PrefixesStartingWith(string typedLower)
    {
        lock (_gate)
            return _data.Prefixes
                .Where(p => p.Key.StartsWith(typedLower, StringComparison.Ordinal))
                .Select(p => (p.Key, p.Value))
                .ToList();
    }

    public IReadOnlyList<KeyValuePair<string, LearnedPrefix>> AllPrefixes()
    {
        lock (_gate) return _data.Prefixes.ToList();
    }

    public void Record(string typed, string key, string name, DateTime? when = null)
    {
        var now = when ?? DateTime.UtcNow;
        var prefix = Normalize(typed);
        lock (_gate)
        {
            if (!_data.Usage.TryGetValue(key, out var usage))
                _data.Usage[key] = usage = new Usage();
            usage.Count++;
            usage.LastUsed = now;

            if (prefix.Length == 0) return;
            if (_data.Prefixes.TryGetValue(prefix, out var existing))
            {
                if (existing.Pinned && !string.Equals(existing.Key, key, StringComparison.OrdinalIgnoreCase)) return;
                if (string.Equals(existing.Key, key, StringComparison.OrdinalIgnoreCase)) existing.Count++;
                else { existing.Key = key; existing.Name = name; existing.Count = 1; }
                existing.LastUsed = now;
            }
            else
            {
                _data.Prefixes[prefix] = new LearnedPrefix { Key = key, Name = name, Count = 1, LastUsed = now };
                Prune();
            }
        }
    }

    /// <summary>Search text as remembered for picks: trimmed, lower-case, single spaces.</summary>
    public static string NormalizeSearch(string text) => Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string PickKey(string ownerKey, string search) => $"{ownerKey}\u001f{search}";

    /// <summary>Remembers that <paramref name="item"/> was chosen for <paramref name="search"/> under this owner.</summary>
    public void RecordPick(string ownerKey, string search, CatItem item, DateTime? when = null)
    {
        var text = NormalizeSearch(search);
        if (text.Length == 0) return;
        lock (_gate)
        {
            var key = PickKey(ownerKey, text);
            if (_data.Picks.TryGetValue(key, out var existing) && string.Equals(existing.ItemKey, item.Key, StringComparison.OrdinalIgnoreCase))
                existing.Count++;
            else
                _data.Picks[key] = existing = new Pick { Search = text, Count = 1 };
            existing.Name = item.Name;
            existing.Target = item.Target;
            existing.Description = item.Description;
            existing.PluginId = item.PluginId;
            existing.Data = item.Data;
            existing.LastUsed = when ?? DateTime.UtcNow;
            PrunePicks();
        }
    }

    /// <summary>The result picked for exactly this search under this owner, if any.</summary>
    public Pick? LookupPick(string ownerKey, string search)
    {
        lock (_gate) return _data.Picks.GetValueOrDefault(PickKey(ownerKey, NormalizeSearch(search)));
    }

    /// <summary>Picks for longer searches that start with <paramref name="search"/> ("pass" → "password reset"), most used first.</summary>
    public List<Pick> PicksStartingWith(string ownerKey, string search)
    {
        var prefix = PickKey(ownerKey, NormalizeSearch(search));
        lock (_gate)
            return _data.Picks
                .Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value)
                .OrderByDescending(p => p.Count).ThenByDescending(p => p.LastUsed)
                .ToList();
    }

    private void PrunePicks()
    {
        if (_data.Picks.Count <= MaxPrefixes) return;
        foreach (var victim in _data.Picks.OrderBy(p => p.Value.LastUsed).Take(_data.Picks.Count - MaxPrefixes).Select(p => p.Key).ToList())
            _data.Picks.Remove(victim);
    }

    /// <summary>Sets (or replaces) an alias explicitly, e.g. from the importer or the settings UI.</summary>
    public void SetPrefix(string typed, string key, string name, bool pinned = false, DateTime? when = null)
    {
        lock (_gate)
            _data.Prefixes[Normalize(typed)] = new LearnedPrefix
            {
                Key = key, Name = name, Count = 1, Pinned = pinned, LastUsed = when ?? DateTime.UtcNow,
            };
    }

    public bool RemovePrefix(string typed)
    {
        lock (_gate) return _data.Prefixes.Remove(Normalize(typed));
    }

    private void Prune()
    {
        if (_data.Prefixes.Count <= MaxPrefixes) return;
        var victims = _data.Prefixes
            .Where(p => !p.Value.Pinned)
            .OrderBy(p => p.Value.LastUsed)
            .Take(_data.Prefixes.Count - MaxPrefixes)
            .Select(p => p.Key)
            .ToList();
        foreach (var v in victims) _data.Prefixes.Remove(v);
    }
}
