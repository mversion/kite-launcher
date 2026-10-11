using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kite.App.Skins;
using Kite.Core.Skins;

namespace Kite.App.Settings;

public enum PreviewMode { Results, Chip, Empty }

/// <summary>Live miniature of the launcher for the skin editor; uses its own resource dictionary.</summary>
public partial class SkinPreview : UserControl
{
    private readonly ResourceDictionary _tokens = new();

    public SkinPreview()
    {
        InitializeComponent();
        Resources = _tokens;
        SetMode(PreviewMode.Results);
    }

    public void Render(Skin skin, string? folder) => SkinManager.ApplyTo(_tokens, skin, folder);

    public void SetMode(PreviewMode mode)
    {
        Chip.Visibility = mode == PreviewMode.Chip ? Visibility.Visible : Visibility.Collapsed;
        InputText.Text = mode == PreviewMode.Chip ? "bob@exam" : "paint";
        InputText.Visibility = mode == PreviewMode.Empty ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderText.Visibility = mode == PreviewMode.Empty ? Visibility.Visible : Visibility.Collapsed;
        ResultsPanel.Visibility = mode == PreviewMode.Empty ? Visibility.Collapsed : Visibility.Visible;
        Rows.Items.Clear();
        var rows = mode == PreviewMode.Chip
            ? new[] { ("", "bob@example.com", "Compose e-mail · Tab to complete"), ("", "E-Mail", "E-Mail  ▸  bob@exam") }
            : new[] { ("", "Paint", "Store app · Microsoft.Paint"), ("", "Paint 3D", "Store app · Microsoft.MSPaint"), ("", "Search Google for “paint”", "https://www.google.com/search?q=paint") };
        for (var i = 0; i < rows.Length; i++)
            Rows.Items.Add(Row(rows[i].Item1, rows[i].Item2, rows[i].Item3, $"Alt+{i + 1}", selected: i == 0));
    }

    private static FrameworkElement Row(string glyph, string name, string description, string shortcut, bool selected)
    {
        var grid = new Grid { Margin = new Thickness(10, 0, 10, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Kite.Glyph.Foreground");
        var iconBox = new Grid { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Children = { icon } };
        iconBox.SetResourceReference(WidthProperty, "Kite.IconSize");
        iconBox.SetResourceReference(HeightProperty, "Kite.IconSize");
        grid.Children.Add(iconBox);

        var title = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Kite.Results.FontFamily");
        title.SetResourceReference(TextBlock.FontSizeProperty, "Kite.Results.FontSize");
        title.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Kite.Results.SelectedForeground" : "Kite.Results.Foreground");
        var desc = new TextBlock { Text = description, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        desc.SetResourceReference(TextBlock.FontFamilyProperty, "Kite.Results.FontFamily");
        desc.SetResourceReference(TextBlock.FontSizeProperty, "Kite.Results.DescFontSize");
        desc.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Kite.Results.SelectedDescForeground" : "Kite.Results.DescForeground");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, desc } };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var key = new TextBlock { Text = shortcut, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0) };
        key.SetResourceReference(TextBlock.ForegroundProperty, "Kite.Shortcut.Foreground");
        key.SetResourceReference(TextBlock.FontFamilyProperty, "Kite.Results.FontFamily");
        Grid.SetColumn(key, 2);
        grid.Children.Add(key);

        var row = new Border { Margin = new Thickness(6, 1, 6, 1), Child = grid };
        row.SetResourceReference(HeightProperty, "Kite.Results.RowHeight");
        row.SetResourceReference(Border.CornerRadiusProperty, "Kite.Results.SelectionCornerRadius");
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "Kite.Results.SelectedBackground");
        return row;
    }
}
