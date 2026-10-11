using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kite.App.Settings;

/// <summary>Small themed prompt for a single line of text (skin names etc.).</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box;

    private InputDialog(Window owner, string title, string prompt, string initial)
    {
        Owner = owner;
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Kite;component/Settings/SettingsTheme.xaml") });
        SetResourceReference(BackgroundProperty, "S.Card");

        _box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 86 };
        ok.SetResourceReference(StyleProperty, "S.AccentButton");
        ok.Click += (_, _) => { if (_box.Text.Trim().Length > 0) DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 86, Margin = new Thickness(8, 0, 0, 0) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    /// <summary>Returns the entered text, or null if cancelled.</summary>
    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog(owner, title, prompt, initial);
        return dlg.ShowDialog() == true ? dlg._box.Text.Trim() : null;
    }
}

internal static class DarkTitleBar
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
