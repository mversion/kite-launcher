using System.Text;
using Kite.Plugins;
using Kite.Plugins.Builtin.Everything;

namespace Kite.Tests;

public class EverythingIpcTests
{
    [Fact]
    public void Builds_query2_header_and_text()
    {
        var bytes = EverythingClient.BuildQuery2(0x1234, 77, "invoice", 25, EverythingSort.DateModifiedDescending, matchPath: true);
        uint U(int i) => BitConverter.ToUInt32(bytes, i * 4);
        Assert.Equal(0x1234u, U(0));
        Assert.Equal(77u, U(1));
        Assert.Equal(4u, U(2));   // match path
        Assert.Equal(0u, U(3));   // offset
        Assert.Equal(25u, U(4));  // max results
        Assert.Equal(0x53u, U(5)); // name | path | size | date modified
        Assert.Equal(14u, U(6));  // date modified descending
        Assert.Equal("invoice\0", Encoding.Unicode.GetString(bytes, 28, bytes.Length - 28));
    }

    [Fact]
    public void Parses_list2_reply()
    {
        // Two items: a file and a folder, each with name and path strings.
        var data = new List<byte>();
        void U32(uint v) => data.AddRange(BitConverter.GetBytes(v));
        void Str(string s) { U32((uint)s.Length); data.AddRange(Encoding.Unicode.GetBytes(s + "\0")); }

        U32(2); U32(2); U32(0); U32(3); U32(1);   // totitems, numitems, offset, request_flags, sort
        var itemTable = data.Count;
        U32(0); U32(0);                            // item 0 placeholder
        U32(1); U32(0);                            // item 1: folder flag
        var item0 = data.Count; Str("invoice.pdf"); Str(@"C:\Docs");
        var item1 = data.Count; Str("Invoices"); Str(@"D:\Archive");
        var bytes = data.ToArray();
        BitConverter.GetBytes((uint)item0).CopyTo(bytes, itemTable + 4);
        BitConverter.GetBytes((uint)item1).CopyTo(bytes, itemTable + 12);

        var results = EverythingClient.ParseList2(bytes);
        Assert.Equal(2, results.Count);
        Assert.Equal(@"C:\Docs\invoice.pdf", results[0].FullPath);
        Assert.False(results[0].IsFolder);
        Assert.Equal(@"D:\Archive\Invoices", results[1].FullPath);
        Assert.True(results[1].IsFolder);
    }

    [Fact]
    public void Parses_size_and_date_modified()
    {
        var modified = new DateTime(2026, 10, 3, 14, 32, 0, DateTimeKind.Utc);
        var data = new List<byte>();
        void U32(uint v) => data.AddRange(BitConverter.GetBytes(v));
        void I64(long v) => data.AddRange(BitConverter.GetBytes(v));
        void Str(string s) { U32((uint)s.Length); data.AddRange(Encoding.Unicode.GetBytes(s + "\0")); }

        U32(1); U32(1); U32(0); U32(0x53); U32(14);    // name | path | size | date modified
        U32(0); U32(28);                                // item 0 data starts right after the table
        Str("report.docx"); Str(@"C:\Work"); I64(123_456); I64(modified.ToFileTimeUtc());

        var r = Assert.Single(EverythingClient.ParseList2(data.ToArray()));
        Assert.Equal(@"C:\Work\report.docx", r.FullPath);
        Assert.Equal(123_456, r.Size);
        Assert.Equal(modified.ToLocalTime(), r.Modified);
    }

    [Fact]
    public void Formats_timestamps_compactly()
    {
        var now = new DateTime(2026, 10, 11, 18, 0, 0);
        string? D(DateTime? m, long? size = null) => EverythingPlugin.FormatDetail(m, size, now);
        var t = new DateTime(2026, 10, 11, 14, 32, 0).ToString("t");
        Assert.Equal($"Today {t}", D(new DateTime(2026, 10, 11, 14, 32, 0)));
        Assert.StartsWith("Yesterday ", D(new DateTime(2026, 10, 10, 9, 10, 0)));
        Assert.Contains("2024", D(new DateTime(2024, 10, 3, 9, 0, 0)));
        Assert.Equal($"Today {t}  ·  1.2 MB", D(new DateTime(2026, 10, 11, 14, 32, 0), 1_258_291));
        Assert.Equal("512 B", D(null, 512));
        Assert.Null(D(null));
    }

    [Fact]
    public void Truncated_reply_does_not_throw() =>
        Assert.Empty(EverythingClient.ParseList2(new byte[10]));

    [Fact]
    public async Task Live_query_against_running_everything()
    {
        using var client = new EverythingClient();
        if (!client.IsAvailable) return; // only meaningful where Everything is running
        var results = await client.SearchAsync("everything.exe", 10, EverythingSort.NameAscending, matchPath: false, CancellationToken.None);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Name.Equals("Everything.exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(r.FullPath));
    }
}

public class EverythingPluginTests
{
    private sealed class FakeClient(bool available, params EverythingResult[] results) : IEverythingClient
    {
        public string? LastSearch { get; private set; }
        public bool IsAvailable => available;
        public Task<IReadOnlyList<EverythingResult>> SearchAsync(string search, int maxResults, EverythingSort sort, bool matchPath, CancellationToken ct)
        {
            LastSearch = search;
            return Task.FromResult<IReadOnlyList<EverythingResult>>(results.Take(maxResults).ToList());
        }
    }

