using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Kite.Plugins;

namespace Kite.App;

public sealed class ResultRow : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public ResultRow(CatItem item, int index)
    {
        Item = item;
        Shortcut = index < 9 ? $"Alt+{index + 1}" : "";
        Glyph = GlyphFor(item);
    }

    public CatItem Item { get; }
    public string Name => Item.Name;
    public string Description => Item.Description ?? Item.Target;
    public string Detail => Item.Detail ?? "";
    public string Shortcut { get; }
    public string Glyph { get; }

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasIcon)); }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>Segoe Fluent/MDL2 glyph shown until (or instead of) a real icon.</summary>
    private static string GlyphFor(CatItem item) => item.PluginId switch
    {
        "calcy" => "",
        "weby" when item.Data?.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) == true => "",
        "weby" => "",
        "runner" => "",
        "kite" => "",
        _ => "",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A Tab-completed segment: the owner item, or a finished argument.</summary>
public sealed record Chip(CatItem? Item, string TypedText)
{
    public string Display => Item?.Name ?? TypedText;
}
