using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kite.App.Settings;

/// <summary>
/// Colour editor: a swatch that opens a picker (saturation/value square, hue and alpha bars,
/// hex box, palette) plus an inline hex box. An empty value means "Auto" (inherit/derive),
/// when <see cref="AllowAuto"/> is on.
/// </summary>
public sealed class ColorField : StackPanel
{
    private readonly Border _swatch;
    private readonly TextBox _hex;
    private readonly Button? _autoButton;
    private Popup? _popup;
    private bool _updating;

    // Picker state
    private double _h, _s, _v, _a = 1;
    private Rectangle? _hueLayer;
    private Ellipse? _svThumb;
    private Border? _hueThumb, _alphaThumb;
    private Border? _alphaGradient;
    private TextBox? _popupHex;
    private const double SvWidth = 220, SvHeight = 150, BarHeight = 14;

    public ColorField(string value, bool allowAuto, IReadOnlyList<string>? palette = null)
    {
        Orientation = Orientation.Horizontal;
        AllowAuto = allowAuto;
        Palette = palette ?? [];

        _swatch = new Border
        {
            Width = 34, Height = 26, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), Cursor = Cursors.Hand,
            Background = Checkerboard(), ToolTip = "Pick a colour",
            Child = new Border { CornerRadius = new CornerRadius(4) },
        };
        _swatch.MouseLeftButtonUp += (_, _) => OpenPicker();
        Children.Add(_swatch);

