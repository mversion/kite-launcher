using System.Runtime.InteropServices;

namespace Kite.App.Services;

/// <summary>A rectangle in device pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Monitor and cursor queries via user32 (replaces WinForms' Screen/Cursor).</summary>
public static class Monitors
{
    private const uint MONITOR_DEFAULTTONEAREST = 2, MONITOR_DEFAULTTOPRIMARY = 1;

    public static PixelRect WorkAreaFromWindow(IntPtr hwnd) => WorkArea(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
    public static PixelRect WorkAreaFromPoint(int x, int y) => WorkArea(MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST));
    public static PixelRect PrimaryWorkArea() => WorkArea(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY));

    public static PixelRect WorkAreaFromRect(PixelRect r)
    {
        var rect = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        return WorkArea(MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST));
    }

    public static (int X, int Y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    private static PixelRect WorkArea(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return new PixelRect(0, 0, 1920, 1080);
        return new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
}

/// <summary>
/// Gives unused memory back to Windows: an aggressive, compacting GC (returns freed heap to the
/// OS) followed by trimming the working set. Pages needed again fault back in quickly.
/// </summary>
public static class MemoryTrimmer
{
    /// <summary>Light: return freed .NET heap to Windows but keep code/UI pages resident (next show stays instant).</summary>
    public static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Full: also trim the working set - Kite drops to a few MB while idle in the tray.</summary>
    public static void Trim(string reason)
    {
        var before = Snapshot();
        Collect();
        SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);
        var after = Snapshot();
        Logger.Log($"Memory trimmed ({reason}): working set {before.WorkingMb} → {after.WorkingMb} MB, private {before.PrivateMb} → {after.PrivateMb} MB, GC heap {after.HeapMb} MB");
    }

    public static (long WorkingMb, long PrivateMb, long HeapMb) Snapshot()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return (p.WorkingSet64 >> 20, p.PrivateMemorySize64 >> 20, GC.GetTotalMemory(false) >> 20);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nint min, nint max, uint flags);
}
