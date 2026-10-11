using System.Diagnostics;
using System.IO;

namespace Kite.App.Services;

public static class Logger
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
        }
        catch (IOException) { }
    }

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        Debug.WriteLine(line);
        if (_path is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); }
            catch (IOException) { }
        }
    }
}
