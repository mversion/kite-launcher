using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Kite.Setup;

public enum SetupMode { Install, Uninstall }

/// <summary>One-window installer/uninstaller in Kite's settings style.</summary>
public sealed class SetupWindow : Window
{
    private readonly SetupMode _mode;
    private readonly string[] _args;
    private readonly Grid _page = new();
    private readonly StackPanel _footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    // install state
    private bool _portable;
    private TextBox _folder = null!;
    private CheckBox _desktop = null!, _autostart = null!, _launch = null!;
    private Border _installCard = null!, _portableCard = null!;
    private FrameworkElement _installedOnly = null!;
    private TextBlock _folderNote = null!;
    private string _installFolder = Installer.DefaultInstallFolder;
    private string _portableFolder = Installer.DefaultPortableFolder;

    public SetupWindow(SetupMode mode, string[] args)
    {
        _mode = mode;
        _args = args;
        Title = mode == SetupMode.Install ? $"Kite {Installer.Version} setup" : "Uninstall Kite";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(BackgroundProperty, "S.Bg");
        SourceInitialized += (_, _) =>
        {
            var on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
        };

        var root = new DockPanel { Margin = new Thickness(32, 26, 32, 24) };
        var header = Header();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        _footer.Margin = new Thickness(0, 24, 0, 0);
        DockPanel.SetDock(_footer, Dock.Bottom);
        root.Children.Add(_footer);
        root.Children.Add(_page);
        Content = root;

        if (mode == SetupMode.Install) ShowInstallPage();
        else ShowUninstallPage();
    }

    // ================================================================= pages

