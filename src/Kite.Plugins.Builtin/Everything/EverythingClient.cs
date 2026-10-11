using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Kite.Plugins.Builtin.Everything;

public sealed record EverythingResult(string Name, string Folder, bool IsFolder)
{
    public string FullPath => Folder.Length == 0 ? Name : System.IO.Path.Combine(Folder, Name);
}

public enum EverythingSort : uint
{
    NameAscending = 1,
    PathAscending = 3,
    SizeDescending = 6,
    DateModifiedDescending = 14,
    RunCountDescending = 20,
}

public interface IEverythingClient
{
    /// <summary>True when the Everything service/window is running and can answer queries.</summary>
    bool IsAvailable { get; }
    Task<IReadOnlyList<EverythingResult>> SearchAsync(string search, int maxResults, EverythingSort sort, bool matchPath, CancellationToken ct);
}

/// <summary>
/// Talks to a running Everything (voidtools) over its WM_COPYDATA IPC protocol - the same one
/// the Everything SDK DLL wraps - so no extra DLL is needed. Replies arrive on a hidden
/// message-only window serviced by a dedicated thread.
/// </summary>
public sealed class EverythingClient : IEverythingClient, IDisposable
{
    private const int WM_COPYDATA = 0x004A;
    private const uint CopyDataQuery2 = 18; // EVERYTHING_IPC_COPYDATA_QUERY2W
    private static readonly string[] WindowClasses = ["EVERYTHING_TASKBAR_NOTIFICATION", "EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)"];

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly WndProc _wndProc; // keep the delegate alive while the window exists
    private IntPtr _hwnd;
    private uint _threadId;
    private int _nextId = 0x4B000000; // 'K' prefix, unlikely to clash with anything else

    public EverythingClient()
    {
        _wndProc = WindowProc;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Kite Everything IPC" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    public bool IsAvailable => FindEverythingWindow() != IntPtr.Zero;

    public async Task<IReadOnlyList<EverythingResult>> SearchAsync(string search, int maxResults, EverythingSort sort, bool matchPath, CancellationToken ct)
    {
        var everything = FindEverythingWindow();
        if (everything == IntPtr.Zero || _hwnd == IntPtr.Zero) return [];

        var id = (uint)Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var request = BuildQuery2((uint)_hwnd.ToInt64(), id, search, maxResults, sort, matchPath);
            var buffer = Marshal.AllocHGlobal(request.Length);
            try
            {
                Marshal.Copy(request, 0, buffer, request.Length);
                var cds = new COPYDATASTRUCT { dwData = (IntPtr)CopyDataQuery2, cbData = request.Length, lpData = buffer };
                // Everything acknowledges immediately and replies with WM_COPYDATA to our window.
                var ok = SendMessageTimeout(everything, WM_COPYDATA, _hwnd, ref cds, 0x0002 /* SMTO_ABORTIFHUNG */, 2000, out var accepted);
                if (ok == IntPtr.Zero || accepted == IntPtr.Zero) return [];
            }
            finally { Marshal.FreeHGlobal(buffer); }

            var reply = await tcs.Task.WaitAsync(Timeout, ct).ConfigureAwait(false);
            return ParseList2(reply);
        }
        catch (TimeoutException) { return []; }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>EVERYTHING_IPC_QUERY2 followed by the null-terminated UTF-16 search text.</summary>
    public static byte[] BuildQuery2(uint replyHwnd, uint replyId, string search, int maxResults, EverythingSort sort, bool matchPath)
    {
        const uint RequestName = 0x1, RequestPath = 0x2;
        const uint SearchMatchPath = 0x4;
        var text = Encoding.Unicode.GetBytes(search + "\0");
        var bytes = new byte[7 * 4 + text.Length];
        var header = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(0, 28));
        header[0] = replyHwnd;
        header[1] = replyId;                        // reply_copydata_message
        header[2] = matchPath ? SearchMatchPath : 0; // search_flags
        header[3] = 0;                              // offset
        header[4] = (uint)Math.Max(1, maxResults);  // max_results
        header[5] = RequestName | RequestPath;      // request_flags
        header[6] = (uint)sort;                     // sort_type
        text.CopyTo(bytes, 28);
        return bytes;
    }

    /// <summary>
    /// Parses EVERYTHING_IPC_LIST2: a header (totitems, numitems, offset, request_flags, sort_type),
    /// numitems × {flags, data_offset}, then per item the requested fields in flag order; each
    /// string is a DWORD length followed by that many UTF-16 chars and a terminating null.
    /// </summary>
    public static IReadOnlyList<EverythingResult> ParseList2(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) return [];
        var numItems = (int)Read(data, 4);
        var requestFlags = Read(data, 12);
        var results = new List<EverythingResult>(numItems);
        for (var i = 0; i < numItems; i++)
        {
            var itemOffset = 20 + i * 8;
            if (itemOffset + 8 > data.Length) break;
            var flags = Read(data, itemOffset);
            var p = (int)Read(data, itemOffset + 4);
            string name = "", folder = "";
            if ((requestFlags & 0x1) != 0) name = ReadString(data, ref p);
            if ((requestFlags & 0x2) != 0) folder = ReadString(data, ref p);
            if (name.Length > 0) results.Add(new EverythingResult(name, folder, (flags & 0x1) != 0 || (flags & 0x2) != 0));
        }
        return results;
    }

    private static uint Read(ReadOnlySpan<byte> data, int offset) => BitConverter.ToUInt32(data.Slice(offset, 4));

    private static string ReadString(ReadOnlySpan<byte> data, ref int p)
    {
        if (p < 0 || p + 4 > data.Length) return "";
        var len = (int)Read(data, p);
        p += 4;
        var bytes = len * 2;
        if (len < 0 || p + bytes > data.Length) return "";
        var s = Encoding.Unicode.GetString(data.Slice(p, bytes));
        p += bytes + 2; // skip the terminating null
        return s;
    }

    private static IntPtr FindEverythingWindow()
    {
        foreach (var cls in WindowClasses)
        {
            var hwnd = FindWindow(cls, null);
            if (hwnd != IntPtr.Zero) return hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>Path of the running Everything.exe, or the usual install location.</summary>
    public static string? LocateExecutable()
    {
        var hwnd = FindEverythingWindow();
        if (hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out var pid) != 0)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (p.MainModule?.FileName is { } path) return path;
            }
            catch { /* access denied (service) - fall back */ }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var candidate = System.IO.Path.Combine(root, "Everything", "Everything.exe");
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ------------------------------------------------------------------ reply window

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        var className = "KiteEverythingReply_" + Environment.ProcessId;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COPYDATA && lParam != IntPtr.Zero)
        {
            var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
            if (_pending.TryRemove((uint)cds.dwData.ToInt64(), out var tcs))
            {
                // The data is only valid during this call - copy it out.
                var copy = new byte[cds.cbData];
                if (cds.cbData > 0) Marshal.Copy(cds.lpData, copy, 0, cds.cbData);
                tcs.TrySetResult(copy);
                return new IntPtr(1);
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _ready.Dispose();
    }

    // ------------------------------------------------------------------ interop

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
