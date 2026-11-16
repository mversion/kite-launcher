using System.Text;
using Kite.Plugins;
using Kite.Plugins.Builtin.EnteAuth;

namespace Kite.Tests;

public class EnteAuthCryptoTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));
    private static readonly byte[] Key0To31 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void ChaCha20_block_matches_RFC_8439() // section 2.3.2
    {
        var block = SecretStream.ChaCha20(Key0To31, Hex("000000090000004a00000000"), 1, new byte[64]);
        Assert.Equal(Hex("10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4e"), block[..32]);
    }

    [Fact]
    public void Poly1305_matches_RFC_8439() // section 2.5.2
    {
        var tag = SecretStream.Poly1305(Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b"),
            Encoding.ASCII.GetBytes("Cryptographic Forum Research Group"));
        Assert.Equal(Hex("a8061dc1305136c6c22b8baf0c0127a9"), tag);
    }

    [Fact]
    public void HChaCha20_matches_the_XChaCha_draft() // draft-irtf-cfrg-xchacha, 2.2.1
    {
        var sub = SecretStream.HChaCha20(Key0To31, Hex("000000090000004a0000000031415927"));
        Assert.Equal(Hex("82413b4227b27bfed30e42508a877d73a0f9e4d58a74a853c12ec41326d3ecdc"), sub);
    }

    [Fact]
    public void Secretstream_round_trips_and_rejects_tampering()
    {
        var message = Encoding.UTF8.GetBytes("\"otpauth://totp/Test:me?secret=JBSWY3DPEHPK3PXP\"");
        var (header, cipher) = SecretStream.Push(Key0To31, message);
        Assert.Equal(message, SecretStream.Pull(Key0To31, header, cipher));

        cipher[5] ^= 1;
        Assert.Null(SecretStream.Pull(Key0To31, header, cipher));
        cipher[5] ^= 1;
        var wrongKey = (byte[])Key0To31.Clone(); wrongKey[0] ^= 1;
        Assert.Null(SecretStream.Pull(wrongKey, header, cipher));
    }
}

public class OtpTests
{
    private static OtpAccount Rfc(string algorithm, string ascii, int digits = 8) =>
        new("t", "RFC", "test", OtpType.Totp, Encoding.ASCII.GetBytes(ascii), algorithm, digits, 30, false);

    [Theory] // RFC 6238 appendix B
    [InlineData("SHA1", "12345678901234567890", 59L, "94287082")]
    [InlineData("SHA1", "12345678901234567890", 1111111109L, "07081804")]
    [InlineData("SHA1", "12345678901234567890", 20000000000L, "65353130")]
    [InlineData("SHA256", "12345678901234567890123456789012", 59L, "46119246")]
    [InlineData("SHA512", "1234567890123456789012345678901234567890123456789012345678901234", 59L, "90693936")]
    public void Totp_matches_RFC_6238(string algorithm, string secret, long unix, string expected) =>
        Assert.Equal(expected, Rfc(algorithm, secret).CodeAt(DateTimeOffset.FromUnixTimeSeconds(unix)).Code);

    [Fact]
    public void Parses_otpauth_uris_as_Ente_stores_them()
    {
        var a = OtpAccount.Parse("id1", "otpauth://totp/Microsoft:bilgin%2Bwork@example.com?secret=JBSWY3DPEHPK3PXP&issuer=Microsoft&algorithm=SHA1&digits=6&period=30&codeDisplay=%7B%22pinned%22%3Atrue%2C%22trashed%22%3Afalse%7D")!;
        Assert.Equal("Microsoft", a.Issuer);
        Assert.Equal("bilgin+work@example.com", a.Account);
        Assert.Equal(Encoding.ASCII.GetBytes("Hello!").Concat(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }), a.Secret);
        Assert.False(a.Trashed);