    private FrameworkElement Header()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
        var logo = new Image { Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0) };
        try { logo.Source = BitmapFrame.Create(new Uri("pack://application:,,,/kite.ico")); } catch { }
        panel.Children.Add(logo);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = "Kite", FontSize = 26, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "v" + Installer.Version, FontSize = 12, Foreground = Res("S.Faint"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 5) });
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = "A fast, skinnable keystroke launcher for Windows", Foreground = Res("S.SubText"), FontSize = 13 });
        panel.Children.Add(text);
        return panel;
    }

    private void ShowInstallPage()
    {
        if (!Installer.HasPayload)
        {
            ShowMessage("This setup file is incomplete", "It was built without Kite inside it. Rebuild it with publish.ps1.", error: true);
            return;
        }

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "How would you like to use Kite?", FontSize = 15, Margin = new Thickness(0, 0, 0, 12) });

        var cards = new UniformGrid2();
        _installCard = ChoiceCard("", "Install on this PC",
            "Start menu shortcut, listed in Settings › Apps for easy removal, optional start with Windows. No administrator rights needed.");
        _portableCard = ChoiceCard("", "Portable",
            "Run from any folder, USB stick or synced drive. Settings, history and skins stay next to Kite.exe; nothing is written to the registry.");
        _installCard.MouseLeftButtonUp += (_, _) => SelectMode(portable: false);
        _portableCard.MouseLeftButtonUp += (_, _) => SelectMode(portable: true);
        cards.Add(_installCard, _portableCard);
        stack.Children.Add(cards.Panel);

        // Folder
        var folderRow = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition());
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(new TextBlock { Text = "Folder", Foreground = Res("S.SubText"), VerticalAlignment = VerticalAlignment.Center });
        _folder = new TextBox { Text = _installFolder };
        _folder.TextChanged += (_, _) =>
        {
            if (_portable) _portableFolder = _folder.Text; else _installFolder = _folder.Text;
            UpdateFolderNote();
        };
        Grid.SetColumn(_folder, 1);
        folderRow.Children.Add(_folder);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose where to put Kite" };
            if (Directory.Exists(_folder.Text)) dlg.InitialDirectory = _folder.Text;
            if (dlg.ShowDialog(this) == true)
                _folder.Text = string.Equals(Path.GetFileName(dlg.FolderName), "Kite", StringComparison.OrdinalIgnoreCase) ? dlg.FolderName : Path.Combine(dlg.FolderName, "Kite");
        };
        Grid.SetColumn(browse, 2);
        folderRow.Children.Add(browse);
        stack.Children.Add(folderRow);
        _folderNote = new TextBlock { FontSize = 11.5, Foreground = Res("S.Faint"), Margin = new Thickness(90, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(_folderNote);

        // Options
        var options = new StackPanel { Margin = new Thickness(90, 14, 0, 0) };
        _desktop = Toggle("Desktop shortcut", false);
        _autostart = Toggle("Start Kite when Windows starts", true);
        var installedOnly = new StackPanel();
        installedOnly.Children.Add(_desktop);
        installedOnly.Children.Add(_autostart);
        _installedOnly = installedOnly;
        options.Children.Add(installedOnly);
        _launch = Toggle("Open Kite when setup finishes", true);
        options.Children.Add(_launch);
        stack.Children.Add(options);

        stack.Children.Add(new TextBlock
        {
            Text = "Your Launchy setup (folders, hotkey, web searches, learned shortcuts) is imported automatically the first time Kite starts. Quit Launchy first if you want Kite to use the same hotkey.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res("S.SubText"), Margin = new Thickness(0, 20, 0, 0),
        });

        SetPage(stack);
        SetFooter(("Cancel", false, Close), ("Install", true, StartInstall));
        SelectMode(portable: false);
    }

    private void SelectMode(bool portable)
    {
        _portable = portable;
        _folder.Text = portable ? _portableFolder : _installFolder;
        Highlight(_installCard, !portable);
        Highlight(_portableCard, portable);
        _installedOnly.Visibility = portable ? Visibility.Collapsed : Visibility.Visible;
        UpdateFolderNote();
    }

    private void UpdateFolderNote()
    {
        var folder = _folder.Text.Trim();
        var existing = File.Exists(Path.Combine(folder, "Kite.exe"));
        _folderNote.Text = existing
            ? "Kite is already here - it will be updated. Your settings, shortcuts and skins are kept."
            : _portable
                ? "Everything Kite saves will live in this folder."
                : $"Settings are kept in {Installer.DataFolder}.";
    }

    private async void StartInstall()
    {
        var folder = _folder.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathRooted(folder))
        {
            _folderNote.Text = "Please choose a full folder path.";
            _folderNote.Foreground = Res("S.Danger");
            return;
        }
        var options = new InstallOptions(_portable, folder, _desktop.IsChecked == true, !_portable && _autostart.IsChecked == true, _launch.IsChecked == true);
        var (bar, status) = ShowProgress(_portable ? "Setting up portable Kite…" : "Installing Kite…");
        var progress = new Progress<(double Fraction, string Status)>(p => { bar.Width = 600 * p.Fraction; status.Text = p.Status; });
        try
        {
            await Task.Run(() => Installer.Install(options, progress));
            ShowMessage(
                _portable ? "Portable Kite is ready" : "Kite is installed",
                (_portable ? $"Kite is in {folder}. Run Kite.exe from there - copy the folder anywhere you like." : $"Kite is in {folder} and in your Start menu.")
                + "\n\nPress your hotkey (Alt+Space by default) to open it, or click the kite icon in the tray. Right-click the launcher or press Ctrl+, for settings.",
                error: false);
        }
        catch (Exception ex)
        {
            ShowMessage("Setup couldn't finish", ex.Message, error: true);
        }
    }

    private void ShowUninstallPage()
    {
        var folder = Installer.FolderFromArgs(_args);
        if (folder is null)
        {
            ShowMessage("Kite isn't installed", "There's no installed copy of Kite to remove. (Portable copies are removed by deleting their folder.)", error: false);
            return;
        }

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Remove Kite from this PC?", FontSize = 15, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock { Text = $"Kite will be closed and removed from {folder}, along with its shortcuts and startup entry.", TextWrapping = TextWrapping.Wrap, Foreground = Res("S.SubText") });
        var removeData = Toggle($"Also remove my settings, history and skins ({Installer.DataFolder}) - they go to the Recycle Bin", false);
        removeData.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(removeData);
        SetPage(stack);

        SetFooter(("Cancel", false, Close), ("Uninstall", true, async () =>
        {
            var (bar, status) = ShowProgress("Removing Kite…");
            var progress = new Progress<(double Fraction, string Status)>(p => { bar.Width = 600 * p.Fraction; status.Text = p.Status; });
            try
            {
                var wipe = removeData.IsChecked == true;
                await Task.Run(() => Installer.Uninstall(folder, wipe, progress));
                ShowMessage("Kite has been removed", wipe ? "Kite and your settings are gone (settings are in the Recycle Bin)." : $"Your settings are still in {Installer.DataFolder} in case you reinstall.", error: false);
            }
            catch (Exception ex)
            {
                ShowMessage("Uninstall couldn't finish", ex.Message, error: true);
            }
            finally { Installer.ScheduleSelfDelete(); }
        }));
    }

    private (Border Bar, TextBlock Status) ShowProgress(string title)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 20, 0, 20) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 16, Margin = new Thickness(0, 0, 0, 14) });
        var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        bar.SetResourceReference(Border.BackgroundProperty, "Kite.Accent");
        stack.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), Width = 600, HorizontalAlignment = HorizontalAlignment.Left, Child = bar });
        var status = new TextBlock { Foreground = Res("S.Faint"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        stack.Children.Add(status);
        SetPage(stack);
        SetFooter();
        return (bar, status);
    }

    private void ShowMessage(string title, string body, bool error)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var glyph = new TextBlock { Text = error ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 26, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, error ? "S.Danger" : "S.Ok");
        head.Children.Add(glyph);
        head.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Foreground = Res("S.SubText"), FontSize = 13, LineHeight = 20 });
        SetPage(stack);
        SetFooter(("Close", true, Close));
    }

    // ================================================================= building blocks

    private void SetPage(UIElement content)
    {
        _page.Children.Clear();
        _page.Children.Add(content);
    }

    private void SetFooter(params (string Text, bool Primary, Action Click)[] buttons)
    {
        _footer.Children.Clear();
        foreach (var (text, primary, click) in buttons)
        {
            var b = new Button { Content = text, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary };
            if (primary) b.SetResourceReference(StyleProperty, "S.AccentButton");
            b.Click += (_, _) => click();
            _footer.Children.Add(b);
        }
    }

    private Border ChoiceCard(string glyph, string title, string description)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 22, Margin = new Thickness(0, 0, 0, 10) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Kite.Accent");
        var panel = new StackPanel();
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Res("S.SubText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), LineHeight = 18 });
        var card = new Border
        {
            Padding = new Thickness(18, 16, 18, 16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand,
            Child = panel,
        };
        card.SetResourceReference(Border.BackgroundProperty, "S.Card");
        return card;
    }

    private void Highlight(Border card, bool selected)
    {
        if (selected) card.SetResourceReference(Border.BorderBrushProperty, "Kite.Accent");
        else card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
    }

    private CheckBox Toggle(string label, bool on)
    {
        var cb = new CheckBox { Content = label, IsChecked = on, Margin = new Thickness(0, 0, 0, 10) };
        cb.SetResourceReference(StyleProperty, "S.Toggle");
        return cb;
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    /// <summary>Two equal columns with a gap.</summary>
    private sealed class UniformGrid2
    {
        public Grid Panel { get; } = new();

        public void Add(UIElement left, UIElement right)
        {
            Panel.ColumnDefinitions.Add(new ColumnDefinition());
            Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            Panel.ColumnDefinitions.Add(new ColumnDefinition());
            Panel.Children.Add(left);
            Grid.SetColumn(right, 2);
            Panel.Children.Add(right);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
