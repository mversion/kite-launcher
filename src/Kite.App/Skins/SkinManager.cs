using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kite.App.Services;
using Kite.Core.Settings;
using Kite.Core.Skins;

namespace Kite.App.Skins;

/// <summary>
/// Turns a skin.json into WPF resources (all bound with DynamicResource, so changes apply
/// live) and optionally merges a Theme.xaml for deeper restyling. Watches the active
/// skin folder and hot-reloads on save.
/// </summary>
public sealed class SkinManager : IDisposable
{
    private readonly KitePaths _paths;
    private readonly ResourceDictionary _target;
    private readonly Dispatcher _dispatcher;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private ResourceDictionary? _themeDictionary;

    public SkinManager(KitePaths paths, ResourceDictionary target, Dispatcher dispatcher)
    {
        _paths = paths;
        _target = target;
        _dispatcher = dispatcher;
    }

    public Skin Current { get; private set; } = new();
    public string? CurrentFolder { get; private set; }
    public event Action<Skin>? Applied;

    public SkinLibrary Library => _library ??= new SkinLibrary(_paths);
    private SkinLibrary? _library;

    public IEnumerable<(string Name, string Folder)> Available() => Library.List().Select(s => (s.Name, s.Folder));

    public void Reload()
    {
        if (CurrentFolder is not null) Load(Path.GetFileName(CurrentFolder));
    }

    public void Load(string name)
    {
        var folder = Available().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Folder;
        Skin skin;
        if (folder is null)
        {
            Logger.Log($"Skin '{name}' not found; using built-in defaults");
            skin = new Skin();
        }
        else
        {
            try
            {
                skin = JsonSerializer.Deserialize<Skin>(File.ReadAllText(Path.Combine(folder, SkinLibrary.FileName)), Json.Options) ?? new Skin();
            }
            catch (Exception ex)
            {
                Logger.Log($"Skin '{name}' is invalid ({ex.Message}); keeping the current skin");
                return;
            }
        }
        CurrentFolder = folder;
        Current = skin;
        Apply(skin, folder);
        Watch(folder);
    }

    private void Apply(Skin s, string? folder)
    {
        ApplyTo(_target, s, folder);
        ApplyTheme(folder);
        Applied?.Invoke(s);
    }

    /// <summary>
    /// Writes a skin's tokens into <paramref name="target"/>. Used for the live app resources and
    /// for the skin editor's preview, which gets its own dictionary.
    /// </summary>
    public static void ApplyTo(ResourceDictionary target, Skin s, string? folder)
    {
        void Set(string key, object value) => target[key] = value;
        var accent = Brush(s.Accent, Brushes.DeepPink);
        var shadowMargin = s.Shadow ? s.ShadowBlur : 0;
        var innerRadius = Math.Max(0, s.CornerRadius - s.BorderThickness);
        Set("Kite.Width", s.Width);
        Set("Kite.WindowWidth", s.Width + shadowMargin * 2);
        // Results sit inside the frame: round only the bottom corners (not the left one when an accent strip is there).
        Set("Kite.ResultsCornerRadius", new CornerRadius(0, 0, innerRadius, s.AccentWidth > 0 ? 0 : innerRadius));
        Set("Kite.CornerRadius", new CornerRadius(s.CornerRadius));
        Set("Kite.Background", Background(s.Background));
        var image = BackgroundImage(s.BackgroundImage, folder);
        var onInput = string.Equals(s.BackgroundImageArea, "input", StringComparison.OrdinalIgnoreCase);
        Set("Kite.BackgroundImage", onInput ? Brushes.Transparent : image);
        Set("Kite.InputBackgroundImage", onInput ? image : Brushes.Transparent);
        Set("Kite.BorderBrush", Brush(s.BorderColor, Brushes.Transparent));
        Set("Kite.BorderThickness", new Thickness(s.BorderThickness));
        Set("Kite.Accent", accent);
        Set("Kite.AccentWidth", s.AccentWidth);
        Set("Kite.AccentCornerRadius", new CornerRadius(s.CornerRadius, 0, 0, s.CornerRadius));
        Set("Kite.Shadow", s.Shadow
            ? new DropShadowEffect { BlurRadius = s.ShadowBlur, ShadowDepth = 4, Direction = 270, Opacity = s.ShadowOpacity, Color = Color(s.ShadowColor, Colors.Black) }
            : null!);
        Set("Kite.ShadowMargin", new Thickness(shadowMargin));

        Set("Kite.Input.Height", s.InputHeight);
        Set("Kite.Input.Padding", new Thickness(s.PaddingX, 0, s.PaddingX, 0));
        Set("Kite.Input.FontFamily", new FontFamily(s.InputFont));
        Set("Kite.Input.FontSize", s.InputFontSize);
        Set("Kite.Input.FontWeight", FontWeight(s.InputFontWeight));
        Set("Kite.Input.Foreground", Brush(s.InputColor, Brushes.White));
        Set("Kite.Input.Caret", Brush(string.IsNullOrEmpty(s.CaretColor) ? s.InputColor : s.CaretColor, Brushes.White));
        Set("Kite.Placeholder.Foreground", Brush(s.PlaceholderColor, Brushes.Gray));
        Set("Kite.Placeholder.Text", s.Placeholder);

        Set("Kite.Chip.Background", string.IsNullOrEmpty(s.ChipBackground) ? accent : Brush(s.ChipBackground, accent));
        Set("Kite.Chip.Foreground", Brush(s.ChipColor, Brushes.White));

        Set("Kite.Results.Background", Brush(s.ResultsBackground, Brushes.Transparent));
        Set("Kite.Separator", Brush(s.SeparatorColor, Brushes.Transparent));
        Set("Kite.Results.FontFamily", new FontFamily(s.ResultFont));
        Set("Kite.Results.FontSize", s.ResultFontSize);
        Set("Kite.Results.Foreground", Brush(s.ResultColor, Brushes.White));
        Set("Kite.Results.DescFontSize", s.DescriptionFontSize);
        Set("Kite.Results.DescForeground", Brush(s.DescriptionColor, Brushes.Gray));
        Set("Kite.Results.SelectedBackground", string.IsNullOrEmpty(s.SelectedBackground) ? accent : Brush(s.SelectedBackground, accent));
        Set("Kite.Results.SelectedForeground", Brush(s.SelectedColor, Brushes.White));
        Set("Kite.Results.SelectedDescForeground", Brush(s.SelectedDescriptionColor, Brushes.White));
        Set("Kite.Results.SelectionCornerRadius", new CornerRadius(s.SelectionCornerRadius));
        Set("Kite.Results.RowHeight", s.RowHeight);
        Set("Kite.IconSize", s.IconSize);
        Set("Kite.Glyph.Foreground", string.IsNullOrEmpty(s.GlyphColor) ? accent : Brush(s.GlyphColor, accent));
        Set("Kite.Shortcut.Foreground", Brush(s.ShortcutColor, Brushes.Gray));

        // Menus can't be translucent gradients nicely; use the first background colour, made opaque.
        var menuBg = string.IsNullOrEmpty(s.MenuBackground) ? FirstColor(s.Background) : Color(s.MenuBackground, Colors.Black);
        if (string.IsNullOrEmpty(s.MenuBackground)) menuBg.A = 255;
        Set("Kite.Menu.Background", Frozen(new SolidColorBrush(menuBg)));
        Set("Kite.Menu.Border", Brush(string.IsNullOrEmpty(s.MenuBorderColor) ? s.SeparatorColor : s.MenuBorderColor, Brushes.Gray));
        Set("Kite.Menu.Foreground", Brush(string.IsNullOrEmpty(s.MenuColor) ? s.ResultColor : s.MenuColor, Brushes.White));
    }

