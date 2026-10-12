using System.Diagnostics;
using Kite.Core.Catalog;
using Kite.Core.Search;
using Kite.Plugins;

namespace Kite.Tests;

public class MatcherTests
{
    private static int Score(string name, string q) => Matcher.Score(new CatalogEntry(Items.App(name)), q);

    [Fact]
    public void Tiers_are_ordered()
    {
        var exact = Score("paint", "paint");
        var prefix = Score("paint 3d", "paint");
        var wordStart = Score("ms paint", "paint");
        var acronym = Score("visual studio code", "vsc");
        var substring = Score("mspaint", "paint");
        var subsequence = Score("photoshop", "phsp");

        Assert.True(exact > prefix);
        Assert.True(prefix > wordStart);
        Assert.True(wordStart > acronym);
        Assert.True(acronym > substring);
        Assert.True(substring > subsequence);
        Assert.True(subsequence > 0);
    }

    [Fact]
    public void Non_matches_score_zero()
    {
        Assert.Equal(0, Score("notepad", "xyz"));
        Assert.Equal(0, Score("notepad", "dapeton"));
    }

    [Fact]
    public void Scattered_subsequences_are_rejected()
    {
        Assert.Equal(0, Score("Microsoft Support and Recovery Assistant", "paint"));
        Assert.Equal(0, Score("update_installer", "paint"));
        Assert.True(Score("Adobe Photoshop 2026", "phsp") > 0);
    }

    [Fact]
    public void CamelCase_counts_as_word_start()
    {
        Assert.Equal("wsp", new CatalogEntry(Items.App("WinSCPPortable")).Acronym[..3]);
        Assert.True(Score("WinSCPPortable", "portable") >= 700);
    }

    [Fact]
    public void Multi_token_queries_match_each_token()
    {
        Assert.True(Score("PO - Chrome", "po chr") > 0);
        Assert.True(Score("Visual Studio Code", "vis code") > 0);
        Assert.Equal(0, Score("Visual Studio Code", "vis xyz"));
    }
}

public class SearchEngineTests
{
    [Fact]
    public void Learned_prefix_wins_even_over_better_text_match()
    {
        using var h = new Harness(Items.Sample());
        // Without learning, "e" prefers the shortest prefix match.
        Assert.Equal("Edge", h.Names("e")[0]);

        h.History.Record("e", @"C:\Apps\Everything.lnk", "Everything");
        Assert.Equal("Everything", h.Names("e")[0]);
    }

    [Fact]
    public void Longer_learned_prefixes_boost_while_typing()
    {
        using var h = new Harness(Items.Sample());
        h.History.Record("notep", @"C:\Apps\Notepad++.lnk", "Notepad++");
        Assert.Equal("Notepad++", h.Names("no")[0]);
    }

    [Fact]
    public void Acronym_finds_visual_studio_code()
    {
        using var h = new Harness(Items.Sample());
        Assert.Equal("Visual Studio Code", h.Names("vsc")[0]);
    }

    [Fact]
    public void Store_apps_are_searchable()
    {
        using var h = new Harness(Items.Sample());
        var top = h.Engine.SearchCatalog("paint", 5)[0];
        Assert.Equal("Paint", top.Name);
        Assert.StartsWith(AppsFolderSource.Prefix, top.Target);
    }

    [Fact]
    public void Usage_breaks_ties()
    {
        using var h = new Harness(Items.Sample());
        h.History.Record("", @"C:\Apps\Notepad++.lnk", "Notepad++"); // usage only, no prefix
        h.History.Record("", @"C:\Apps\Notepad++.lnk", "Notepad++");
        Assert.Equal("Notepad++", h.Names("notepad")[1]); // exact "Notepad" still first
        Assert.Equal("Notepad", h.Names("notepad")[0]);
    }

    [Fact]
    public void Pinned_alias_is_not_overwritten_by_learning()
    {
        var history = new History();
        history.SetPrefix("n", "A", "A", pinned: true);
        history.Record("n", "B", "B");
        Assert.Equal("A", history.Lookup("n")!.Key);
    }

