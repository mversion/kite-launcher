using System.Runtime.InteropServices;
using Kite.Core.Native;
using Kite.Plugins;

namespace Kite.Core.Catalog;

/// <summary>
/// Enumerates shell:AppsFolder - the same list as Start's "All apps". This is what
/// finds Microsoft Store / UWP apps (Paint, Calculator, Terminal, ...) that have no
/// .lnk file anywhere on disk, which is why Launchy can't see them.
/// </summary>
public static class AppsFolderSource
{
    public const string Prefix = @"shell:AppsFolder\";

    public static List<CatItem> Enumerate() => Shell32.RunSta(EnumerateCore);

    private static List<CatItem> EnumerateCore()
    {
        var items = new List<CatItem>();
        var iidItem = Shell32.IID_IShellItem;
        Shell32.SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iidItem, out var folder);
        try
        {
            var bhid = Shell32.BHID_EnumItems;
            var iidEnum = Shell32.IID_IEnumShellItems;
            var enumerator = (IEnumShellItems)folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum);
            try
            {
                while (enumerator.Next(1, out var child, out var fetched) == 0 && fetched == 1 && child is not null)
                {
                    try
                    {
                        var name = Shell32.GetName(child, SIGDN.NormalDisplay);
                        var id = Shell32.GetName(child, SIGDN.ParentRelativeParsing);
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                        items.Add(new CatItem
                        {
                            Name = name,
                            Target = Prefix + id,
                            Description = Describe(id),
                            IconPath = Prefix + id,
                        });
                    }
                    finally { Marshal.ReleaseComObject(child); }
                }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        finally { Marshal.ReleaseComObject(folder); }
        return items;
    }

    /// <summary>A Store app's id looks like "Microsoft.Paint_8wekyb3d8bbwe!App".</summary>
    public static bool IsPackagedAppId(string id) => id.Contains('!') && !id.Contains('\\');

    private static string Describe(string id)
    {
        if (IsPackagedAppId(id))
        {
            var end = id.IndexOf('_');
            if (end <= 0) end = id.IndexOf('!');
            return "Store app · " + id[..end];
        }
        if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "Web link · " + id;
        // Desktop apps appear as "{KnownFolderGuid}\path\app.exe"; show the real path when we can.
        if (id.StartsWith('{') && id.IndexOf('}') is var close and > 0 &&
            Guid.TryParse(id[1..close], out var folderId) && TryGetKnownFolder(folderId) is { } folder)
            return "App · " + folder + id[(close + 1)..];
        return id.Contains('\\') ? "App · " + id : "App";
    }

    private static string? TryGetKnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var path) != 0 || path == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        catch { return null; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
