using Kite.Core.Catalog;
using Kite.Core.Settings;

namespace Kite.Tests;

public class ScannerTests
{
    [Fact]
    public void Honours_types_depth_exes_and_dirs()
    {
        using var t = new TempDir();
        t.File(@"Top.lnk");
        t.File(@"readme.txt");
        t.File(@"desktop.ini");
        t.File(@"A\Tool.exe");
        t.File(@"A\Nested.lnk");
        t.File(@"A\B\Deep.lnk");

        var lnkOnly = DirectoryScanner.Scan(new CatalogDirectory { Path = t.Path, Types = ["*.lnk"], Depth = 1 }, t.Path);
        Assert.Equal(["Nested", "Top"], lnkOnly.Select(i => i.Name).Order());

        var withExes = DirectoryScanner.Scan(new CatalogDirectory { Path = t.Path, Types = ["*.lnk"], IncludeExecutables = true, Depth = 100 }, t.Path);
        Assert.Equal(["Deep", "Nested", "Tool", "Top"], withExes.Select(i => i.Name).Order());

        var withDirs = DirectoryScanner.Scan(new CatalogDirectory { Path = t.Path, Types = [], IncludeDirectories = true, Depth = 100 }, t.Path);
        Assert.Equal(["A", "B"], withDirs.Select(i => i.Name).Order());

        var everything = DirectoryScanner.Scan(new CatalogDirectory { Path = t.Path, Types = ["*.*"], Depth = 0 }, t.Path);
        Assert.Equal(["readme.txt", "Top"], everything.Select(i => i.Name).Order()); // desktop.ini skipped
    }

    [Fact]
    public void Handles_caret_folders_forward_slashes_and_env_vars()
    {
        using var t = new TempDir();
        t.File(@"^\VNC\Server.lnk");
        Environment.SetEnvironmentVariable("KITE_TEST_ROOT", t.Path);

        var forward = DirectoryScanner.Scan(new CatalogDirectory { Path = t.Path.Replace('\\', '/') + "/^" }, t.Path);
        var env = DirectoryScanner.Scan(new CatalogDirectory { Path = @"%KITE_TEST_ROOT%\^" }, t.Path);

        Assert.Single(forward);
        Assert.Equal(Path.Combine(t.Path, @"^\VNC\Server.lnk"), forward[0].Target); // normalised to backslashes
        Assert.Equal(forward[0].Target, env[0].Target);
    }

    [Fact]
    public void Relative_paths_resolve_against_base_directory()
    {
        using var t = new TempDir();
        t.File(@"Utilities\Tool.lnk");
        var items = DirectoryScanner.Scan(new CatalogDirectory { Path = @"Utilities\" }, t.Path);
        Assert.Single(items);
    }

    [Fact]
    public void Missing_directory_yields_nothing()
    {
        Assert.Empty(DirectoryScanner.Scan(new CatalogDirectory { Path = @"Z:\definitely\not\here" }, "C:\\"));
    }
}