        _hex = new TextBox { Width = 104, Margin = new Thickness(8, 0, 0, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        _hex.Tag = allowAuto ? "Auto" : "#RRGGBB";
        _hex.TextChanged += (_, _) =>
        {
            if (_updating) return;
            var text = _hex.Text.Trim();
            if (text.Length == 0 && AllowAuto) Commit("");
            else if (TryParse(text, out _)) Commit(text);
        };
        Children.Add(_hex);

        if (allowAuto)
        {
            _autoButton = new Button { Content = "", ToolTip = "Reset to Auto", Margin = new Thickness(2, 0, 0, 0) };
            _autoButton.SetResourceReference(StyleProperty, "S.IconButton");
            _autoButton.FontSize = 9;
            _autoButton.Click += (_, _) => { Value = ""; Changed?.Invoke(""); };
            Children.Add(_autoButton);
        }
        Value = value;
    }

    public bool AllowAuto { get; }
    public IReadOnlyList<string> Palette { get; }
    public event Action<string>? Changed;

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            _value = value ?? "";
            _updating = true;
            if (_hex.Text != _value) _hex.Text = _value;
            _updating = false;
            RefreshSwatch();
        }
    }

    private void Commit(string value)
    {
        _value = value;
        RefreshSwatch();
        Changed?.Invoke(value);
    }

    private void RefreshSwatch()
    {
        var inner = (Border)_swatch.Child;
        inner.Background = TryParse(_value, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
        if (_autoButton is not null) _autoButton.Visibility = _value.Length > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    public static bool TryParse(string text, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(text.Trim());
            return true;
        }
        catch (FormatException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    public static string ToHex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    // ------------------------------------------------------------------ picker popup

    private void OpenPicker()
    {
        var start = TryParse(_value, out var c) ? c : Colors.White;
        (_h, _s, _v) = ToHsv(start);
        _a = start.A / 255.0;
        _popup ??= BuildPopup();
        UpdatePicker(updateHex: true);
        _popup.IsOpen = true;
    }

    private Popup BuildPopup()
    {
        var root = new StackPanel { Margin = new Thickness(12) };

        // Saturation/value square: hue fill, white→transparent horizontally, transparent→black vertically.
        var sv = new Grid { Width = SvWidth, Height = SvHeight, Cursor = Cursors.Cross, ClipToBounds = true };
        _hueLayer = new Rectangle();
        sv.Children.Add(_hueLayer);
        sv.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
        sv.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        var svCanvas = new Canvas();
        _svThumb = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        svCanvas.Children.Add(_svThumb);
        sv.Children.Add(svCanvas);
        Drag(sv, p =>
        {
            _s = Math.Clamp(p.X / SvWidth, 0, 1);
            _v = 1 - Math.Clamp(p.Y / SvHeight, 0, 1);
            UpdatePicker(updateHex: true);
            Emit();
        });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = sv });

        // Hue bar
        var hueBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var hues = new[] { Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Red };
        for (var i = 0; i < hues.Length; i++) hueBrush.GradientStops.Add(new GradientStop(hues[i], i / 6.0));
        var hueBar = Bar(hueBrush, out _hueThumb);
        Drag(hueBar, p => { _h = Math.Clamp(p.X / SvWidth, 0, 1) * 360; UpdatePicker(updateHex: true); Emit(); });
        hueBar.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(hueBar);

        // Alpha bar
        _alphaGradient = new Border();
        var alphaBar = Bar(Checkerboard(), out _alphaThumb, _alphaGradient);
        Drag(alphaBar, p => { _a = Math.Clamp(p.X / SvWidth, 0, 1); UpdatePicker(updateHex: true); Emit(); });
        alphaBar.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(alphaBar);

        // Hex + palette
        _popupHex = new TextBox { Margin = new Thickness(0, 10, 0, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        _popupHex.TextChanged += (_, _) =>
        {
            if (_updating || !TryParse(_popupHex.Text, out var c)) return;
            (_h, _s, _v) = ToHsv(c);
            _a = c.A / 255.0;
            UpdatePicker(updateHex: false);
            Emit();
        };
        root.Children.Add(_popupHex);

        var swatches = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Width = SvWidth };
        var defaults = new[] { "#FFFFFF", "#D8D8D8", "#808080", "#2B2B2B", "#141414", "#000000", "#C2185B", "#E91E63", "#00C4FF", "#0067C0", "#7C3AED", "#A78BFA", "#22C55E", "#F59E0B", "#EF4444", "#00000000" };
        foreach (var hex in Palette.Concat(defaults).Where(p => TryParse(p, out _)).Distinct(StringComparer.OrdinalIgnoreCase).Take(24))
        {
            TryParse(hex, out var sc);
            var chip = new Border
            {
                Width = 20, Height = 20, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(4),
                Background = Checkerboard(), Cursor = Cursors.Hand, ToolTip = hex,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), BorderThickness = new Thickness(1),
                Child = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(sc) },
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                (_h, _s, _v) = ToHsv(sc);
                _a = sc.A / 255.0;
                UpdatePicker(updateHex: true);
                Emit();
            };
            swatches.Children.Add(chip);
        }
        root.Children.Add(swatches);

        var frame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 4, 12, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45 },
            Child = root,
        };
        var popup = new Popup
        {
            PlacementTarget = _swatch,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = frame,
        };
        Children.Add(popup); // keeps it in the logical tree so the settings theme styles apply
        return popup;
    }

    private static Grid Bar(Brush background, out Border thumb, UIElement? overlay = null)
    {
        var bar = new Grid { Width = SvWidth, Height = BarHeight, Cursor = Cursors.Hand };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(BarHeight / 2), Background = background });
        if (overlay is not null) bar.Children.Add(overlay);
        var canvas = new Canvas { IsHitTestVisible = false };
        thumb = new Border
        {
            Width = 8, Height = BarHeight + 4, CornerRadius = new CornerRadius(3), Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), BorderThickness = new Thickness(1),
        };
        Canvas.SetTop(thumb, -2);
        canvas.Children.Add(thumb);
        bar.Children.Add(canvas);
        return bar;
    }

    private static void Drag(Panel area, Action<Point> moved)
    {
        area.Background ??= Brushes.Transparent;
        area.MouseLeftButtonDown += (_, e) => { area.CaptureMouse(); moved(e.GetPosition(area)); e.Handled = true; };
        area.MouseMove += (_, e) => { if (area.IsMouseCaptured) moved(e.GetPosition(area)); };
        area.MouseLeftButtonUp += (_, _) => area.ReleaseMouseCapture();
    }

    private void UpdatePicker(bool updateHex)
    {
        var pure = FromHsv(_h, 1, 1, 1);
        if (_hueLayer is not null) _hueLayer.Fill = new SolidColorBrush(pure);
        if (_svThumb is not null)
        {
            Canvas.SetLeft(_svThumb, _s * SvWidth - 6);
            Canvas.SetTop(_svThumb, (1 - _v) * SvHeight - 6);
        }
        if (_hueThumb is not null) Canvas.SetLeft(_hueThumb, _h / 360 * SvWidth - 4);
        var opaque = FromHsv(_h, _s, _v, 1);
        if (_alphaGradient is not null)
        {
            _alphaGradient.CornerRadius = new CornerRadius(BarHeight / 2);
            _alphaGradient.Background = new LinearGradientBrush(Color.FromArgb(0, opaque.R, opaque.G, opaque.B), opaque, 0);
        }
        if (_alphaThumb is not null) Canvas.SetLeft(_alphaThumb, _a * SvWidth - 4);
        if (updateHex && _popupHex is not null)
        {
            _updating = true;
            _popupHex.Text = ToHex(FromHsv(_h, _s, _v, _a));
            _updating = false;
        }
    }

    private void Emit()
    {
        var hex = ToHex(FromHsv(_h, _s, _v, _a));
        Value = hex;
        Changed?.Invoke(hex);
    }

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v, double a)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        byte B(double f) => (byte)Math.Round((f + m) * 255);
        return Color.FromArgb((byte)Math.Round(a * 255), B(r), B(g), B(b));
    }

    /// <summary>Grey checkerboard so translucent colours are visible.</summary>
    public static Brush Checkerboard()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } }));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}
