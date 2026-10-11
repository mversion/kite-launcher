using Kite.Core.Settings;

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
