using System.Globalization;
using System.Text.RegularExpressions;

namespace Kite.Core.Skins;

/// <summary>
/// Best-effort conversion of a Launchy 2.x skin (style.qss + frame.png + author.txt) into a
/// Kite skin. Launchy positions widgets absolutely over a bitmap; Kite uses a flowing layout,
/// so the frame image becomes the input bar background and colours/fonts carry over.
/// </summary>
public static partial class LaunchySkinConverter
{
    public static Skin Convert(string launchyFolder, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        foreach (var file in Directory.EnumerateFiles(launchyFolder))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".bmp" or ".gif" or ".qss" or ".txt")
                File.Copy(file, Path.Combine(destinationFolder, Path.GetFileName(file)), overwrite: true);
        }

        var qssPath = Path.Combine(launchyFolder, "style.qss");
        var rules = File.Exists(qssPath) ? ParseQss(File.ReadAllText(qssPath)) : new Dictionary<string, Dictionary<string, string>>();
        var skin = FromRules(rules);

        var frame = Path.Combine(launchyFolder, "frame.png");
        if (File.Exists(frame) && TryReadPngSize(frame, out var w, out var h))
        {
            skin.BackgroundImage = "frame.png";
            skin.BackgroundImageArea = "input";
            skin.Width = Math.Clamp(w, 300, 1600);
            skin.InputHeight = Math.Clamp(h, 36, 160);
            skin.Background = "Transparent";
            skin.BorderThickness = 0;
            skin.CornerRadius = 0;
            skin.Shadow = false; // Launchy frames carry their own shadow
        }

        var author = Path.Combine(launchyFolder, "author.txt");
        if (File.Exists(author))
        {
            var lines = File.ReadAllLines(author).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            skin.Author = lines.FirstOrDefault(l => l.StartsWith("by ", StringComparison.OrdinalIgnoreCase))?[3..].Trim() ?? lines.ElementAtOrDefault(1) ?? "";
            skin.Description = "Imported from Launchy" + (lines.Count > 2 ? " · " + lines[^1] : "");
        }
        else skin.Description = "Imported from Launchy";
        return skin;
    }

    internal static Skin FromRules(Dictionary<string, Dictionary<string, string>> rules)
    {
        var skin = new Skin { AccentWidth = 0 };

        if (rules.TryGetValue("#input", out var input))
        {
            if (Color(input, "color") is { } c) skin.InputColor = c;
            ApplyFont(input, size => skin.InputFontSize = size, weight => skin.InputFontWeight = weight);
            if (Geometry(input) is { } g) skin.PaddingX = Math.Clamp(g.X, 6, 80);
        }

        if (rules.TryGetValue("#alternatives", out var alt))
        {
            if (Color(alt, "background-color") is { } bg) skin.ResultsBackground = bg;
            if (Color(alt, "color") is { } fg) skin.ResultColor = fg;
            if (Color(alt, "selection-background-color") is { } sbg) skin.SelectedBackground = sbg;
            if (Color(alt, "selection-color") is { } sfg) skin.SelectedColor = sfg;
            ApplyFont(alt, size => skin.ResultFontSize = size, _ => { });
            skin.Accent = skin.SelectedBackground.Length > 0 ? skin.SelectedBackground : skin.Accent;
        }
        if (rules.TryGetValue("#alternativesPath", out var path) && Color(path, "color") is { } pc)
            skin.DescriptionColor = pc;
        // Light result lists need dark secondary text; keep the separator subtle against them.
        skin.SeparatorColor = "#22808080";
        skin.SelectedDescriptionColor = skin.SelectedColor;
        return skin;
    }

    public static Dictionary<string, Dictionary<string, string>> ParseQss(string qss)
    {
        qss = CommentRegex().Replace(qss, "");
        var rules = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in RuleRegex().Matches(qss))
        {
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var decl in m.Groups[2].Value.Split(';'))
            {
                var colon = decl.IndexOf(':');
                if (colon <= 0) continue;
                props[decl[..colon].Trim()] = decl[(colon + 1)..].Trim();
            }
            foreach (var selector in m.Groups[1].Value.Split(','))
            {
                var key = selector.Trim();
                if (key.Length == 0) continue;
                if (!rules.TryGetValue(key, out var existing)) rules[key] = existing = new(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in props) existing[k] = v;
            }
        }
        return rules;
    }

    /// <summary>Converts QSS colours (#rgb, #rrggbb, rgb(), rgba() with 0-255/0-1/% alpha, names) to #AARRGGBB/#RRGGBB.</summary>
    public static string? ToKiteColor(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v.StartsWith('#')) return v.Length is 4 or 7 or 9 ? v.ToUpperInvariant() : null;
        var fn = RgbRegex().Match(v);
        if (fn.Success)
        {
            var parts = fn.Groups[2].Value.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 3) return null;
            byte Channel(string s) => s.EndsWith('%')
                ? (byte)Math.Round(Math.Clamp(double.Parse(s[..^1], CultureInfo.InvariantCulture), 0, 100) * 2.55)
                : (byte)Math.Clamp(double.Parse(s, CultureInfo.InvariantCulture), 0, 255);
            var a = 255;
            if (parts.Length > 3)
            {
                var p = parts[3];
                if (p.EndsWith('%'))
                    a = (int)Math.Round(double.Parse(p[..^1], CultureInfo.InvariantCulture) * 2.55);
                else
                {
                    // Qt accepts 0-255; CSS-style 0.0-1.0 fractions are treated as such.
                    var d = double.Parse(p, CultureInfo.InvariantCulture);
                    a = d <= 1 && (d < 1 || p.Contains('.')) ? (int)Math.Round(d * 255) : (int)d;
                }
            }
            return $"#{Math.Clamp(a, 0, 255):X2}{Channel(parts[0]):X2}{Channel(parts[1]):X2}{Channel(parts[2]):X2}";
        }
        return v switch
        {
            "grey" => "Gray",
            "lightgrey" => "LightGray",
            "darkgrey" => "DarkGray",
            "transparent" => "Transparent",
            _ when v.All(char.IsLetter) => char.ToUpperInvariant(v[0]) + v[1..],
            _ => null,
        };
    }

    private static string? Color(Dictionary<string, string> props, string key) =>
        props.TryGetValue(key, out var v) ? ToKiteColor(v) : null;

    private static void ApplyFont(Dictionary<string, string> props, Action<double> size, Action<string> weight)
    {
        var text = props.TryGetValue("font", out var f) ? f : "";
        if (props.TryGetValue("font-size", out var fs)) text += " " + fs;
        if (props.TryGetValue("font-weight", out var fw)) text += " " + fw;
        var px = PxRegex().Match(text);
        if (px.Success) size(Math.Clamp(double.Parse(px.Groups[1].Value, CultureInfo.InvariantCulture), 8, 60));
        if (text.Contains("bold", StringComparison.OrdinalIgnoreCase)) weight("Bold");
    }

    private static (int X, int Y, int W, int H)? Geometry(Dictionary<string, string> props)
    {
        if (!props.TryGetValue("qproperty-geometry", out var g)) return null;
        var m = GeometryRegex().Match(g);
        return m.Success
            ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value))
            : null;
    }

    /// <summary>Reads width/height from a PNG's IHDR chunk without decoding the image.</summary>
    public static bool TryReadPngSize(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (fs.Read(header) < 24 || header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G') return false;
            width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return width > 0 && height > 0;
        }
        catch (IOException) { return false; }
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex CommentRegex();
    [GeneratedRegex(@"([^{}]+)\{([^}]*)\}")] private static partial Regex RuleRegex();
    [GeneratedRegex(@"^(rgba?)\(([^)]*)\)$")] private static partial Regex RgbRegex();
    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*px", RegexOptions.IgnoreCase)] private static partial Regex PxRegex();
    [GeneratedRegex(@"rect\(\s*(-?\d+)\s+(-?\d+)\s+(\d+)\s+(\d+)\s*\)")] private static partial Regex GeometryRegex();
}
