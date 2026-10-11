using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Kite.App.Services;

/// <summary>System-wide hotkey via RegisterHotKey, delivered through the launcher window's message loop.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4B49; // "KI"
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private bool _registered;

    public HotkeyService(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    /// <summary>Registers e.g. "Alt+Space". Returns false (with a reason) if the text is invalid or another app owns the combination.</summary>
    public bool Register(string text, out string? error)
    {
        Unregister();
        if (!TryParse(text, out var modifiers, out var key))
        {
            error = $"'{text}' is not a valid hotkey";
            return false;
        }
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers | MOD_NOREPEAT, vk);
        error = _registered ? null : $"{text} {TakenSuffix}";
        return _registered;
    }

    private const string TakenSuffix = "is already used by another program";

    /// <summary>True when registration failed because another program owns the combination (worth retrying).</summary>
    public static bool IsTakenError(string? error) => error?.EndsWith(TakenSuffix, StringComparison.Ordinal) == true;

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    public static bool TryParse(string text, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": modifiers |= MOD_ALT; continue;
                case "ctrl" or "control": modifiers |= MOD_CONTROL; continue;
                case "shift": modifiers |= MOD_SHIFT; continue;
                case "win" or "windows" or "meta": modifiers |= MOD_WIN; continue;
            }
            key = ParseKey(raw);
            if (key == Key.None) return false;
        }
        return key != Key.None;
    }

    private static Key ParseKey(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z') return Key.A + (c - 'A');
            if (c is >= '0' and <= '9') return Key.D0 + (c - '0');
            if (c == '`') return Key.Oem3;
        }
        return Enum.TryParse<Key>(name, ignoreCase: true, out var k) ? k : Key.None;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