    [Fact]
    public void Merge_places_plugin_scores_around_catalog()
    {
        var catalog = new List<CatItem> { Items.App("Notepad") };
        var plugin = new List<CatItem>
        {
            new() { Name = "fallback", Target = "f", PluginId = "weby", Score = -100 },
            new() { Name = "35", Target = "35", PluginId = "calcy", Score = 1000 },
        };
        var merged = SearchEngine.Merge(catalog, plugin, 10).Select(i => i.Name).ToList();
        Assert.Equal(["35", "Notepad", "fallback"], merged);
    }

    [Fact]
    public void Catalog_search_of_5000_items_is_under_5ms()
    {
        var rnd = new Random(42);
        var words = new[] { "adobe", "visual", "studio", "code", "chrome", "portable", "manager", "tool", "editor", "viewer", "setup", "helper", "remote", "desktop", "player" };
        var items = Enumerable.Range(0, 5000)
            .Select(i => Items.App($"{words[rnd.Next(words.Length)]} {words[rnd.Next(words.Length)]} {i}"))
            .ToList();
        using var h = new Harness(items);
        for (var i = 0; i < 20; i++) h.Engine.SearchCatalog("vis", 10); // warm-up / JIT

        var queries = new[] { "v", "vi", "vis", "visu", "vsc", "port", "ed", "chr pl" };
        var sw = Stopwatch.StartNew();
        const int rounds = 25;
        for (var r = 0; r < rounds; r++)
            foreach (var q in queries) h.Engine.SearchCatalog(q, 10);
        var perQuery = sw.Elapsed.TotalMilliseconds / (rounds * queries.Length);
        Assert.True(perQuery < 5, $"Average {perQuery:F2} ms per query");
    }
}

public class OwnerPickTests
{
    private static readonly CatItem Owner = new() { Name = "e", Target = "e", PluginId = "runner", Data = @"C:\Program Files\Everything\Everything.exe" };

    private static CatItem File(string path, int score) =>
        new() { Name = Path.GetFileName(path), Target = path, PluginId = "everything", Data = "result", Score = score };

    private static Query Search(string text) => new($"e\t{text}", Owner, [text]);

    [Fact]
    public void Picked_file_comes_first_for_the_same_search()
    {
        using var dir = new TempDir();
        var a = dir.File("a.pdf"); var b = dir.File("password reset.pdf");
        var history = new History();
        history.RecordPick(Owner.Key, "Password  Reset ", File(b, 0));

        var merged = SearchEngine.MergeOwner(Owner, Search("password reset"), [File(a, 1000), File(b, 999)], history);
        Assert.Equal(b, merged[0].Target);
    }

    [Fact]
    public void Picked_file_shows_before_results_arrive_and_partial_search_lifts_it()
    {
        using var dir = new TempDir();
        var a = dir.File("a.pdf"); var b = dir.File("b.pdf");
        var history = new History();
        history.RecordPick(Owner.Key, "password reset", File(b, 0));

        var early = SearchEngine.MergeOwner(Owner, Search("password reset"), [], history);
        Assert.Equal(b, early[0].Target);                                   // shown while Everything is still searching

        var partial = SearchEngine.MergeOwner(Owner, Search("pass"), [File(a, 1000), File(b, 999)], history);
        Assert.Equal(b, partial[0].Target);
        var other = SearchEngine.MergeOwner(Owner, Search("invoice"), [File(a, 1000), File(b, 999)], history);
        Assert.Equal(a, other[0].Target);                                    // unrelated searches are untouched
    }

    [Fact]
    public void Picks_survive_save_and_load()
    {
        using var dir = new TempDir();
        var b = dir.File("b.pdf");
        var history = new History();
        history.RecordPick(Owner.Key, "password reset", File(b, 0));
        var path = Path.Combine(dir.Path, "history.json");
        history.Save(path);

        var loaded = new History();
        loaded.Load(path);
        Assert.Equal(b, loaded.LookupPick(Owner.Key, "PASSWORD RESET")?.Target);
    }
}
