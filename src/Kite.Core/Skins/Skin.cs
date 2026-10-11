namespace Kite.Core.Skins;

/// <summary>
/// Design tokens loaded from skins/&lt;name&gt;/skin.json. Every property is optional;
/// missing values fall back to these defaults. Colours accept #RGB, #RRGGBB, #AARRGGBB
/// or named colours; <see cref="Background"/> also accepts "linear:#top,#bottom".
/// </summary>
public sealed class Skin
{
    public string Name { get; set; } = "Default";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";

    // Frame
    public double Width { get; set; } = 640;
    public double CornerRadius { get; set; } = 10;
    public string Background { get; set; } = "#F0202020";
    /// <summary>Optional image file (relative to the skin folder) drawn over the background.</summary>
    public string? BackgroundImage { get; set; }
    /// <summary>
    /// Where <see cref="BackgroundImage"/> is drawn: "frame" (the whole launcher, stretched) or
    /// "input" (just the input bar - how Launchy skins' frame.png works).
    /// </summary>
    public string BackgroundImageArea { get; set; } = "frame";
    public string BorderColor { get; set; } = "#33FFFFFF";
    public double BorderThickness { get; set; } = 1;
    public string Accent { get; set; } = "#E91E63";
    /// <summary>Width of the coloured strip on the left edge; 0 hides it.</summary>
    public double AccentWidth { get; set; } = 0;
    public bool Shadow { get; set; } = true;
    public string ShadowColor { get; set; } = "#000000";
    public double ShadowOpacity { get; set; } = 0.45;
    public double ShadowBlur { get; set; } = 24;

    // Input row
    public double InputHeight { get; set; } = 56;
    public double PaddingX { get; set; } = 16;
    public string InputFont { get; set; } = "Segoe UI Variable Display, Segoe UI";
    public double InputFontSize { get; set; } = 22;
    public string InputFontWeight { get; set; } = "Normal";
    public string InputColor { get; set; } = "#FFFFFF";
    public string CaretColor { get; set; } = "";
    public string PlaceholderColor { get; set; } = "#66FFFFFF";
    public string Placeholder { get; set; } = "Type to launch…";

    // Chips (Tab-completed segments)
    public string ChipBackground { get; set; } = "";
    public string ChipColor { get; set; } = "#FFFFFF";

    // Results
    public string ResultsBackground { get; set; } = "Transparent";
    public string SeparatorColor { get; set; } = "#22FFFFFF";
    public string ResultFont { get; set; } = "Segoe UI Variable Text, Segoe UI";
    public double ResultFontSize { get; set; } = 14;
    public string ResultColor { get; set; } = "#E6FFFFFF";
    public double DescriptionFontSize { get; set; } = 11;
    public string DescriptionColor { get; set; } = "#80FFFFFF";
    public string SelectedBackground { get; set; } = "";
    public string SelectedColor { get; set; } = "#FFFFFF";
    public string SelectedDescriptionColor { get; set; } = "#CCFFFFFF";
    public double SelectionCornerRadius { get; set; } = 6;
    public double RowHeight { get; set; } = 48;
    public double IconSize { get; set; } = 32;
    public string GlyphColor { get; set; } = "";
    public string ShortcutColor { get; set; } = "#55FFFFFF";

    // Right-click menu (defaults derive from the frame colours)
    public string MenuBackground { get; set; } = "";
    public string MenuBorderColor { get; set; } = "";
    public string MenuColor { get; set; } = "";
}
