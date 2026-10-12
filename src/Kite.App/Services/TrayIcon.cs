using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Kite.App.Services;

public enum NotifyKind { Info = 1, Warning = 2, Error = 3 }

/// <summary>
/// Notification-area icon via Shell_NotifyIcon, with the native Windows menu (TrackPopupMenuEx).
/// The native menu is what the shell expects from a tray icon: Windows places it at the icon,
/// keeps it open when the icon lives in the hidden-icons flyout, closes it on a click elsewhere,
/// and draws it dark when Windows is in dark mode. (Replaces WinForms' NotifyIcon so Windows Forms
/// never has to load.)
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x4B; // WM_APP + 'K'
    private const int IconId = 1;
    private const int WM_NULL = 0x0000, WM_LBUTTONUP = 0x0202, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint MF_STRING = 0x0, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800, MF_DEFAULT = 0x1000;
    private const uint TPM_RIGHTALIGN = 0x8, TPM_BOTTOMALIGN = 0x20, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;

    private readonly TrayActions _actions;
    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private readonly IntPtr _icon;
    private string _tooltip = "Kite Launcher";
    private bool _visible;
    private bool _menuOpen;

    public TrayIcon(TrayActions actions)
    {
        _actions = actions;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // A hidden top-level window rather than a message-only one: the menu needs a window that can be
        // foreground to close properly, and only top-level windows hear "TaskbarCreated" when Explorer restarts.
        _window = new HwndSource(new HwndSourceParameters("KiteTrayHost")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = 0x80,                // WS_EX_TOOLWINDOW: not in Alt+Tab
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);
        UseDarkMenusWhenWindowsIsDark(_window.Handle);
        _icon = LoadAppIcon();
        Visible = true;
    }

    /// <summary>Raised when the user clicks the last notification ("balloon"); see <see cref="Notify"/>.</summary>
    public event Action? NotificationClicked;

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
                // With NOTIFYICON_VERSION_4 a right-click arrives as WM_RBUTTONUP *and* WM_CONTEXTMENU (which
                // also covers Shift+F10 / the menu key). Answering only WM_CONTEXTMENU opens the menu once.
                case WM_CONTEXTMENU:
                    var anchor = wParam.ToInt64();
                    ShowMenu((short)(anchor & 0xFFFF), (short)((anchor >> 16) & 0xFFFF));
                    break;
                case NIN_BALLOONUSERCLICK:
                    NotificationClicked?.Invoke();
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

    private enum Command { Show = 1, Settings, Rescan, OpenData, OpenSkins, Exit, SkinBase = 100 }

    /// <summary>Opens the menu at the point the shell gives for the icon (in device pixels) and runs the chosen command.</summary>
    private void ShowMenu(int x, int y)
    {
        if (_menuOpen) return;
        var skins = _actions.ListSkins().ToList();
        var current = _actions.CurrentSkin();

        var skinMenu = CreatePopupMenu();
        for (var i = 0; i < skins.Count; i++)
            AppendMenu(skinMenu, MF_STRING | (skins[i].Equals(current, StringComparison.OrdinalIgnoreCase) ? MF_CHECKED : 0), (nuint)((int)Command.SkinBase + i), skins[i]);
        AppendMenu(skinMenu, MF_SEPARATOR, 0, null);
        AppendMenu(skinMenu, MF_STRING, (nuint)Command.OpenSkins, "Open skins folder");

        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING | MF_DEFAULT, (nuint)Command.Show, "Show Kite");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, (nuint)Command.Settings, "Settings");
        AppendMenu(menu, MF_POPUP, (nuint)skinMenu, "Skin");
        AppendMenu(menu, MF_STRING, (nuint)Command.Rescan, "Rebuild catalog");
        AppendMenu(menu, MF_STRING, (nuint)Command.OpenData, "Open data folder");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, (nuint)Command.Exit, "Exit");

        uint chosen;
        _menuOpen = true;
        try
        {
            // The documented tray-menu dance: make our window foreground so a click elsewhere closes the
            // menu, open it (modal until it closes), then post a dummy message so a second click works.
            SetForegroundWindow(_window.Handle);
            var align = GetSystemMetrics(40 /* SM_MENUDROPALIGNMENT */) != 0 ? TPM_RIGHTALIGN : 0;
            chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY | TPM_BOTTOMALIGN | align, x, y, _window.Handle, IntPtr.Zero);
            PostMessage(_window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            _menuOpen = false;
            DestroyMenu(menu); // also destroys the skin submenu
        }

        switch ((Command)chosen)
        {
            case Command.Show: _actions.Show(); break;
            case Command.Settings: _actions.OpenSettings(); break;
            case Command.Rescan: _actions.Rescan(); break;
            case Command.OpenData: _actions.OpenDataFolder(); break;
            case Command.OpenSkins: _actions.OpenSkinsFolder(); break;
            case Command.Exit: _actions.Exit(); break;
            default:
                var index = (int)chosen - (int)Command.SkinBase;
                if (index >= 0 && index < skins.Count) _actions.SelectSkin(skins[index]);
                break;
        }
    }

    /// <summary>
    /// Native menus are light unless the app opts in to dark mode. These uxtheme exports (by ordinal, since
    /// Windows 10 1903) make them follow the Windows app theme; on older builds this quietly does nothing.
    /// </summary>
    private static void UseDarkMenusWhenWindowsIsDark(IntPtr hwnd)
    {
        if (Environment.OSVersion.Version.Build < 18362) return;
        try
        {
            SetPreferredAppMode(1 /* AllowDark */);
            AllowDarkModeForWindow(hwnd, true);
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Logger.Log($"Dark tray menu unavailable: {ex.Message}");
        }
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
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#133")] private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
}

public sealed class TrayActions
{
    public required Action Show { get; init; }
    public required Action OpenSettings { get; init; }
    public required Action Rescan { get; init; }
    public required Action OpenDataFolder { get; init; }
    public required Action OpenSkinsFolder { get; init; }
    public required Func<IEnumerable<string>> ListSkins { get; init; }
    public required Func<string> CurrentSkin { get; init; }
    public required Action<string> SelectSkin { get; init; }
    public required Action Exit { get; init; }
}