        Assert.True(OtpAccount.Parse("id2", "otpauth://totp/Old?secret=JBSWY3DPEHPK3PXP&codeDisplay=%7B%22trashed%22%3Atrue%7D")!.Trashed);
        Assert.Equal("Label", OtpAccount.Parse("id3", "otpauth://totp/Label:me?secret=JBSWY3DPEHPK3PXP")!.Issuer);
        Assert.Null(OtpAccount.Parse("id4", "otpauth://totp/NoSecret:me?issuer=x"));
        Assert.Null(OtpAccount.Parse("id5", "https://example.com"));
        Assert.Equal(5, OtpAccount.Parse("id6", "otpauth://steam/Steam:me?secret=JBSWY3DPEHPK3PXP")!.Generate(1).Length);
    }

    [Fact]
    public void Decrypts_an_entry_like_Ente_writes_it()
    {
        var key = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
        var json = Encoding.UTF8.GetBytes("\"otpauth://totp/GitHub:me?secret=JBSWY3DPEHPK3PXP&issuer=GitHub\"");
        var (header, cipher) = SecretStream.Push(key, json);
        var account = EnteAuthStore.Decrypt("e1", key, Convert.ToBase64String(header), Convert.ToBase64String(cipher));
        Assert.Equal("GitHub", account?.Issuer);
        Assert.Null(EnteAuthStore.Decrypt("e1", new byte[32], Convert.ToBase64String(header), Convert.ToBase64String(cipher)));
    }

    [Fact]
    public void Matches_by_service_then_account()
    {
        var ms = new OtpAccount("1", "Microsoft", "me@work.com", OtpType.Totp, [1], "SHA1", 6, 30, false);
        Assert.Equal(300, EnteAuthPlugin.Match(ms, "microsoft"));
        Assert.Equal(200, EnteAuthPlugin.Match(ms, "micro"));
        Assert.Equal(100, EnteAuthPlugin.Match(ms, "work"));
        Assert.Equal(0, EnteAuthPlugin.Match(ms, "paint"));
    }

    [Fact]
    public void Plugin_is_off_until_switched_on()
    {
        using var h = new Harness([]);
        h.Plugins.Register(new EnteAuthPlugin());
        h.Plugins.Register(new Kite.Plugins.Builtin.Calcy.CalcyPlugin());
        h.Plugins.InitAll();
        Assert.Contains(EnteAuthPlugin.PluginId, h.Settings.Current.DisabledPlugins);
        Assert.DoesNotContain("calcy", h.Settings.Current.DisabledPlugins);

        // Once switched on, a later start doesn't turn it off again.
        h.Settings.Current.DisabledPlugins.Remove(EnteAuthPlugin.PluginId);
        h.Plugins.InitAll();
        Assert.DoesNotContain(EnteAuthPlugin.PluginId, h.Settings.Current.DisabledPlugins);
    }
}

public class EnteAuthHelloTests
{
    private sealed class Context(System.Text.Json.Nodes.JsonObject settings, string? helloAnswer) : IPluginContext
    {
        public System.Text.Json.Nodes.JsonObject Settings { get; } = settings;
        public List<string> Copied { get; } = [];
        public int HelloPrompts { get; private set; }
        public void SaveSettings() { }
        public string DataDirectory => Path.GetTempPath();
        public string AppDirectory => AppContext.BaseDirectory;
        public void RunProgram(string file, string? arguments = null, bool runAsAdmin = false) { }
        public void OpenUri(string uri) { }
        public void CopyToClipboard(string text) => Copied.Add(text);
        public void CopySecretToClipboard(string text, TimeSpan clearAfter) => Copied.Add(text);
        public Task<string?> VerifyUserAsync(string reason) { HelloPrompts++; return Task.FromResult(helloAnswer); }
        public void Log(string message) { }
        public void RequestCatalogRebuild() { }
    }

    private static readonly OtpAccount Microsoft = new("ms", "Microsoft", "me@work.com", OtpType.Totp,
        Encoding.ASCII.GetBytes("12345678901234567890"), "SHA1", 6, 30, false);

    private static (EnteAuthPlugin Plugin, Context Ctx) Setup(bool requireHello, string? helloAnswer)
    {
        var plugin = new EnteAuthPlugin([Microsoft]) { Clock = () => DateTimeOffset.FromUnixTimeSeconds(59) };
        var ctx = new Context(new() { ["requireHello"] = requireHello, ["unlockMinutes"] = 5 }, helloAnswer);
        plugin.Init(ctx);
        return (plugin, ctx);
    }

    private static async Task<CatItem> Row(EnteAuthPlugin p) =>
        Assert.Single(await p.GetResults(new Query("microsoft"), CancellationToken.None));

    [Fact]
    public async Task Without_the_check_codes_show_and_copy()
    {
        var (plugin, ctx) = Setup(requireHello: false, helloAnswer: null);
        var row = await Row(plugin);
        Assert.StartsWith("287 082", row.Detail);                           // RFC 6238 SHA1 at T=59, 6 digits
        plugin.Launch(row, new Query("microsoft"), LaunchMode.Default);
        Assert.Equal(["287082"], ctx.Copied);
        Assert.Equal(0, ctx.HelloPrompts);
    }

    [Fact]
    public async Task With_the_check_codes_hide_until_Hello_confirms()
    {
        var (plugin, ctx) = Setup(requireHello: true, helloAnswer: null);
        var row = await Row(plugin);
        Assert.DoesNotContain("287", row.Detail);
        Assert.Contains("Windows Hello", row.Description);

        plugin.Launch(row, new Query("microsoft"), LaunchMode.Default);
        await Task.Delay(50);
        Assert.Equal(1, ctx.HelloPrompts);
        Assert.Equal(["287082"], ctx.Copied);
        Assert.False(plugin.IsLocked);
        Assert.StartsWith("287 082", (await Row(plugin)).Detail);           // unlocked for the next few minutes
    }

    [Theory]
    [InlineData("")]                                                         // cancelled
    [InlineData("Windows Hello isn't set up on this PC.")]                   // not available
    public async Task A_failed_or_cancelled_check_copies_nothing(string answer)
    {
        var (plugin, ctx) = Setup(requireHello: true, helloAnswer: answer);
        plugin.Launch(await Row(plugin), new Query("microsoft"), LaunchMode.Default);
        await Task.Delay(50);
        Assert.Empty(ctx.Copied);
        Assert.True(plugin.IsLocked);
        if (answer.Length > 0) Assert.Contains("isn't set up", (await Row(plugin)).Description);
    }
}
