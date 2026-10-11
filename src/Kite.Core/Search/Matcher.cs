using Kite.Core.Catalog;

namespace Kite.Core.Search;

/// <summary>
/// Pure text-match scoring. Higher is better, 0 means no match. Tiers:
/// exact (1000) &gt; prefix (~900) &gt; word-start (~800) &gt; acronym (~750) &gt;
/// substring (~600) &gt; subsequence (~100-400).
/// </summary>
public static class Matcher
{
    public const int Exact = 1000;

    /// <param name="query">Lower-cased, trimmed query text.</param>
    public static int Score(CatalogEntry entry, string query)
    {
        if (query.Length == 0) return 0;
        if (query.Contains(' ')) return ScoreTokens(entry, query);
        return ScoreSingle(entry, query);
    }

    private static int ScoreSingle(CatalogEntry entry, string q)
    {
        var name = entry.NameLower;
        if (name.Length < q.Length) return entry.Acronym.StartsWith(q, StringComparison.Ordinal) ? 750 : 0;

        if (name.Length == q.Length && name == q) return Exact;
        if (name.StartsWith(q, StringComparison.Ordinal))
            return 900 - Math.Min(name.Length - q.Length, 50);

        foreach (var ws in entry.WordStarts)
        {
            if (ws == 0) continue;
            if (ws + q.Length <= name.Length && string.CompareOrdinal(name, ws, q, 0, q.Length) == 0)
                return 800 - Math.Min(ws, 40);
        }

        if (q.Length >= 2 && entry.Acronym.StartsWith(q, StringComparison.Ordinal))
            return 750 - Math.Min(entry.Acronym.Length - q.Length, 20);

        var idx = name.IndexOf(q, StringComparison.Ordinal);
        if (idx > 0) return 600 - Math.Min(idx, 100);

        return Subsequence(entry, q);
    }

    /// <summary>"vis code" - every token must match somewhere; tokens hitting word starts score higher.</summary>
    private static int ScoreTokens(CatalogEntry entry, string q)
    {
        var total = 0;
        var count = 0;
        foreach (var token in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = ScoreSingle(entry, token);
            if (s == 0) return 0;
            total += Math.Min(s, 900);
            count++;
        }
        return count == 0 ? 0 : 550 + total / count / 10;
    }

    /// <summary>
    /// All query chars appear in order, starting at a word start, with few gaps ("phsp" → Photoshop).
    /// Loose scatter matches ("paint" in "Microsoft Support and Recovery Assistant") are rejected.
    /// </summary>
    private static int Subsequence(CatalogEntry entry, string q)
    {
        var name = entry.NameLower;
        var starts = entry.WordStarts;
        var maxGaps = Math.Max(2, q.Length / 2);
        foreach (var start in starts)
        {
            if (name[start] != q[0]) continue;
            int qi = 1, score = 35, run = 1, last = start, gaps = 0;
            for (var i = start + 1; i < name.Length && qi < q.Length; i++)
            {
                if (name[i] != q[qi]) continue;
                var atWordStart = Array.IndexOf(starts, i) >= 0;
                if (i == last + 1) run++;
                else { run = 1; gaps++; }
                score += 10 + run * 5 + (atWordStart ? 25 : 0);
                last = i;
                qi++;
            }
            if (qi == q.Length && gaps <= maxGaps)
                return Math.Clamp(100 + score - gaps * 15 - start, 100, 450);
        }
        return 0;
    }
}
