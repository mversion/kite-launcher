using Kite.Plugins;

namespace Kite.Plugins.Builtin.EnteAuth;

/// <summary>
/// 2FA codes from the installed Ente Auth app. Type an issuer ("microsoft") or Tab on Ente Auth
/// ("ente ⇥ micro") and the current code shows on the row; Enter copies it (kept out of clipboard
/// history, cleared after 30 s). Off until switched on in Settings › Plugins: anyone at the unlocked
/// PC could otherwise read codes, since Ente's own app lock only guards its window.
/// </summary>
public sealed class EnteAuthPlugin : IKitePlugin
{
    public const string PluginId = "enteauth";
    private static readonly TimeSpan ClipboardLifetime = TimeSpan.FromSeconds(30);

    private readonly Func<EnteAuthStore> _storeFactory;
    private IPluginContext _ctx = null!;
    private readonly object _gate = new();
    private IReadOnlyList<OtpAccount> _accounts = [];
    private DateTime _loadedStamp;
    private string? _error;
    private bool _requireHello;
    private TimeSpan _unlockFor = TimeSpan.FromMinutes(5);
    private DateTimeOffset _unlockedUntil = DateTimeOffset.MinValue;
    private string? _helloProblem;
    private int _verifying;

    public EnteAuthPlugin() : this(() => new EnteAuthStore()) { }
    public EnteAuthPlugin(Func<EnteAuthStore> storeFactory) => _storeFactory = storeFactory;

    /// <summary>For tests: fixed accounts instead of reading Ente Auth.</summary>
    public EnteAuthPlugin(IReadOnlyList<OtpAccount> accounts) : this(() => new EnteAuthStore()) => _fixedAccounts = accounts;
    private readonly IReadOnlyList<OtpAccount>? _fixedAccounts;

    public string Id => PluginId;
    public string Name => "Ente Auth";
    public string Description => "2FA codes from Ente Auth: type the service, Enter copies the code";
    public bool EnabledByDefault => false;

    /// <summary>For tests: the clock codes are generated for.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public void Init(IPluginContext context)
    {
        _ctx = context;
        var wasRequired = _requireHello;
        _requireHello = context.Settings["requireHello"]?.GetValue<bool>() ?? false;
        _unlockFor = TimeSpan.FromMinutes(Math.Clamp(context.Settings["unlockMinutes"]?.GetValue<int>() ?? 5, 1, 240));
        if (_requireHello && !wasRequired) _unlockedUntil = DateTimeOffset.MinValue; // switching the check on locks at once
    }

    /// <summary>Codes are hidden until Windows Hello confirms it's you (only when that check is on).</summary>
    public bool IsLocked => _requireHello && Clock() >= _unlockedUntil;

    /// <summary>"Ente Auth" from the Start menu works as a Tab owner: "ente ⇥ micro".</summary>
    public bool HandlesOwner(CatItem owner) =>
        owner.PluginId is null && (owner.Name.Equals("Ente Auth", StringComparison.OrdinalIgnoreCase) ||
                                   Path.GetFileName(owner.Target).Equals("Ente Auth.lnk", StringComparison.OrdinalIgnoreCase));

    public Task<IReadOnlyList<CatItem>> GetResults(Query query, CancellationToken ct)
    {
        string text;
        var ownerMode = false;
        if (query.Owner is { } owner)
        {
            if (!HandlesOwner(owner)) return Empty;
            text = query.ArgumentText.Trim();
            ownerMode = true;
        }
        else
        {
            text = query.Text.Trim();
            if (text.Length < 2) return Empty;
        }

        var accounts = Accounts();
        if (accounts.Count == 0)
            return ownerMode && _error is not null
                ? Task.FromResult<IReadOnlyList<CatItem>>([new CatItem { Name = "Can't read Ente Auth", Target = "", PluginId = PluginId, Description = _error, Score = 1 }])
                : Empty;

        var now = Clock();
        var locked = IsLocked;
        var results = new List<CatItem>();
        foreach (var a in accounts)
        {
            if (a.Type == OtpType.Hotp) continue; // counter-based codes need Ente Auth to advance the counter
            var score = ownerMode ? (text.Length == 0 ? 1 : Match(a, text) is var m && m > 0 ? m : 0) : Match(a, text);
            if (score <= 0) continue;
            string detail, hint;
            if (locked)
            {
                detail = "••• •••";
                hint = _helloProblem ?? "Enter unlocks with Windows Hello";
            }
            else
            {
                var (code, left) = a.CodeAt(now);
                detail = $"{OtpAccount.Pretty(code)}  ·  {left}s";
                hint = "Enter copies the code";
            }
            results.Add(new CatItem
            {
                Name = a.Title,
                Target = a.Id,
                PluginId = PluginId,
                Description = a.Issuer.Length > 0 && a.Account.Length > 0 ? $"{a.Account}  ·  {hint}" : hint,
                Detail = detail,
                IconPath = EnteShortcut,
                // Plain searches: an exact service name goes first; other matches list below apps and files.
                Score = ownerMode ? 1000 + score : score >= 300 ? 1500 : -50 + score / 10,
            });
        }
        return Task.FromResult<IReadOnlyList<CatItem>>(results.OrderByDescending(r => r.Score).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(ownerMode ? 12 : 6).ToList());
    }

