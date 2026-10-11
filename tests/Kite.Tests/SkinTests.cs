using System.IO.Compression;
using Kite.Core.Settings;
using Kite.Core.Skins;

namespace Kite.Tests;

public class SkinLibraryTests
{
    private static (TempDir Dir, SkinLibrary Library) NewLibrary()
    {
        var t = new TempDir();
        var app = Path.Combine(t.Path, "app");
        var data = Path.Combine(t.Path, "data");
        Directory.CreateDirectory(app);
        SkinLibrary.Write(Path.Combine(app, "skins", "Builtin"), new Skin { Name = "Builtin", Accent = "#FF0000" });
        return (t, new SkinLibrary(new KitePaths(app, data)));
    }

    [Fact]
    public void Lists_builtin_as_read_only_and_user_skins_as_editable()
    {
        var (t, lib) = NewLibrary();
        using var _ = t;
        var created = lib.Create("Mine", new Skin());
        var all = lib.List();
        Assert.True(all.Single(s => s.Name == "Builtin").BuiltIn);
        Assert.False(all.Single(s => s.Name == "Mine").BuiltIn);
        Assert.Throws<InvalidOperationException>(() => lib.Delete(all.Single(s => s.Name == "Builtin")));
        Assert.Equal(created.Folder, all.Single(s => s.Name == "Mine").Folder);
    }

    [Fact]
    public void Duplicate_copies_assets_and_picks_unique_names()
    {
        var (t, lib) = NewLibrary();
        using var _ = t;
        var builtin = lib.Find("Builtin")!;
        File.WriteAllText(Path.Combine(builtin.Folder, "frame.png"), "img");

        var copy1 = lib.Duplicate(builtin, "Builtin");
        var copy2 = lib.Duplicate(builtin, "Builtin");
        Assert.Equal("Builtin 2", copy1.Name);
        Assert.Equal("Builtin 3", copy2.Name);
        Assert.True(File.Exists(Path.Combine(copy1.Folder, "frame.png")));
        Assert.Equal("#FF0000", SkinLibrary.Read(copy1.Folder)!.Accent);
    }

    [Fact]
    public void Rename_moves_folder_and_updates_name()
    {
        var (t, lib) = NewLibrary();
        using var _ = t;
        var mine = lib.Create("Old", new Skin());
        var renamed = lib.Rename(mine, "New: name?");
        Assert.Equal("New- name-", renamed.Name); // invalid filename characters replaced
        Assert.False(Directory.Exists(mine.Folder));
        Assert.Equal(renamed.Name, SkinLibrary.Read(renamed.Folder)!.Name);
    }

    [Fact]
    public void Export_then_import_round_trips()
    {
        var (t, lib) = NewLibrary();
        using var _ = t;
        var mine = lib.Create("Roundtrip", new Skin { InputColor = "#123456" });
        var zip = Path.Combine(t.Path, "roundtrip.zip");
        lib.Export(mine, zip);
        var imported = lib.Import(zip);
        Assert.Equal("Roundtrip 2", imported.Name);
        Assert.Equal("#123456", imported.Skin.InputColor);
    }

    [Fact]
    public void Import_rejects_zip_without_skin()
    {
        var (t, lib) = NewLibrary();
        using var _ = t;
        var zip = Path.Combine(t.Path, "junk.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            z.CreateEntry("readme.txt");
        Assert.Throws<InvalidDataException>(() => lib.Import(zip));
        Assert.Empty(Directory.GetDirectories(lib.UserRoot).Where(d => Path.GetFileName(d).StartsWith(".import")));
    }

    [Fact]
    public void Imports_launchy_skin_folder()
    {
        var (t, lib) = NewLibrary();
        using var _ = t;
        var launchy = Path.Combine(t.Path, "LaunchySkin");
        Directory.CreateDirectory(launchy);
        File.WriteAllText(Path.Combine(launchy, "style.qss"), """
            #input { font: 18px bold Sans; color: #00C4FF; qproperty-geometry: rect(40 15 260 24); }
            /* comment */
            #alternatives { background-color: white; color: #646464; selection-background-color: black; selection-color: white; font: 12px Sans; }
            #alternativesPath { color: #adadad; }
            """);
        File.WriteAllText(Path.Combine(launchy, "author.txt"), "mversion-launchy (v. 1.0) for Launchy\nby Bilgin Ozkan - 15 July 2017\n\nMinimalist dark skin.");
        // 1x1 PNG header claiming 490x60
        var png = new byte[24] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 1, 0xEA, 0, 0, 0, 60 };
        File.WriteAllBytes(Path.Combine(launchy, "frame.png"), png);

        var skin = lib.Import(launchy);
        Assert.Equal("LaunchySkin", skin.Name);
        Assert.Equal("#00C4FF", skin.Skin.InputColor);
        Assert.Equal(18, skin.Skin.InputFontSize);
        Assert.Equal("Bold", skin.Skin.InputFontWeight);
        Assert.Equal(40, skin.Skin.PaddingX);
        Assert.Equal("White", skin.Skin.ResultsBackground);
        Assert.Equal("Black", skin.Skin.SelectedBackground);
        Assert.Equal(490, skin.Skin.Width);
        Assert.Equal(60, skin.Skin.InputHeight);
        Assert.Equal("input", skin.Skin.BackgroundImageArea);
        Assert.StartsWith("Bilgin Ozkan", skin.Skin.Author);
        Assert.True(File.Exists(Path.Combine(skin.Folder, "frame.png")));
    }

    [Fact]
    public void Converts_every_installed_launchy_skin()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Launchy", "skins");
        if (!Directory.Exists(installed)) return; // only meaningful where Launchy is installed

        var (t, lib) = NewLibrary();
        using var _ = t;
        foreach (var dir in Directory.GetDirectories(installed))
        {
            var info = lib.Import(dir); // reads only; writes into the temp library
            Assert.True(File.Exists(Path.Combine(info.Folder, SkinLibrary.FileName)), dir);
            Assert.True(info.Skin.Width >= 300, dir);
        }
    }

    [Theory]
    [InlineData("rgba(0,244,120,0%)", "#0000F478")]
    [InlineData("rgba(10, 20, 30, 0.5)", "#800A141E")]
    [InlineData("rgb(255,0,0)", "#FFFF0000")]
    [InlineData("#abc", "#ABC")]
    [InlineData("grey", "Gray")]
    [InlineData("white", "White")]
    public void Converts_qss_colours(string qss, string expected) => Assert.Equal(expected, LaunchySkinConverter.ToKiteColor(qss));
}
