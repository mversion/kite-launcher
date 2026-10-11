using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Kite.Core.Search;
using Kite.Core.Settings;
using Kite.LaunchyImport;

namespace Kite.Tests;

public class ImporterTests
{
    private const string SampleIni = """
        [General]
        version=260

        [directories]
        1\name=C:\\ProgramData\\Microsoft\\Windows\\Start Menu
        1\types=*.lnk
        1\indexDirs=false
        1\indexExes=false
        1\depth=100
        2\name=Utilities\\
        2\types=*.lnk, *.exe
        2\indexDirs=true
        2\indexExes=true
        2\depth=3
        size=3
        3\name=D:/Backup/^
        3\types=*.lnk
        3\indexDirs=false
        3\indexExes=true
        3\depth=100
        2\Display\pos=@Point(-635 949)

        [GenOps]
        skin=mversion
        hotkey=134217760
        hideiflostfocus=false
        alwayscenter=3
        numviewable=4
        numresults=10
        updatetimer=10
        fadein=0
        fadeout=20

        [runner]
        cmds\1\name=cmd
        cmds\1\file=C:\\Windows\\System32\\cmd.exe
        cmds\1\args=/K $$
        cmds\size=1

        [weby]
        sites\1\name=Google
        sites\1\query="http://www.google.com/search?source=launchy&q=%1"
        sites\2\name=E-Mail
        sites\2\query=mailto:%1
        sites\size=2

        [calcy]
        outputRounding=4
        outputGroupSeparator=false
        copyToClipboard=true
        useCommaForDecimal=false

        [History]
        e=everything, C:/ProgramData/Microsoft/Windows/Start Menu/Programs/Everything.lnk
        po=0, 0.calcy
        po%20chr=po - chrome, G:/My Drive/Portable Apps/^/PO - Chrome.lnk
        quoted="name, with comma", C:/x/y.lnk
        """;

    [Fact]
    public void Imports_all_sections()
    {
        var r = LaunchyImporter.Import(QSettingsIni.Parse(SampleIni), @"C:\Program Files (x86)\Launchy");
        var s = r.Settings;

        Assert.Equal("Alt+Space", s.General.Hotkey);
        Assert.Equal("mversion", s.General.Skin);
        Assert.False(s.General.HideOnFocusLoss);
        Assert.True(s.General.CenterHorizontally && s.General.CenterVertically);
        Assert.Equal(4, s.General.VisibleResults);
        Assert.Equal(200, s.General.FadeOutMs);

        Assert.Equal(3, s.Directories.Count);
        Assert.Equal(@"C:\ProgramData\Microsoft\Windows\Start Menu", s.Directories[0].Path);
        Assert.Equal(@"C:\Program Files (x86)\Launchy\Utilities", s.Directories[1].Path);
        Assert.Equal(["*.lnk", "*.exe"], s.Directories[1].Types);
        Assert.True(s.Directories[1].IncludeDirectories);
        Assert.Equal(3, s.Directories[1].Depth);
        Assert.Equal(@"D:\Backup\^", s.Directories[2].Path);
        Assert.True(s.Directories[2].IncludeExecutables);

        var sites = (JsonArray)s.Plugins["weby"]["sites"]!;
        Assert.Equal(2, sites.Count);
        Assert.Equal("http://www.google.com/search?source=launchy&q=%1", (string)sites[0]!["query"]!);
        Assert.True((bool)sites[0]!["default"]!);

        var cmds = (JsonArray)s.Plugins["runner"]["commands"]!;
        Assert.Equal("/K $$", (string)cmds[0]!["args"]!);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", (string)cmds[0]!["file"]!);

        Assert.Equal(4, (int)s.Plugins["calcy"]["rounding"]!);

        Assert.Equal(3, r.Aliases.Count); // calcy entry skipped
        Assert.Contains(r.Aliases, a => a.Typed == "po chr" && a.Key == @"G:\My Drive\Portable Apps\^\PO - Chrome.lnk");
        Assert.Contains(r.Aliases, a => a.Typed == "quoted" && a.Name == "name, with comma");
    }

    [Fact]
    public void Apply_populates_history()
    {
        using var t = new TempDir();
        var store = new SettingsStore(new KitePaths(t.Path, t.Path));
        var history = new History();
        var r = LaunchyImporter.Import(QSettingsIni.Parse(SampleIni), @"C:\L");
        LaunchyImporter.Apply(r, store, history);

        Assert.Equal(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Everything.lnk", history.Lookup("e")!.Key);
        Assert.True(File.Exists(store.Paths.SettingsFile));
    }

    [Theory]
    [InlineData(134217760, "Alt+Space")]
    [InlineData(0x04000000 | 0x20, "Ctrl+Space")]
    [InlineData(0x04000000 | 0x02000000 | 0x4B, "Ctrl+Shift+K")]
    [InlineData(0x08000000 | 0x01000030, "Alt+F1")]
    public void Decodes_qt_hotkeys(int code, string expected) => Assert.Equal(expected, QtHotkey.ToText(code));

    [Fact]
    public void Real_launchy_ini_imports_without_modifying_it()
    {
        var path = LaunchyImporter.DefaultIniPath;
        if (!File.Exists(path)) return; // only meaningful on a machine with Launchy installed

        var before = SHA256.HashData(File.ReadAllBytes(path));
        var r = LaunchyImporter.Import(path);
        var after = SHA256.HashData(File.ReadAllBytes(path));

        Assert.Equal(before, after);
        Assert.NotEmpty(r.Settings.Directories);
        Assert.NotEmpty(r.Aliases);
        Assert.All(r.Aliases, a => Assert.False(string.IsNullOrWhiteSpace(a.Key)));
    }
}
