using System.Globalization;
using System.Text.Json.Nodes;
using Kite.Plugins;

namespace Kite.Plugins.Builtin.Calcy;

public sealed class CalcyOptions
{
    public int Rounding { get; set; } = 10;
    public bool GroupSeparator { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    public bool CommaDecimal { get; set; }
}

/// <summary>Inline calculator: type "5*7" and the answer is the top result; Enter copies it.</summary>
public sealed class CalcyPlugin : IKitePlugin
{
    public const string PluginId = "calcy";
    private IPluginContext _ctx = null!;
    private CalcyOptions _options = new();

    public string Id => PluginId;
    public string Name => "Calcy";
    public string Description => "Calculator";

    public void Init(IPluginContext context)
    {
        _ctx = context;
        var s = context.Settings;
        _options = new CalcyOptions
        {
            Rounding = s["rounding"]?.GetValue<int>() ?? 10,
            GroupSeparator = s["groupSeparator"]?.GetValue<bool>() ?? true,
            CopyToClipboard = s["copyToClipboard"]?.GetValue<bool>() ?? true,
            CommaDecimal = s["commaDecimal"]?.GetValue<bool>() ?? false,
        };
    }

    public Task<IReadOnlyList<CatItem>> GetResults(Query query, CancellationToken ct)
    {
        if (query.HasOwner || !LooksLikeMath(query.Text) ||
            !Expression.TryEvaluate(query.Text, _options.CommaDecimal, out var value))
            return Task.FromResult<IReadOnlyList<CatItem>>([]);

        var formatted = Format(value, _options);
        IReadOnlyList<CatItem> result =
        [
            new CatItem
            {
                Name = formatted,
                Target = formatted,
                Description = $"{query.Text.Trim()} =" + (_options.CopyToClipboard ? "   ·   Enter to copy" : ""),
                Score = 1000,
            },
        ];
        return Task.FromResult(result);
    }

    public bool Launch(CatItem item, Query query, LaunchMode mode)
    {
        if (_options.CopyToClipboard) _ctx.CopyToClipboard(item.Target);
        return true;
    }

    /// <summary>Only treat input as math when it has a digit and an operator/function, so "7zip" stays a program.</summary>
    public static bool LooksLikeMath(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return false;
        if (t.StartsWith('=')) return t.Length > 1;
        var hasDigit = false;
        var hasOperator = false;
        foreach (var c in t)
        {
            if (char.IsDigit(c)) hasDigit = true;
            else if ("+-*/^%!()×÷".Contains(c)) hasOperator = true;
        }
        if (!hasDigit) return t.Contains('(') && t.EndsWith(')'); // "sqrt(pi)" style still counts
        return hasOperator || t.Contains('x') && char.IsDigit(t[0]) && char.IsDigit(t[^1]);
    }

    public static string Format(double value, CalcyOptions options)
    {
        var rounded = Math.Round(value, Math.Clamp(options.Rounding, 0, 15), MidpointRounding.AwayFromZero);
        var decimals = new string('#', Math.Clamp(options.Rounding, 0, 15));
        var pattern = (options.GroupSeparator ? "#,##0" : "0") + (decimals.Length > 0 ? "." + decimals : "");
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        if (options.CommaDecimal)
        {
            culture.NumberFormat.NumberDecimalSeparator = ",";
            culture.NumberFormat.NumberGroupSeparator = ".";
        }
        return rounded.ToString(pattern, culture);
    }

    public static JsonObject CreateSettings(CalcyOptions o) => new()
    {
        ["rounding"] = o.Rounding,
        ["groupSeparator"] = o.GroupSeparator,
        ["copyToClipboard"] = o.CopyToClipboard,
        ["commaDecimal"] = o.CommaDecimal,
    };
}