    private static readonly CatItem EverythingShortcut = new()
    {
        Name = "Everything",
        Target = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Everything.lnk",
    };

    private static EverythingPlugin Plugin(FakeClient client, EverythingOptions? options = null)
    {
        var p = new EverythingPlugin(() => client);
        p.Init(new FakeContext(options is null ? new() : EverythingPlugin.CreateSettings(options)));
        return p;
    }

    [Fact]
    public void Takes_over_the_users_everything_shortcut()
    {
        var p = Plugin(new FakeClient(true));
        Assert.True(p.HandlesOwner(EverythingShortcut));
        Assert.True(p.HandlesOwner(new CatItem { Name = "x", Target = @"P:\Tools\Everything.exe" }));
        Assert.False(p.HandlesOwner(new CatItem { Name = "Notepad", Target = @"C:\Windows\notepad.exe" }));
    }

    [Fact]
    public async Task Owner_chip_returns_file_results_above_the_owner_row()
    {
        var client = new FakeClient(true, new EverythingResult("invoice.pdf", @"C:\Docs", false), new EverythingResult("invoice-2.pdf", @"C:\Docs", false));
        var results = await Plugin(client).GetResults(new Query("inv", EverythingShortcut, ["inv"]), CancellationToken.None);
        Assert.Equal("inv", client.LastSearch);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Score > 0));
        Assert.Equal(@"C:\Docs\invoice.pdf", results[0].Target);
    }

    [Fact]
    public async Task Enter_opens_everything_option_puts_files_below()
    {
        var client = new FakeClient(true, new EverythingResult("a.txt", @"C:\", false));
        var results = await Plugin(client, new EverythingOptions { EnterOpensEverything = true })
            .GetResults(new Query("a", EverythingShortcut, ["a"]), CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Score <= 0));
    }

    [Fact]
    public async Task Prefix_searches_without_tab()
    {
        var client = new FakeClient(true, new EverythingResult("report.docx", @"C:\Work", false));
        var results = await Plugin(client).GetResults(new Query("?report"), CancellationToken.None);
        Assert.Equal("report", client.LastSearch);
        Assert.Contains(results, r => r.Name == "report.docx");
        Assert.Contains(results, r => r.Name.StartsWith("Search Everything for"));
        Assert.Empty(await Plugin(client).GetResults(new Query("report"), CancellationToken.None)); // no prefix, no search
    }

    [Fact]
    public async Task Offers_to_start_everything_when_not_running()
    {
        var results = await Plugin(new FakeClient(false)).GetResults(new Query("x", EverythingShortcut, ["x"]), CancellationToken.None);
        Assert.Single(results);
        Assert.Contains("isn't running", results[0].Name);
    }

    [Fact]
    public async Task Runner_command_for_everything_gets_inline_results()
    {
        var runnerE = new CatItem
        {
            Name = "e",
            Target = "e",
            PluginId = "runner",
            Data = @"C:\Program Files\Everything\Everything.exe",
            IconPath = @"C:\Program Files\Everything\Everything.exe",
        };
        var client = new FakeClient(true, new EverythingResult("kite.exe", @"P:\Kite", false));
        var plugin = Plugin(client);
        Assert.True(plugin.HandlesOwner(runnerE));
        var results = await plugin.GetResults(new Query("kite", runnerE, ["kite"]), CancellationToken.None);
        Assert.Contains(results, r => r.Target == @"P:\Kite\kite.exe");
        Assert.False(plugin.HandlesOwner(runnerE with { Data = @"C:\Windows\System32\cmd.exe", IconPath = @"C:\Windows\System32\cmd.exe" }));
    }

    [Fact]
    public async Task Ignores_unrelated_owners()
    {
        var notepad = new CatItem { Name = "Notepad", Target = @"C:\Windows\notepad.exe" };
        Assert.Empty(await Plugin(new FakeClient(true)).GetResults(new Query("x", notepad, ["x"]), CancellationToken.None));
    }

    private sealed class FakeContext(System.Text.Json.Nodes.JsonObject settings) : IPluginContext
    {
        public System.Text.Json.Nodes.JsonObject Settings { get; } = settings;
        public void SaveSettings() { }
        public string DataDirectory => Path.GetTempPath();
        public string AppDirectory => AppContext.BaseDirectory;
        public void RunProgram(string file, string? arguments = null, bool runAsAdmin = false) { }
        public void OpenUri(string uri) { }
        public void CopyToClipboard(string text) { }
        public void Log(string message) { }
        public void RequestCatalogRebuild() { }
    }
}
