using System.Globalization;
using System.Text;

namespace Kite.LaunchyImport;

/// <summary>
/// Reader for Qt's QSettings INI format as written by Launchy: percent-encoded keys,
/// quoted values with C-style escapes, comma-separated string lists, and @Variant blobs.
/// </summary>
public sealed class QSettingsIni
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static QSettingsIni Load(string path) => Parse(File.ReadAllText(path));

    public static QSettingsIni Parse(string text)
    {
        var ini = new QSettingsIni();
        var current = ini.Section("General");
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = ini.Section(DecodeKey(line[1..^1]));
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            current[DecodeKey(line[..eq].Trim())] = line[(eq + 1)..].Trim();
        }
        return ini;
    }

    private Dictionary<string, string> Section(string name)
    {
        if (!_sections.TryGetValue(name, out var s))
            _sections[name] = s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return s;
    }

    public IReadOnlyDictionary<string, string> GetSection(string name) =>
        _sections.TryGetValue(name, out var s) ? s : new Dictionary<string, string>();

    public bool HasSection(string name) => _sections.ContainsKey(name);

    /// <summary>Raw (still escaped) value.</summary>
    public string? Raw(string section, string key) =>
        _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    public string? GetString(string section, string key)
    {
        var raw = Raw(section, key);
        if (raw is null || raw.StartsWith('@')) return null;
        var list = ParseList(raw);
        return list.Count == 0 ? "" : string.Join(", ", list);
    }

    public List<string> GetList(string section, string key)
    {
        var raw = Raw(section, key);
        return raw is null || raw.StartsWith('@') ? [] : ParseList(raw);
    }

    public bool? GetBool(string section, string key) => GetString(section, key)?.ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    public int? GetInt(string section, string key) =>
        int.TryParse(GetString(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Reads a QSettings array: "group\size" plus "group\1\field", "group\2\field", ...</summary>
    public List<Dictionary<string, string>> GetArray(string section, string group)
    {
        var prefix = string.IsNullOrEmpty(group) ? "" : group + "\\";
        var size = GetInt(section, prefix + "size") ?? 0;
        var result = new List<Dictionary<string, string>>();
        for (var i = 1; i <= size; i++)
        {
            var entry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var itemPrefix = $"{prefix}{i}\\";
            foreach (var (k, _) in GetSection(section))
            {
                if (!k.StartsWith(itemPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var field = k[itemPrefix.Length..];
                if (field.Contains('\\')) continue; // nested groups, e.g. "2\Display\pos"
                entry[field] = GetString(section, k) ?? "";
            }
            result.Add(entry);
        }
        return result;
    }

    /// <summary>Splits a value into its list items, honouring quotes and escapes.</summary>
    public static List<string> ParseList(string raw)
    {
        var items = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var quotedItem = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '"') { inQuotes = !inQuotes; quotedItem = true; continue; }
            if (c == '\\' && i + 1 < raw.Length)
            {
                i = ReadEscape(raw, i, sb);
                continue;
            }
            if (c == ',' && !inQuotes)
            {
                items.Add(quotedItem ? sb.ToString() : sb.ToString().Trim());
                sb.Clear();
                quotedItem = false;
                while (i + 1 < raw.Length && raw[i + 1] == ' ') i++;
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0 || items.Count > 0 || quotedItem) items.Add(quotedItem ? sb.ToString() : sb.ToString().Trim());
        return items;
    }

    private static int ReadEscape(string raw, int i, StringBuilder sb)
    {
        var next = raw[i + 1];
        switch (next)
        {
            case '\\': sb.Append('\\'); return i + 1;
            case '"': sb.Append('"'); return i + 1;
            case 'n': sb.Append('\n'); return i + 1;
            case 't': sb.Append('\t'); return i + 1;
            case 'r': sb.Append('\r'); return i + 1;
            case '0': sb.Append('\0'); return i + 1;
            case 'x':
                var j = i + 2;
                var hex = new StringBuilder();
                while (j < raw.Length && hex.Length < 4 && Uri.IsHexDigit(raw[j])) hex.Append(raw[j++]);
                if (hex.Length > 0) sb.Append((char)Convert.ToInt32(hex.ToString(), 16));
                return j - 1;
            default:
                sb.Append(next);
                return i + 1;
        }
    }

    /// <summary>QSettings percent-encodes special characters in keys ("po%20chr" → "po chr", "%U00E9" → "é").</summary>
    public static string DecodeKey(string key)
    {
        if (!key.Contains('%')) return key;
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] == '%' && i + 1 < key.Length && key[i + 1] == 'U' && IsHex(key, i + 2, 4))
            {
                sb.Append((char)Convert.ToInt32(key.Substring(i + 2, 4), 16));
                i += 5;
            }
            else if (key[i] == '%' && IsHex(key, i + 1, 2))
            {
                sb.Append((char)Convert.ToInt32(key.Substring(i + 1, 2), 16));
                i += 2;
            }
            else sb.Append(key[i]);
        }
        return sb.ToString();
    }

    private static bool IsHex(string s, int start, int count)
    {
        if (start + count > s.Length) return false;
        for (var i = start; i < start + count; i++)
            if (!Uri.IsHexDigit(s[i])) return false;
        return true;
    }
}
