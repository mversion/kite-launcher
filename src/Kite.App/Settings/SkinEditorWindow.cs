using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kite.Core.Skins;
using Microsoft.Win32;

namespace Kite.App.Settings;

/// <summary>
/// Visual skin editor: a live preview on top and every skin token below. Edits are written to
/// the skin's skin.json shortly after each change; if it is the active skin, the real launcher
/// updates too (the skin folder is watched).
/// </summary>
public sealed class SkinEditorWindow : Window
{
    private static readonly Lazy<string[]> SystemFonts = new(() =>
        Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray());

    private static readonly string[] Weights = ["Thin", "ExtraLight", "Light", "Normal", "Medium", "SemiBold", "Bold", "ExtraBold", "Black"];

    private readonly ISettingsHost _host;
    private readonly SkinInfo _info;
    private readonly Skin _skin;
    private readonly SkinPreview _preview = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private string[] _palette = [];

    public event Action? Saved;

    public SkinEditorWindow(ISettingsHost host, SkinInfo info)
    {
        _host = host;
        _info = info;
        _skin = SkinLibrary.Clone(info.Skin);

        Title = $"Skin editor - {info.Name}";
        Width = 1120;
        Height = 860;
        MinWidth = 820;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Kite;component/Settings/SettingsTheme.xaml") });
        SetResourceReference(BackgroundProperty, "S.Bg");
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) => Save(), Dispatcher);
        _saveTimer.Stop();
        Closing += (_, _) => { if (_saveTimer.IsEnabled) Save(); };

        RefreshPalette();
        Content = BuildLayout();
        _preview.Render(_skin, _info.Folder);
        SetStatus("Changes save automatically", ok: false);
    }

    // ================================================================= layout

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        // Header
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = _info.Name, FontSize = 22, FontWeight = FontWeights.SemiBold });
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        sub.Children.Add(new TextBlock { Text = _info.Folder, FontSize = 11.5, Foreground = Brush("S.Faint"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        sub.Children.Add(_status);
        title.Children.Add(sub);

        var mode = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 8, 0), ToolTip = "What the preview shows" };
        foreach (var (label, m) in new[] { ("Preview: results", PreviewMode.Results), ("Preview: Tab chip", PreviewMode.Chip), ("Preview: empty", PreviewMode.Empty) })
            mode.Items.Add(new ComboBoxItem { Content = label, Tag = m });
        mode.SelectedIndex = 0;
        mode.SelectionChanged += (_, _) => { if (mode.SelectedItem is ComboBoxItem { Tag: PreviewMode m }) _preview.SetMode(m); };

        var backdropHost = new Border { Padding = new Thickness(24, 18, 24, 18), Height = 300 };
        var backdrop = new ComboBox { MinWidth = 140, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Preview background" };
        var backdrops = new (string Label, Brush Brush)[]
        {
            ("Dark desktop", new LinearGradientBrush(Color.FromRgb(0x2E, 0x33, 0x45), Color.FromRgb(0x10, 0x12, 0x1A), 45)),
            ("Light desktop", new LinearGradientBrush(Color.FromRgb(0xE8, 0xEE, 0xF8), Color.FromRgb(0xB9, 0xC8, 0xE0), 45)),
            ("Colourful", new LinearGradientBrush(Color.FromRgb(0x5B, 0x21, 0xB6), Color.FromRgb(0xF5, 0x9E, 0x0B), 30)),
            ("Checkerboard", ColorField.Checkerboard()),
        };
        foreach (var (label, brush) in backdrops) backdrop.Items.Add(new ComboBoxItem { Content = label, Tag = brush });
        backdrop.SelectionChanged += (_, _) => { if (backdrop.SelectedItem is ComboBoxItem { Tag: Brush b }) backdropHost.Background = b; };
        backdrop.SelectedIndex = 0;

        var use = new Button { Content = "Use this skin", Margin = new Thickness(0, 0, 8, 0) };
        use.SetResourceReference(StyleProperty, "S.AccentButton");
        use.Click += (_, _) => { Save(); _host.ApplySkin(_info.Name); SetStatus($"{_info.Name} is now the active skin", ok: true); };
        var folder = new Button { Content = "Open folder", Margin = new Thickness(0, 0, 8, 0) };
        folder.Click += (_, _) => _host.OpenFolder(_info.Folder);
        var done = new Button { Content = "Done", MinWidth = 80 };
        done.Click += (_, _) => Close();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(mode);
        actions.Children.Add(backdrop);
        actions.Children.Add(use);
        actions.Children.Add(folder);
        actions.Children.Add(done);

        var header = new DockPanel { Margin = new Thickness(24, 18, 24, 14) };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(title);
        root.Children.Add(header);

        // Preview
        backdropHost.Child = new Viewbox { StretchDirection = StretchDirection.DownOnly, Child = _preview, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(backdropHost, 1);
        root.Children.Add(backdropHost);

        // Properties in two columns
        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        left.Children.Add(FrameGroup());
        left.Children.Add(InfoGroup());
        right.Children.Add(InputGroup());
        right.Children.Add(ResultsGroup());
        right.Children.Add(ChipsMenuGroup());
        var columns = new Grid { Margin = new Thickness(24, 16, 24, 24) };
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.Children.Add(left);
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = columns };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        return root;
    }

    private FrameworkElement FrameGroup()
    {
        var s = _skin;
        return Group("Frame",
            Row("Width", Slider(() => s.Width, v => s.Width = v, 360, 1400, 10, "0 px")),
            Row("Corner radius", Slider(() => s.CornerRadius, v => s.CornerRadius = v, 0, 32, 1, "0 px")),
            Row("Background", BackgroundEditor()),
            Row("Background image", ImageEditor()),
            Row("Border colour", ColorRow(() => s.BorderColor, v => s.BorderColor = v, allowAuto: false)),
            Row("Border thickness", Slider(() => s.BorderThickness, v => s.BorderThickness = v, 0, 4, 0.5, "0.# px")),
            Row("Accent colour", ColorRow(() => s.Accent, v => s.Accent = v, allowAuto: false)),
            Row("Accent strip", Slider(() => s.AccentWidth, v => s.AccentWidth = v, 0, 24, 1, "0 px", zeroLabel: "Off")),
            Row("Shadow", Toggle(() => s.Shadow, v => s.Shadow = v)),
            Row("Shadow colour", ColorRow(() => s.ShadowColor, v => s.ShadowColor = v, allowAuto: false)),
            Row("Shadow strength", Slider(() => s.ShadowOpacity * 100, v => s.ShadowOpacity = v / 100, 0, 100, 5, "0'%'")),
            Row("Shadow size", Slider(() => s.ShadowBlur, v => s.ShadowBlur = v, 0, 64, 2, "0 px")));
    }

    private FrameworkElement InfoGroup()
    {
        var s = _skin;
        return Group("About this skin",
            Row("Author", Text(() => s.Author, v => s.Author = v)),
            Row("Description", Text(() => s.Description, v => s.Description = v)));
    }

    private FrameworkElement InputGroup()
    {
        var s = _skin;
        return Group("Input bar",
            Row("Height", Slider(() => s.InputHeight, v => s.InputHeight = v, 32, 160, 2, "0 px")),
            Row("Side padding", Slider(() => s.PaddingX, v => s.PaddingX = v, 0, 80, 1, "0 px")),
            Row("Font", Font(() => s.InputFont, v => s.InputFont = v)),
            Row("Font size", Slider(() => s.InputFontSize, v => s.InputFontSize = v, 10, 56, 1, "0 px")),
            Row("Font weight", Weight(() => s.InputFontWeight, v => s.InputFontWeight = v)),
            Row("Text colour", ColorRow(() => s.InputColor, v => s.InputColor = v, allowAuto: false)),
            Row("Caret colour", ColorRow(() => s.CaretColor, v => s.CaretColor = v, allowAuto: true)),
            Row("Placeholder", Text(() => s.Placeholder, v => s.Placeholder = v)),
            Row("Placeholder colour", ColorRow(() => s.PlaceholderColor, v => s.PlaceholderColor = v, allowAuto: false)));
    }

    private FrameworkElement ResultsGroup()
    {
        var s = _skin;
        return Group("Results",
            Row("Background", ColorRow(() => s.ResultsBackground, v => s.ResultsBackground = v, allowAuto: false)),
            Row("Separator", ColorRow(() => s.SeparatorColor, v => s.SeparatorColor = v, allowAuto: false)),
            Row("Font", Font(() => s.ResultFont, v => s.ResultFont = v)),
            Row("Font size", Slider(() => s.ResultFontSize, v => s.ResultFontSize = v, 9, 28, 0.5, "0.# px")),
            Row("Text colour", ColorRow(() => s.ResultColor, v => s.ResultColor = v, allowAuto: false)),
            Row("Path size", Slider(() => s.DescriptionFontSize, v => s.DescriptionFontSize = v, 8, 20, 0.5, "0.# px")),
            Row("Path colour", ColorRow(() => s.DescriptionColor, v => s.DescriptionColor = v, allowAuto: false)),
            Row("Selected background", ColorRow(() => s.SelectedBackground, v => s.SelectedBackground = v, allowAuto: true)),
            Row("Selected text", ColorRow(() => s.SelectedColor, v => s.SelectedColor = v, allowAuto: false)),
            Row("Selected path", ColorRow(() => s.SelectedDescriptionColor, v => s.SelectedDescriptionColor = v, allowAuto: false)),
            Row("Selection corners", Slider(() => s.SelectionCornerRadius, v => s.SelectionCornerRadius = v, 0, 20, 1, "0 px")),
            Row("Row height", Slider(() => s.RowHeight, v => s.RowHeight = v, 28, 80, 1, "0 px")),
            Row("Icon size", Slider(() => s.IconSize, v => s.IconSize = v, 16, 64, 2, "0 px")),
            Row("Icon glyph colour", ColorRow(() => s.GlyphColor, v => s.GlyphColor = v, allowAuto: true)),
            Row("Shortcut hint colour", ColorRow(() => s.ShortcutColor, v => s.ShortcutColor = v, allowAuto: false)));
    }

    private FrameworkElement ChipsMenuGroup()
    {
        var s = _skin;
        return Group("Chips and menu",
            Row("Chip background", ColorRow(() => s.ChipBackground, v => s.ChipBackground = v, allowAuto: true)),
            Row("Chip text", ColorRow(() => s.ChipColor, v => s.ChipColor = v, allowAuto: false)),
            Row("Menu background", ColorRow(() => s.MenuBackground, v => s.MenuBackground = v, allowAuto: true)),
            Row("Menu border", ColorRow(() => s.MenuBorderColor, v => s.MenuBorderColor = v, allowAuto: true)),
            Row("Menu text", ColorRow(() => s.MenuColor, v => s.MenuColor = v, allowAuto: true)));
    }

    // ================================================================= editors

    private FrameworkElement BackgroundEditor()
    {
        var s = _skin;
        var stops = s.Background.StartsWith("linear:", StringComparison.OrdinalIgnoreCase)
            ? s.Background[7..].Split(',', StringSplitOptions.TrimEntries)
            : [s.Background];
        var top = new ColorField(stops[0], false, _palette);
        var bottom = new ColorField(stops.Length > 1 ? stops[1] : stops[0], false, _palette) { Margin = new Thickness(0, 6, 0, 0) };
        var gradient = new CheckBox { Content = "Gradient", IsChecked = stops.Length > 1, Margin = new Thickness(0, 6, 0, 0) };
        gradient.SetResourceReference(StyleProperty, "S.Toggle");
        void Update()
        {
            bottom.Visibility = gradient.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            s.Background = gradient.IsChecked == true ? $"linear:{top.Value},{bottom.Value}" : top.Value;
            Changed();
        }
        top.Changed += _ => Update();
        bottom.Changed += _ => Update();
        gradient.Checked += (_, _) => Update();
        gradient.Unchecked += (_, _) => Update();
        bottom.Visibility = gradient.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        var panel = new StackPanel();
        panel.Children.Add(top);
        panel.Children.Add(bottom);
        panel.Children.Add(gradient);
        return panel;
    }

    private FrameworkElement ImageEditor()
    {
        var s = _skin;
        var name = new TextBlock { Text = string.IsNullOrEmpty(s.BackgroundImage) ? "None" : s.BackgroundImage, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("S.SubText"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
        var choose = new Button { Content = "Choose…", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        var clear = new Button { Content = "", ToolTip = "Remove image", Margin = new Thickness(2, 0, 0, 0) };
        clear.SetResourceReference(StyleProperty, "S.IconButton");
        clear.FontSize = 9;
        var area = new ComboBox { MinWidth = 0, Width = 190, Margin = new Thickness(0, 6, 0, 0) };
        area.Items.Add(new ComboBoxItem { Content = "Fill the whole launcher", Tag = "frame" });
        area.Items.Add(new ComboBoxItem { Content = "Input bar only", Tag = "input" });
        area.SelectedIndex = string.Equals(s.BackgroundImageArea, "input", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        area.SelectionChanged += (_, _) => { if (area.SelectedItem is ComboBoxItem { Tag: string t }) { s.BackgroundImageArea = t; Changed(); } };

        choose.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Title = "Choose a background image", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (dlg.ShowDialog(this) != true) return;
            // Copy into the skin folder so the skin stays self-contained (and exportable).
            var fileName = Path.GetFileName(dlg.FileName);
            var target = Path.Combine(_info.Folder, fileName);
            if (!string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(dlg.FileName, target, overwrite: true);
            s.BackgroundImage = fileName;
            name.Text = fileName;
            Changed();
        };
        clear.Click += (_, _) => { s.BackgroundImage = null; name.Text = "None"; Changed(); };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(name);
        line.Children.Add(choose);
        line.Children.Add(clear);
        var panel = new StackPanel();
        panel.Children.Add(line);
        panel.Children.Add(area);
        return panel;
    }

    private FrameworkElement Slider(Func<double> get, Action<double> set, double min, double max, double step, string format, string? zeroLabel = null)
    {
        string Label(double v) => v == 0 && zeroLabel is not null ? zeroLabel : v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        var value = new TextBlock { Width = 58, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("S.SubText"), Margin = new Thickness(10, 0, 0, 0), Text = Label(get()) };
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(get(), min, max), SmallChange = step, LargeChange = step * 4, TickFrequency = step, IsSnapToTickEnabled = true, Width = double.NaN, MinWidth = 140 };
        slider.ValueChanged += (_, _) =>
        {
            set(slider.Value);
            value.Text = Label(slider.Value);
            Changed();
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(slider);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private ColorField ColorRow(Func<string> get, Action<string> set, bool allowAuto)
    {
        var field = new ColorField(get(), allowAuto, _palette);
        field.Changed += v => { set(v); Changed(); };
        return field;
    }

    private TextBox Text(Func<string> get, Action<string> set)
    {
        var box = new TextBox { Text = get() };
        box.TextChanged += (_, _) => { set(box.Text); Changed(); };
        return box;
    }

    private CheckBox Toggle(Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { IsChecked = get(), Content = get() ? "On" : "Off" };
        cb.SetResourceReference(StyleProperty, "S.Toggle");
        cb.Checked += (_, _) => { set(true); cb.Content = "On"; Changed(); };
        cb.Unchecked += (_, _) => { set(false); cb.Content = "Off"; Changed(); };
        return cb;
    }

    private ComboBox Font(Func<string> get, Action<string> set)
    {
        var combo = new ComboBox { MinWidth = 0 };
        var current = get();
        var names = SystemFonts.Value.ToList();
        if (!names.Contains(current, StringComparer.OrdinalIgnoreCase)) names.Insert(0, current); // e.g. a fallback list
        foreach (var n in names)
        {
            var item = new ComboBoxItem { Content = n };
            combo.Items.Add(item);
            if (n.Equals(current, StringComparison.OrdinalIgnoreCase)) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Content: string n }) { set(n); Changed(); }
        };
        return combo;
    }

    private ComboBox Weight(Func<string> get, Action<string> set)
    {
        var combo = new ComboBox { MinWidth = 0 };
        foreach (var w in Weights)
        {
            var item = new ComboBoxItem { Content = w };
            combo.Items.Add(item);
            if (w.Equals(get(), StringComparison.OrdinalIgnoreCase)) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Content: string w }) { set(w); Changed(); }
        };
        return combo;
    }

    private FrameworkElement Group(string title, params FrameworkElement[] rows)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var r in rows) panel.Children.Add(r);
        var card = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(18, 14, 18, 10),
            Margin = new Thickness(0, 0, 0, 14),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
        card.SetResourceReference(Border.BackgroundProperty, "S.Card");
        return card;
    }

    private FrameworkElement Row(string label, FrameworkElement editor)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = label, Foreground = Brush("S.SubText"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.HorizontalAlignment = editor is ComboBox or TextBox or Grid ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    // ================================================================= saving

    private void Changed()
    {
        _preview.Render(_skin, _info.Folder);
        _saveTimer.Stop();
        _saveTimer.Start();
        SetStatus("Editing…", ok: false);
    }

    private void Save()
    {
        _saveTimer.Stop();
        try
        {
            SkinLibrary.Write(_info.Folder, _skin);
            SetStatus("Saved", ok: true);
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't save: " + ex.Message, ok: false, error: true);
        }
    }

    private void SetStatus(string text, bool ok, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = Brush(error ? "S.Danger" : ok ? "S.Ok" : "S.Faint");
    }

    private void RefreshPalette()
    {
        var s = _skin;
        _palette = new[] { s.Accent, s.InputColor, s.ResultColor, s.SelectedBackground, s.ResultsBackground, s.ChipBackground }
            .Where(c => ColorField.TryParse(c, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private Brush Brush(string key) => (Brush)FindResource(key);
}