    /// <summary>An optional Theme.xaml in the skin folder can override any style or resource.</summary>
    private void ApplyTheme(string? folder)
    {
        if (_themeDictionary is not null)
        {
            _target.MergedDictionaries.Remove(_themeDictionary);
            _themeDictionary = null;
        }
        var file = folder is null ? null : Path.Combine(folder, "Theme.xaml");
        if (file is null || !File.Exists(file)) return;
        try
        {
            using var stream = File.OpenRead(file);
            var context = new ParserContext { BaseUri = new Uri(file) };
            _themeDictionary = (ResourceDictionary)XamlReader.Load(stream, context);
            _target.MergedDictionaries.Add(_themeDictionary);
        }
        catch (Exception ex) { Logger.Log($"Theme.xaml failed to load: {ex.Message}"); }
    }

    private void Watch(string? folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (folder is null) return;
        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
    }

    private void ScheduleReload() => _dispatcher.BeginInvoke(() =>
    {
        _debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            _debounce!.Stop();
            Logger.Log($"Skin files changed; reloading '{Path.GetFileName(CurrentFolder)}'");
            if (CurrentFolder is not null) Load(Path.GetFileName(CurrentFolder));
        }, _dispatcher);
        _debounce.Stop();
        _debounce.Start();
    });

    internal static Color Color(string text, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(text.Trim()); }
        catch { return fallback; }
    }

    private static Color FirstColor(string background)
    {
        var text = background.StartsWith("linear:", StringComparison.OrdinalIgnoreCase)
            ? background[7..].Split(',', StringSplitOptions.TrimEntries)[0]
            : background;
        return Color(text, Colors.Black);
    }

    private static SolidColorBrush Frozen(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    internal static SolidColorBrush Brush(string text, SolidColorBrush fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var b = new SolidColorBrush(Color(text, fallback.Color));
        b.Freeze();
        return b;
    }

    internal static System.Windows.Media.Brush Background(string text)
    {
        if (text.StartsWith("linear:", StringComparison.OrdinalIgnoreCase))
        {
            var stops = text[7..].Split(',', StringSplitOptions.TrimEntries);
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            for (var i = 0; i < stops.Length; i++)
                g.GradientStops.Add(new GradientStop(Color(stops[i], Colors.Black), stops.Length == 1 ? 0 : (double)i / (stops.Length - 1)));
            g.Freeze();
            return g;
        }
        return Brush(text, new SolidColorBrush(Colors.Black));
    }

    private static System.Windows.Media.Brush BackgroundImage(string? file, string? folder)
    {
        if (string.IsNullOrWhiteSpace(file) || folder is null) return Brushes.Transparent;
        var path = Path.Combine(folder, file);
        if (!File.Exists(path)) return Brushes.Transparent;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    private static FontWeight FontWeight(string text)
    {
        try { return (FontWeight)new FontWeightConverter().ConvertFromString(text)!; }
        catch { return FontWeights.Normal; }
    }

    public void Dispose() => _watcher?.Dispose();
}
