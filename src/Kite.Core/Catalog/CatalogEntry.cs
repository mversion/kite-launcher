using Kite.Plugins;

namespace Kite.Core.Catalog;

/// <summary>A catalog item with precomputed search keys so matching is allocation-free.</summary>
public sealed class CatalogEntry
{
    public CatalogEntry(CatItem item)
    {
        Item = item;
        NameLower = item.Name.ToLowerInvariant();
        WordStarts = ComputeWordStarts(item.Name);
        Acronym = string.Concat(WordStarts.Select(i => NameLower[i]));
    }

    public CatItem Item { get; }
    public string NameLower { get; }
    /// <summary>Indexes where a word begins: after a separator or at a lower→Upper transition ("VisualStudio").</summary>
    public int[] WordStarts { get; }
    /// <summary>First letter of each word: "Visual Studio Code" → "vsc".</summary>
    public string Acronym { get; }

    internal static int[] ComputeWordStarts(string name)
    {
        var starts = new List<int>(4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c)) continue;
            if (i == 0) { starts.Add(i); continue; }
            var prev = name[i - 1];
            var acronymEnd = char.IsUpper(c) && char.IsUpper(prev) && i + 1 < name.Length && char.IsLower(name[i + 1]); // "SCPPortable"
            if (!char.IsLetterOrDigit(prev) || (char.IsUpper(c) && char.IsLower(prev)) || acronymEnd || (char.IsDigit(c) && !char.IsDigit(prev)))
                starts.Add(i);
        }
        return starts.ToArray();
    }
}

/// <summary>Immutable snapshot of the catalog; swapped atomically after a rescan.</summary>
public sealed class CatalogSnapshot
{
    public static readonly CatalogSnapshot Empty = new([]);

    public CatalogSnapshot(IEnumerable<CatItem> items)
    {
        var byKey = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            byKey.TryAdd(item.Key, new CatalogEntry(item));
        ByKey = byKey;
        Entries = byKey.Values.ToArray();
    }

    public CatalogEntry[] Entries { get; }
    public IReadOnlyDictionary<string, CatalogEntry> ByKey { get; }
    public int Count => Entries.Length;
}
