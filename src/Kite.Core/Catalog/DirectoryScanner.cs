using System.IO.Enumeration;
using Kite.Core.Settings;
using Kite.Plugins;

namespace Kite.Core.Catalog;

/// <summary>Scans one configured directory the way Launchy does: glob types, depth, optional exes and dirs.</summary>
public static class DirectoryScanner
{
    /// <summary>Extensions hidden from the display name ("Notepad++.lnk" shows as "Notepad++").</summary>
    private static readonly HashSet<string> LaunchableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".exe", ".url", ".bat", ".cmd", ".com", ".appref-ms", ".msc", ".cpl", ".vbs", ".ps1", ".website",
    };

    private static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db",
    };

    /// <summary>Expands %VARS% and resolves relative paths against <paramref name="baseDirectory"/>.</summary>
    public static string ResolvePath(string path, string baseDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(baseDirectory, expanded);
        try { return Path.GetFullPath(expanded).TrimEnd('\\'); }
        catch (Exception) { return expanded; }
    }

    public static List<CatItem> Scan(CatalogDirectory dir, string baseDirectory, bool includeHidden = false, CancellationToken ct = default)
    {
        var results = new List<CatItem>();
        var root = ResolvePath(dir.Path, baseDirectory);
        if (root.Length == 2 && root[1] == ':') root += "\\";
        if (!Directory.Exists(root)) return results;

        var patterns = dir.Types.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (dir.IncludeExecutables && !patterns.Contains("*.exe", StringComparer.OrdinalIgnoreCase))
            patterns.Add("*.exe");

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = includeHidden ? FileAttributes.System : FileAttributes.System | FileAttributes.Hidden,
        };

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (current, depth) = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(current, "*", options))
                {
                    var fileName = Path.GetFileName(file);
                    if (IgnoredFiles.Contains(fileName) || !Matches(fileName, patterns)) continue;
                    results.Add(ItemForFile(file));
                }

                if (depth >= dir.Depth) continue;
                foreach (var sub in Directory.EnumerateDirectories(current, "*", options))
                {
                    if (IsLink(sub)) continue; // junction/symlink loops
                    if (dir.IncludeDirectories)
                        results.Add(new CatItem { Name = Path.GetFileName(sub), Target = sub, Description = sub, IconPath = sub });
                    pending.Push((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return results;
    }

    public static CatItem ItemForFile(string file)
    {
        var ext = Path.GetExtension(file);
        var name = LaunchableExtensions.Contains(ext) ? Path.GetFileNameWithoutExtension(file) : Path.GetFileName(file);
        return new CatItem { Name = name, Target = file, Description = file, IconPath = file };
    }

    public static bool Matches(string fileName, IReadOnlyList<string> patterns)
    {
        foreach (var p in patterns)
            if (FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true))
                return true;
        return false;
    }

    private static bool IsLink(string directory)
    {
        try { return new DirectoryInfo(directory).LinkTarget is not null; }
        catch { return false; }
    }
}
