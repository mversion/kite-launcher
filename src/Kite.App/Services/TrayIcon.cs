using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Kite.App.Services;

public enum NotifyKind { Info = 1, Warning = 2, Error = 3 }

/// <summary>
/// Notification-area icon via Shell_NotifyIcon on a message-only window, with a skinned WPF
/// menu. (Replaces WinForms' NotifyIcon so Windows Forms never has to load.)
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x4B; // WM_APP + 'K'
    private const int IconId = 1;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;

    private readonly TrayActions _actions;
    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private readonly IntPtr _icon;
    private string _tooltip = "Kite";
    private bool _visible;
    private ContextMenu? _menu;

    public TrayIcon(TrayActions actions)
    {
        _actions = actions;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window = new HwndSource(new HwndSourceParameters("KiteTrayHost") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(WndProc);
        _icon = LoadAppIcon();
        Visible = true;
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (value == _visible) return;
            _visible = value;
            if (value) Add();
            else Remove();
        }
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 120 ? text[..120] : text;
        if (!_visible) return;
        var data = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Shows a Windows notification ("balloon") from the tray icon.</summary>
    public void Notify(string title, string text, NotifyKind kind = NotifyKind.Info)
    {
        if (!_visible) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = (int)kind;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_ADD, ref data);
        data.uTimeoutOrVersion = 4; // NOTIFYICON_VERSION_4
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private void Remove()
    {
        var data = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    private NOTIFYICONDATAW Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT:
                    _actions.Show();
                    break;
                case WM_CONTEXTMENU or WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated && _visible)
        {
            Add(); // Explorer restarted
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        _menu ??= BuildMenu();
        if (_menu.IsOpen) { _menu.IsOpen = false; return; }
        RefreshSkins();
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
        // Let the popup take focus so it closes when you click elsewhere.
        if (PresentationSource.FromVisual(_menu) is HwndSource src) SetForegroundWindow(src.Handle);
    }

    private MenuItem? _skins;

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(FrameworkElement.StyleProperty, "KiteContextMenu");

        MenuItem Item(string header, string glyph, Action run, string? gesture = null)
        {
            var item = new MenuItem { Header = header, Tag = glyph, InputGestureText = gesture ?? "" };
            item.Click += (_, _) => run();
            return item;
        }

        menu.Items.Add(Item("Show Kite", "", _actions.Show));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings", "", _actions.OpenSettings));
        _skins = new MenuItem { Header = "Skin", Tag = "" };
        menu.Items.Add(_skins);
        menu.Items.Add(Item("Rebuild catalog", "", _actions.Rescan));
        menu.Items.Add(Item("Import Launchy settings…", "", _actions.ImportLaunchy));
        menu.Items.Add(Item("Open data folder", "", _actions.OpenDataFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", "", _actions.Exit));
        return menu;
    }

    private void RefreshSkins()
    {
        if (_skins is null) return;
        _skins.Items.Clear();
        var current = _actions.CurrentSkin();
        foreach (var name in _actions.ListSkins())
        {
            var item = new MenuItem { Header = name, IsChecked = name.Equals(current, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => _actions.SelectSkin(name);
            _skins.Items.Add(item);
        }
        _skins.Items.Add(new Separator());
        var open = new MenuItem { Header = "Open skins folder", Tag = "" };
        open.Click += (_, _) => _actions.OpenSkinsFolder();
        _skins.Items.Add(open);
    }

    /// <summary>The exe's own icon (Assets\kite.ico) at the small-icon size for the current DPI.</summary>
    private static IntPtr LoadAppIcon()
    {
        var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem());
        var path = Environment.ProcessPath;
        if (path is not null)
        {
            var icons = new IntPtr[1];
            var ids = new int[1];
            if (PrivateExtractIcons(path, 0, size, size, icons, ids, 1, 0) > 0 && icons[0] != IntPtr.Zero)
                return icons[0];
        }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    public void Dispose()
    {
        Remove();
        _window.RemoveHook(WndProc);
        _window.Dispose();
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}

public sealed class TrayActions
{
    public required Action Show { get; init; }
    public required Action OpenSettings { get; init; }
    public required Action Rescan { get; init; }
    public required Action ImportLaunchy { get; init; }
    public required Action OpenDataFolder { get; init; }
    public required Action OpenSkinsFolder { get; init; }
    public required Func<IEnumerable<string>> ListSkins { get; init; }
    public required Func<string> CurrentSkin { get; init; }
    public required Action<string> SelectSkin { get; init; }
    public required Action Exit { get; init; }
}
