using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Kite.Plugins.Builtin.EnteAuth;

/// <summary>
/// Reads the 2FA accounts from the installed Ente Auth desktop app, as the same Windows user:
/// <list type="number">
/// <item>flutter_secure_storage.dat is a DPAPI (current user) blob holding a JSON map; it gives the
/// 32-byte data key ("auth_secret_key", or "offline_auth_secret_key" in offline mode).</item>
/// <item>.ente.authenticator.db (or .ente.offline_authenticator.db) is SQLite; table "entities"
/// has one secretstream-encrypted row per code (base64 header + encryptedData).</item>
/// <item>Each row decrypts to JSON: an otpauth:// URI string (or an object containing one).</item>
/// </list>
/// Read-only throughout: nothing is written back, and nothing decrypted is saved anywhere.
/// </summary>
public sealed class EnteAuthStore
{
    public string Folder { get; }

    public EnteAuthStore(string? folder = null) => Folder = folder ?? DefaultFolder;

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ente Technologies, Inc", "Ente Auth");

    public bool IsInstalled => File.Exists(Path.Combine(Folder, "flutter_secure_storage.dat"));

    /// <summary>The database Ente Auth is using now (offline mode keeps a separate one).</summary>
    public string DatabasePath => Path.Combine(Folder, IsOfflineMode() ? ".ente.offline_authenticator.db" : ".ente.authenticator.db");

    public sealed record LoadResult(IReadOnlyList<OtpAccount> Accounts, int Skipped, string? Error);

    public LoadResult Load()
    {
        try
        {
            if (!IsInstalled) return new([], 0, "Ente Auth isn't set up on this PC");
            var offline = IsOfflineMode();
            var key = ReadKey(offline ? "offline_auth_secret_key" : "auth_secret_key");
            if (key is null) return new([], 0, "Ente Auth is signed out or locked to another Windows user");
            var db = DatabasePath;
            if (!File.Exists(db)) return new([], 0, "Ente Auth has no codes yet");

            var accounts = new List<OtpAccount>();
            var skipped = 0;
            foreach (var (id, encryptedData, header) in ReadEntities(db))
            {
                var account = Decrypt(id, key, header, encryptedData);
                if (account is null) skipped++;
                else if (!account.Trashed) accounts.Add(account);
            }
            Array.Clear(key);
            return new(accounts, skipped, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new([], 0, ex.Message);
        }
    }

    /// <summary>One stored code, or null when it doesn't authenticate (wrong key, altered data) or isn't usable.</summary>
    public static OtpAccount? Decrypt(string id, byte[] key, string headerB64, string dataB64)
    {
        byte[] header, data;
        try { header = Convert.FromBase64String(headerB64); data = Convert.FromBase64String(dataB64); }
        catch (FormatException) { return null; }
        var plain = SecretStream.Pull(key, header, data);
        if (plain is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(plain);
            var uri = FindOtpAuthUri(doc.RootElement);
            return uri is null ? null : OtpAccount.Parse(id, uri);
        }
        catch (JsonException) { return null; }
        finally { Array.Clear(plain); }
    }

    /// <summary>The JSON is normally a string holding the URI; older entries are objects containing it.</summary>
    private static string? FindOtpAuthUri(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                var s = e.GetString();
                return s is not null && s.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase) ? s : null;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    if (FindOtpAuthUri(p.Value) is { } found) return found;
                return null;
            default:
                return null;
        }
    }

    private bool IsOfflineMode()
    {
        try
        {
            var prefs = Path.Combine(Folder, "shared_preferences.json");
            if (!File.Exists(prefs)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(prefs));
            return doc.RootElement.TryGetProperty("flutter.has_opted_for_offline_mode", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }

    /// <summary>One value from flutter_secure_storage.dat (a DPAPI-protected JSON map), base64-decoded.</summary>
    private byte[]? ReadKey(string name)
    {
        var blob = File.ReadAllBytes(Path.Combine(Folder, "flutter_secure_storage.dat"));
        var json = Dpapi.Unprotect(blob);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
            var key = Convert.FromBase64String(v.GetString()!);
            return key.Length == SecretStream.KeyBytes ? key : null;
        }
        finally { Array.Clear(json); }
    }

    // ------------------------------------------------------------------ SQLite via Windows' own winsqlite3.dll (no extra download)

    private static IEnumerable<(string Id, string Data, string Header)> ReadEntities(string dbPath)
    {
        var rows = new List<(string, string, string)>();
        // Read-only, and the file is opened through a URI so a running Ente Auth is never blocked.
        var uri = "file:" + new Uri(dbPath).AbsolutePath + "?mode=ro";
        if (Sqlite.sqlite3_open_v2(Utf8(uri), out var db, Sqlite.OpenReadOnly | Sqlite.OpenUri, IntPtr.Zero) != Sqlite.Ok)
        {
            Sqlite.sqlite3_close_v2(db);
            throw new IOException("Couldn't open the Ente Auth database");
        }
        try
        {
            Sqlite.sqlite3_busy_timeout(db, 1500);
            if (Sqlite.sqlite3_prepare_v2(db, Utf8("SELECT id, encryptedData, header FROM entities"), -1, out var stmt, IntPtr.Zero) != Sqlite.Ok)
                throw new IOException("Unexpected Ente Auth database layout");
            try
            {
                int rc;
                while ((rc = Sqlite.sqlite3_step(stmt)) == Sqlite.Row)
                {
                    var id = Text(stmt, 0); var data = Text(stmt, 1); var header = Text(stmt, 2);
                    if (id is not null && data is not null && header is not null) rows.Add((id, data, header));
                }
                if (rc != Sqlite.Done) throw new IOException($"Reading the Ente Auth database failed ({rc})");
            }
            finally { Sqlite.sqlite3_finalize(stmt); }
        }
        finally { Sqlite.sqlite3_close_v2(db); }
        return rows;

        static string? Text(IntPtr stmt, int column) => Marshal.PtrToStringUTF8(Sqlite.sqlite3_column_text(stmt, column));
        static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");
    }

    private static class Sqlite
    {
        public const int Ok = 0, Row = 100, Done = 101, OpenReadOnly = 0x1, OpenUri = 0x40;
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll")] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport("winsqlite3.dll")] public static extern int sqlite3_finalize(IntPtr stmt);
    }

    private static class Dpapi
    {
        public static byte[]? Unprotect(byte[] blob)
        {
            var input = new DataBlob { cbData = blob.Length, pbData = Marshal.AllocHGlobal(blob.Length) };
            try
            {
                Marshal.Copy(blob, 0, input.pbData, blob.Length);
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1 /* UI_FORBIDDEN */, out var output)) return null;
                try
                {
                    var result = new byte[output.cbData];
                    Marshal.Copy(output.pbData, result, 0, output.cbData);
                    return result;
                }
                finally
                {
                    ZeroMemory(output.pbData, (nuint)output.cbData);
                    LocalFree(output.pbData);
                }
            }
            finally { Marshal.FreeHGlobal(input.pbData); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int cbData; public IntPtr pbData; }
        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")] private static extern void ZeroMemory(IntPtr dest, nuint size);
    }
}
