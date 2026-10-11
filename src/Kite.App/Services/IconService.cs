using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kite.App.Services;

/// <summary>
/// Loads shell icons (files, shortcuts, folders, shell:AppsFolder items) on a dedicated STA
/// thread so typing never waits on disk or COM. Results are cached and frozen.
/// </summary>
public sealed class IconService : IDisposable
{
    private readonly BlockingCollection<(string Path, TaskCompletionSource<ImageSource?> Tcs)> _queue = new();
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread _worker;
    private int _failures;

    public IconService()
    {
        _worker = new Thread(Run) { IsBackground = true, Name = "Kite icon loader" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>File search results can bring thousands of distinct paths; keep the cache bounded.</summary>
    private const int MaxCachedIcons = 2000;

    public Task<ImageSource?> GetAsync(string path)
    {
        if (_cache.Count > MaxCachedIcons) _cache.Clear();
        return GetOrLoad(path);
    }

    private Task<ImageSource?> GetOrLoad(string path) =>
        _cache.GetOrAdd(path, p =>
        {
            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((p, tcs));
            return tcs.Task;
        });

    public void Clear() => _cache.Clear();

    private void Run()
    {
        foreach (var (path, tcs) in _queue.GetConsumingEnumerable())
        {
            try { tcs.SetResult(Load(path)); }
            catch (Exception ex)
            {
                if (_failures++ < 5) Logger.Log($"Icon load failed for {path}: {ex.GetType().Name}: {ex.Message}");
                tcs.SetResult(null);
            }
        }
    }

    private static ImageSource? Load(string path)
    {
        var ext = Path.GetExtension(path);
        if ((ext.Equals(".png", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)) && File.Exists(path))
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 64;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            return null;
        try
        {
            var info = new SHFILEINFO();
            var ok = SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_PIDL | SHGFI_ICON | SHGFI_LARGEICON);
            if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    public void Dispose() => _queue.CompleteAdding();

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_PIDL = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