    /// <summary>300 exact issuer, 200 issuer prefix, 150 word start, 100 account match, 50 substring, 0 none.</summary>
    public static int Match(OtpAccount a, string text)
    {
        var q = text.Trim();
        if (q.Length == 0) return 0;
        bool Has(string s, Func<string, bool> f) => s.Length > 0 && f(s);
        if (Has(a.Issuer, s => s.Equals(q, StringComparison.OrdinalIgnoreCase))) return 300;
        if (Has(a.Title, s => s.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 200;
        if (Has(a.Title, s => s.Split(' ', '.', '-', '_').Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase)))) return 150;
        if (q.Length >= 3 && Has(a.Account, s => s.Contains(q, StringComparison.OrdinalIgnoreCase))) return 100;
        if (q.Length >= 3 && Has(a.Title, s => s.Contains(q, StringComparison.OrdinalIgnoreCase))) return 50;
        return 0;
    }

    public bool Launch(CatItem item, Query query, LaunchMode mode)
    {
        if (mode == LaunchMode.Alternate && EnteShortcut is { } lnk)
        {
            _ctx.RunProgram(lnk); // Shift+Enter: open Ente Auth itself
            return true;
        }
        var account = Accounts().FirstOrDefault(a => a.Id == item.Target);
        if (account is null) return item.Target.Length == 0; // the error row: nothing to do
        if (IsLocked)
        {
            _ = UnlockThenCopyAsync(account);
            return true;
        }
        var (code, _) = account.CodeAt(Clock());
        _ctx.CopySecretToClipboard(code, ClipboardLifetime);
        return true;
    }

    /// <summary>Windows Hello first; the code is copied only once it confirms it's you.</summary>
    private async Task UnlockThenCopyAsync(OtpAccount account)
    {
        if (Interlocked.Exchange(ref _verifying, 1) == 1) return; // a prompt is already showing
        try
        {
            var problem = await _ctx.VerifyUserAsync($"Show 2FA codes from Ente Auth ({account.Title})");
            if (problem is not null)
            {
                _helloProblem = problem.Length > 0 ? problem : null; // "" = cancelled: nothing to explain
                return;
            }
            _helloProblem = null;
            _unlockedUntil = Clock() + _unlockFor;
            var (code, _) = account.CodeAt(Clock());
            _ctx.CopySecretToClipboard(code, ClipboardLifetime);
        }
        catch (Exception ex)
        {
            _ctx.Log($"Ente Auth: Windows Hello failed: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _verifying, 0); }
    }

    /// <summary>The accounts, re-read whenever Ente Auth's database changes.</summary>
    private IReadOnlyList<OtpAccount> Accounts()
    {
        if (_fixedAccounts is not null) return _fixedAccounts;
        var store = _storeFactory();
        var stamp = File.Exists(store.DatabasePath) ? File.GetLastWriteTimeUtc(store.DatabasePath) : DateTime.MinValue;
        lock (_gate)
        {
            if (stamp == _loadedStamp && (_accounts.Count > 0 || _error is not null)) return _accounts;
            var result = store.Load();
            _accounts = result.Accounts;
            _error = result.Error;
            _loadedStamp = stamp;
            if (result.Error is not null) _ctx.Log($"Ente Auth: {result.Error}");
            else if (result.Skipped > 0) _ctx.Log($"Ente Auth: {result.Skipped} entries couldn't be read");
            return _accounts;
        }
    }

    private static string? EnteShortcut
    {
        get
        {
            foreach (var root in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            {
                var lnk = Path.Combine(Environment.GetFolderPath(root), "Ente Auth.lnk");
                if (File.Exists(lnk)) return lnk;
            }
            return null;
        }
    }

    private static Task<IReadOnlyList<CatItem>> Empty => Task.FromResult<IReadOnlyList<CatItem>>([]);
}
