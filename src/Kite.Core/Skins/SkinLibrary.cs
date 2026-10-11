using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kite.Core.Settings;

namespace Kite.Core.Skins;

public sealed record SkinInfo(string Name, string Folder, Skin Skin, bool BuiltIn);

/// <summary>
/// Skin files on disk: listing, creating, duplicating, renaming, deleting, importing and
/// exporting. User skins live in &lt;data&gt;\skins and override bundled skins of the same
/// name; bundled skins (next to Kite.exe) are read-only unless running portable.
/// </summary>
public sealed class SkinLibrary(KitePaths paths)
{
    public const string FileName = "skin.json";

    public string UserRoot => Path.Combine(paths.DataDirectory, "skins");
    public string BuiltInRoot => Path.Combine(paths.AppDirectory, "skins");

    public bool IsBuiltIn(string folder) =>
        !paths.IsPortable && IsUnder(folder, BuiltInRoot) && !IsUnder(folder, UserRoot);

    public IReadOnlyList<SkinInfo> List()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SkinInfo>();
        foreach (var root in new[] { UserRoot, BuiltInRoot }.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || !File.Exists(Path.Combine(dir, FileName)) || !seen.Add(name)) continue;
                if (Read(dir) is { } skin) result.Add(new SkinInfo(name, dir, skin, IsBuiltIn(dir)));
            }
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public SkinInfo? Find(string name) => List().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static Skin? Read(string folder)
    {
        try { return JsonSerializer.Deserialize<Skin>(File.ReadAllText(Path.Combine(folder, FileName)), Json.Options); }
        catch { return null; }
    }

    public static void Write(string folder, Skin skin)
    {
        Directory.CreateDirectory(folder);
        Json.Save(Path.Combine(folder, FileName), skin);
    }

    /// <summary>Turns user text into a safe, unused folder name ("My skin", "My skin 2", ...).</summary>
    public string UniqueName(string wanted)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(wanted.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim('.', ' ');
        if (clean.Length == 0) clean = "Skin";
        var taken = new HashSet<string>(List().Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(clean) && !Directory.Exists(Path.Combine(UserRoot, clean))) return clean;
        for (var i = 2; ; i++)
        {
            var candidate = $"{clean} {i}";
            if (!taken.Contains(candidate) && !Directory.Exists(Path.Combine(UserRoot, candidate))) return candidate;
        }
    }

    public SkinInfo Create(string name, Skin template)
    {
        var finalName = UniqueName(name);
        var folder = Path.Combine(UserRoot, finalName);
        var skin = Clone(template);
        skin.Name = finalName;
        Write(folder, skin);
        return new SkinInfo(finalName, folder, skin, false);
    }

    /// <summary>Copies a skin (including its images and Theme.xaml) to a new editable user skin.</summary>
    public SkinInfo Duplicate(SkinInfo source, string name)
    {
        var finalName = UniqueName(name);
        var folder = Path.Combine(UserRoot, finalName);
        CopyDirectory(source.Folder, folder);
        var skin = Clone(source.Skin);
        skin.Name = finalName;
        Write(folder, skin);
        return new SkinInfo(finalName, folder, skin, false);
    }

    public SkinInfo Rename(SkinInfo skin, string newName)
    {
        if (skin.BuiltIn) throw new InvalidOperationException("Built-in skins can't be renamed; duplicate it instead.");
        var finalName = UniqueName(newName);
        var folder = Path.Combine(Path.GetDirectoryName(skin.Folder)!, finalName);
        Directory.Move(skin.Folder, folder);
        var updated = Clone(skin.Skin);
        updated.Name = finalName;
        Write(folder, updated);
        return new SkinInfo(finalName, folder, updated, false);
    }

    /// <summary>Moves a user skin to the Recycle Bin.</summary>
    public void Delete(SkinInfo skin)
    {
        if (skin.BuiltIn) throw new InvalidOperationException("Built-in skins can't be deleted.");
        if (!RecycleBin.Send(skin.Folder))
            throw new IOException($"Couldn't move {skin.Folder} to the Recycle Bin.");
    }

    /// <summary>Imports a .zip, a Kite skin folder, or a Launchy skin folder (style.qss + images).</summary>
    public SkinInfo Import(string path)
    {
        Directory.CreateDirectory(UserRoot);
        var staging = Path.Combine(UserRoot, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            string source;
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(path, staging); // rejects entries that escape the folder
                source = LocateSkinRoot(staging) ?? throw new InvalidDataException("The zip doesn't contain a skin.json or a Launchy style.qss.");
            }
            else if (Directory.Exists(path))
            {
                source = LocateSkinRoot(path) ?? throw new InvalidDataException("That folder doesn't contain a skin.json or a Launchy style.qss.");
            }
            else throw new FileNotFoundException("Choose a skin folder or a .zip file.", path);

            // A zip with the skin at its root is named after the zip file itself.
            var baseName = source.Equals(staging, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(path)
                : Path.GetFileName(source.TrimEnd('\\', '/'));

            if (File.Exists(Path.Combine(source, FileName)))
            {
                var skin = Read(source) ?? throw new InvalidDataException("skin.json couldn't be read.");
                var name = UniqueName(string.IsNullOrWhiteSpace(skin.Name) || skin.Name == "Default" ? baseName : skin.Name);
                var folder = Path.Combine(UserRoot, name);
                CopyDirectory(source, folder);
                skin.Name = name;
                Write(folder, skin);
                return new SkinInfo(name, folder, skin, false);
            }

            // Launchy skin
            var launchyName = UniqueName(baseName);
            var target = Path.Combine(UserRoot, launchyName);
            var converted = LaunchySkinConverter.Convert(source, target);
            converted.Name = launchyName;
            Write(target, converted);
            return new SkinInfo(launchyName, target, converted, false);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    public void Export(SkinInfo skin, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(skin.Folder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
    }

    private static string? LocateSkinRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, FileName)) || File.Exists(Path.Combine(dir, "style.qss"))) return dir;
        var subdirs = Directory.GetDirectories(dir);
        return subdirs.Length == 1 ? LocateSkinRoot(subdirs[0]) : null;
    }

    public static Skin Clone(Skin skin) =>
        JsonSerializer.Deserialize<Skin>(JsonSerializer.Serialize(skin, Json.Options), Json.Options)!;

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public static bool Send(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0", // double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !Directory.Exists(path);
    }
}
