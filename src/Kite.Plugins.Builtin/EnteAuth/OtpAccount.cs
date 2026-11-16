using System.Security.Cryptography;
using System.Text.Json;

namespace Kite.Plugins.Builtin.EnteAuth;

public enum OtpType { Totp, Hotp, Steam }

/// <summary>One 2FA account, parsed from an otpauth:// URI as Ente Auth stores it.</summary>
public sealed record OtpAccount(string Id, string Issuer, string Account, OtpType Type, byte[] Secret, string Algorithm, int Digits, int Period, bool Trashed)
{
    private const string SteamAlphabet = "23456789BCDFGHJKMNPQRTVWXY";

    /// <summary>The issuer, or the account name when there is no issuer.</summary>
    public string Title => Issuer.Length > 0 ? Issuer : Account;

    /// <summary>The code for <paramref name="now"/> and the seconds until it changes. Not for HOTP.</summary>
    public (string Code, int SecondsLeft) CodeAt(DateTimeOffset now)
    {
        var unix = now.ToUnixTimeSeconds();
        var period = Math.Max(1, Period);
        var counter = unix / period;
        var left = (int)(period - unix % period);
        return (Generate(counter), left);
    }

    public string Generate(long counter)
    {
        var msg = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(msg, counter);
        var hash = Algorithm switch
        {
            "SHA256" => HMACSHA256.HashData(Secret, msg),
            "SHA512" => HMACSHA512.HashData(Secret, msg),
            _ => HMACSHA1.HashData(Secret, msg),
        };
        var o = hash[^1] & 0x0f;
        var bin = ((hash[o] & 0x7f) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];

        if (Type == OtpType.Steam)
        {
            var chars = new char[Digits];
            for (var i = 0; i < Digits; i++) { chars[i] = SteamAlphabet[bin % SteamAlphabet.Length]; bin /= SteamAlphabet.Length; }
            return new string(chars);
        }
        var code = (bin % (int)Math.Pow(10, Digits)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return code.PadLeft(Digits, '0');
    }

    /// <summary>"482913" → "482 913", "12345678" → "1234 5678"; Steam codes unchanged.</summary>
    public static string Pretty(string code) =>
        code.Length is 6 or 8 && code.All(char.IsAsciiDigit) ? $"{code[..(code.Length / 2)]} {code[(code.Length / 2)..]}" : code;

    /// <summary>
    /// Parses otpauth://TYPE/LABEL?secret=..&amp;issuer=..&amp;algorithm=..&amp;digits=..&amp;period=..
    /// plus Ente's codeDisplay={"trashed":..} parameter. Returns null for anything it can't use.
    /// </summary>
    public static OtpAccount? Parse(string id, string uri)
    {
        const string scheme = "otpauth://";
        if (!uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = uri[scheme.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return null;
        var type = rest[..slash].ToLowerInvariant() switch
        {
            "totp" => OtpType.Totp,
            "hotp" => OtpType.Hotp,
            "steam" => OtpType.Steam,
            _ => (OtpType?)null,
        };
        if (type is null) return null;
        rest = rest[(slash + 1)..];
        var q = rest.IndexOf('?');
        var label = Unescape(q < 0 ? rest : rest[..q]);
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q >= 0)
            foreach (var pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) query[Unescape(pair[..eq])] = Unescape(pair[(eq + 1)..]);
            }

        if (!query.TryGetValue("secret", out var secretText) || Base32Decode(secretText) is not { Length: > 0 } secret) return null;

        var colon = label.IndexOf(':');
        var labelIssuer = colon > 0 ? label[..colon].Trim() : "";
        var account = (colon > 0 ? label[(colon + 1)..] : label).Trim();
        var issuer = query.TryGetValue("issuer", out var i) && i.Trim().Length > 0 ? i.Trim() : labelIssuer;
        if (account.Length == 0 && issuer.Length == 0) return null;

        var algorithm = query.TryGetValue("algorithm", out var a) ? a.ToUpperInvariant().Replace("-", "") : "SHA1";
        var digits = query.TryGetValue("digits", out var d) && int.TryParse(d, out var dn) && dn is >= 4 and <= 10 ? dn : type == OtpType.Steam ? 5 : 6;
        var period = query.TryGetValue("period", out var p) && int.TryParse(p, out var pn) && pn is > 0 and <= 3600 ? pn : 30;

        var trashed = false;
        if (query.TryGetValue("codeDisplay", out var display))
        {
            try
            {
                using var doc = JsonDocument.Parse(display);
                trashed = doc.RootElement.TryGetProperty("trashed", out var t) && t.ValueKind == JsonValueKind.True;
            }
            catch (JsonException) { }
        }
        return new OtpAccount(id, issuer, account, type.Value, secret, algorithm, digits, period, trashed);
    }

    private static string Unescape(string s) => Uri.UnescapeDataString(s); // '+' stays '+' (e.g. name+tag@example.com)

    public static byte[]? Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            if (raw is '=' or ' ' or '-') continue;
            var v = alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); buffer &= (1 << bits) - 1; }
        }
        return bytes.ToArray();
    }
}
