using System.IO.Compression;
using System.Text;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using Xunit;

namespace FloatingDock.Core.Tests;

public class IconNameMatcherTests
{
    private static readonly byte[] Png = PngWriter.EncodeBgra(2, 2, new byte[16], false);

    private static IconPack MakePack(TempDir d, params string[] files)
    {
        foreach (var f in files)
        {
            var p = Path.Combine(d.Path, f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, Png);
        }
        return IconPack.Load(d.Path);
    }

    [Theory]
    [InlineData("photoshop.png", "Photoshop.exe", MatchRule.Exact)]
    [InlineData("Ms-Teams.png", "msteams", MatchRule.Exact)]
    [InlineData("Adobe Photoshop 2024.png", "Photoshop", MatchRule.WithoutNoise)]
    [InlineData("Photoshop CC.png", "photoshop.exe", MatchRule.WithoutNoise)]
    [InlineData("Фотошоп.png", "Photoshop", MatchRule.SamePronunciation)]
    [InlineData("Дискорд.png", "Discord", MatchRule.SamePronunciation)]
    [InlineData("Телеграм.png", "Telegram", MatchRule.SamePronunciation)]
    [InlineData("photoshop-extra.png", "Photoshop", MatchRule.Contains)]
    public void FileFindsItsApp(string file, string appName, MatchRule rule)
    {
        using var d = new TempDir();
        var pack = MakePack(d, file);
        var m = pack.Find(new[] { appName });
        Assert.NotNull(m);
        Assert.Equal(rule, m!.Rule);
    }

    [Fact]
    public void OneLetterDifference_InCyrillicName()
    {
        using var d = new TempDir();
        var pack = MakePack(d, "Спотифай.png");
        var m = pack.Find(new[] { "Spotify" });
        Assert.NotNull(m);
    }

    [Fact]
    public void GenericOrShortNames_AreNeverMatchedLoosely()
    {
        using var d = new TempDir();
        var pack = MakePack(d, "app.png", "ab.png", "windows-terminal-big.png");
        Assert.Null(pack.Find(new[] { "My App Helper" }));
        Assert.Null(pack.Find(new[] { "Ab Tool" }));
        Assert.Null(pack.Find(new[] { "Notepad" }));
    }

    [Fact]
    public void BuiltInAliases_Work()
    {
        using var d = new TempDir();
        var pack = MakePack(d, "edge.png", "calculator.png");
        Assert.NotNull(pack.Find(new[] { "msedge.exe" }));
        Assert.NotNull(pack.Find(new[] { "CalculatorApp.exe" }));
    }

    [Fact]
    public void DarkVariant_PreferredWhenRequested()
    {
        using var d = new TempDir();
        var pack = MakePack(d, "spotify.png", "spotify_dark.png", "x_mask.png");
        Assert.EndsWith("spotify_dark.png", pack.Find(new[] { "spotify" }, preferDark: true)!.Path);
        Assert.EndsWith("spotify.png", pack.Find(new[] { "spotify" }, preferDark: false)!.Path);
        Assert.Null(pack.Find(new[] { "x" }));
    }

    [Fact]
    public void SubFolders_AreScanned_RootWins()
    {
        using var d = new TempDir();
        var pack = MakePack(d, "sub/steam.png", "steam.png", "a/b/c/deep.png");
        Assert.Equal(Path.Combine(d.Path, "steam.png"), pack.Find(new[] { "steam" })!.Path);
        Assert.Null(pack.Find(new[] { "deep" }));     // 3 levels is beyond the scan depth
    }

    [Fact]
    public void Manifest_MapsNames_Aliases_AndRejectsEscapingPaths()
    {
        using var d = new TempDir();
        File.WriteAllBytes(Path.Combine(d.Path, "ae.png"), Png);
        File.WriteAllBytes(Path.Combine(d.Path, "pp.png"), Png);
        var outside = Path.Combine(Path.GetDirectoryName(d.Path)!, "outside-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(outside, Png);
        try
        {
            File.WriteAllText(Path.Combine(d.Path, "manifest.json"), $$"""
            { "id": "dark-minimal", "name": "Dark Minimal", "version": "1.0.0",
              "icons": { "Adobe After Effects": "ae.png", "premiere": { "file": "pp.png", "aliases": ["Premiere Pro", "PPro"] }, "evil": "../{{Path.GetFileName(outside)}}" } }
            """);
            var pack = IconPack.Load(d.Path);
            Assert.Equal("dark-minimal", pack.Id);
            Assert.Equal("Dark Minimal", pack.Name);
            Assert.NotNull(pack.Find(new[] { "afterfx.exe", "Adobe After Effects 2024" }));
            Assert.EndsWith("pp.png", pack.Find(new[] { "PPro" })!.Path);
            Assert.Null(pack.Find(new[] { "evil" }));
            Assert.Contains(pack.Warnings, w => w.Contains("evil"));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void AppRef_IconNames_ContainAllIdentities()
    {
        var app = new AppRef { Name = "Calculator", ExePath = @"C:\x\CalculatorApp.exe", Aumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", ShortcutPath = @"C:\sm\Calc.lnk" };
        var names = app.IconNames().ToList();
        Assert.Contains("CalculatorApp", names);
        Assert.Contains("Microsoft.WindowsCalculator", names);
        Assert.Contains("WindowsCalculator", names);
        Assert.Contains("Calculator", names);
        Assert.Contains("Calc", names);
    }
}

public class IconPackServiceTests
{
    private static readonly byte[] Png = PngWriter.EncodeBgra(2, 2, new byte[16], false);

    private static string MakeZip(TempDir d, string name, params (string Entry, byte[] Data)[] entries)
    {
        var path = d.File(name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (e, data) in entries)
        {
            var en = z.CreateEntry(e);
            using var s = en.Open();
            s.Write(data);
        }
        return path;
    }

    private static IconPackService Svc(TempDir d) => new(Path.Combine(d.Path, "packs"), Path.Combine(d.Path, "tmp"));

    [Fact]
    public void Import_Works_StripsTopFolder_UsesManifestId_AndIsListed()
    {
        using var d = new TempDir();
        var svc = Svc(d);
        var zip = MakeZip(d, "a.zip", ("MyPack/manifest.json", Encoding.UTF8.GetBytes("""{"id":"dark-minimal","name":"Dark Minimal","version":"1.2.3"}""")), ("MyPack/discord.png", Png), ("MyPack/readme.md", Encoding.UTF8.GetBytes("hi")));
        var preview = svc.Inspect(zip);
        Assert.Equal("Dark Minimal", preview.Name); Assert.Equal(1, preview.IconCount); Assert.DoesNotContain(preview.Problems, p => p.StartsWith("Unsafe"));
        var pack = svc.Import(zip);
        Assert.Equal("dark-minimal", pack.Id);
        Assert.True(File.Exists(Path.Combine(d.Path, "packs", "dark-minimal", "discord.png")));
        Assert.Contains(svc.ListPacks(), p => p.Id == "dark-minimal");
        Assert.NotNull(svc.Load("dark-minimal")!.Find(new[] { "discord" }));
    }

    [Theory]
    [InlineData("../evil.png")]
    [InlineData("a/../../evil.png")]
    [InlineData("/abs/evil.png")]
    [InlineData("C:/evil.png")]
    [InlineData("..\\evil.png")]
    public void Import_RejectsPathTraversal(string entry)
    {
        using var d = new TempDir();
        var svc = Svc(d);
        var zip = MakeZip(d, "evil.zip", (entry, Png), ("ok.png", Png));
        Assert.Throws<IconPackImportException>(() => svc.Import(zip));
        Assert.False(File.Exists(Path.Combine(d.Path, "evil.png")));
        Assert.Equal(new[] { "Custom", "Default" }, svc.ListPacks().Select(p => p.Id).OrderBy(x => x).ToArray());
    }

    [Theory]
    [InlineData("tool.exe")] [InlineData("x/setup.dll")] [InlineData("run.bat")] [InlineData("a.ps1")] [InlineData("evil.lnk")]
    public void Import_RejectsExecutables(string entry)
    {
        using var d = new TempDir();
        var svc = Svc(d);
        var zip = MakeZip(d, "bad.zip", (entry, new byte[] { 1, 2, 3 }), ("ok.png", Png));
        Assert.Throws<IconPackImportException>(() => svc.Import(zip));
    }

    [Fact]
    public void Import_RejectsArchiveWithoutImages_AndNonZip()
    {
        using var d = new TempDir();
        var svc = Svc(d);
        Assert.Throws<IconPackImportException>(() => svc.Import(MakeZip(d, "empty.zip", ("readme.txt", new byte[] { 65 }))));
        var junk = d.File("junk.zip"); File.WriteAllText(junk, "not a zip");
        Assert.Throws<IconPackImportException>(() => svc.Import(junk));
    }

    [Fact]
    public void Import_CannotReplaceDefaultPack_AndOverwritesSameId()
    {
        using var d = new TempDir();
        var svc = Svc(d);
        var z1 = MakeZip(d, "1.zip", ("manifest.json", Encoding.UTF8.GetBytes("""{"id":"Default"}""")), ("a.png", Png));
        Assert.Equal("Default-imported", svc.Import(z1).Id);
        var z2 = MakeZip(d, "2.zip", ("manifest.json", Encoding.UTF8.GetBytes("""{"id":"p1"}""")), ("a.png", Png));
        var z3 = MakeZip(d, "3.zip", ("manifest.json", Encoding.UTF8.GetBytes("""{"id":"p1"}""")), ("b.png", Png));
        svc.Import(z2); svc.Import(z3);
        Assert.False(File.Exists(Path.Combine(d.Path, "packs", "p1", "a.png")));
        Assert.True(File.Exists(Path.Combine(d.Path, "packs", "p1", "b.png")));
        Assert.Throws<IconPackImportException>(() => svc.Import(z3, overwrite: false));
    }

    [Fact]
    public void Delete_RemovesCustomPacks_ButNotBuiltIn()
    {
        using var d = new TempDir();
        var svc = Svc(d);
        svc.Import(MakeZip(d, "x.zip", ("manifest.json", Encoding.UTF8.GetBytes("""{"id":"p1"}""")), ("a.png", Png)));
        Assert.True(svc.Delete("p1"));
        Assert.False(Directory.Exists(Path.Combine(d.Path, "packs", "p1")));
        Assert.False(svc.Delete("Default"));
        Assert.False(svc.Delete("../packs"));
    }

    [Fact]
    public void Export_ThenImport_RoundTrips()
    {
        using var d = new TempDir();
        var svc = Svc(d);
        svc.Import(MakeZip(d, "x.zip", ("manifest.json", Encoding.UTF8.GetBytes("""{"id":"p1","name":"P One"}""")), ("a.png", Png), ("sub/b.png", Png)));
        var outZip = d.File("export.zip");
        svc.Export("p1", outZip);
        svc.Delete("p1");
        var back = svc.Import(outZip);
        Assert.Equal("p1", back.Id);
        Assert.Equal(2, back.IconCount);
    }

    [Fact]
    public void Export_PackWithoutManifest_GetsGeneratedManifest()
    {
        using var d = new TempDir();
        var svc = Svc(d);
        File.WriteAllBytes(Path.Combine(d.Path, "packs", "Custom", "x.png"), Png);
        var outZip = d.File("c.zip");
        svc.Export("Custom", outZip);
        using var z = ZipFile.OpenRead(outZip);
        Assert.Contains(z.Entries, e => e.FullName == "manifest.json");
    }

    [Fact]
    public void Import_EnforcesRealSizeLimit_NotJustHeaders()
    {
        using var d = new TempDir();
        var svc = Svc(d);
        var big = new byte[(int)(IconPackService.MaxEntryBytes + 1024)];     // compresses to almost nothing
        var zip = MakeZip(d, "bomb.zip", ("a.png", big));
        Assert.Throws<IconPackImportException>(() => svc.Import(zip));
    }

    [Fact]
    public void IconResolver_Priority_OverrideThenPackThenNull()
    {
        using var d = new TempDir();
        var svc = Svc(d);
        File.WriteAllBytes(Path.Combine(d.Path, "packs", "Custom", "chrome.png"), Png);
        var ov = new IconOverrideStore(d.File("ov.json"), d.File("ui"));
        var res = new IconResolver(ov, svc);
        res.Configure(true, "Custom", true);
        var app = new AppRef { Name = "Chrome", ExePath = @"C:\chrome.exe" };
        Assert.Equal(MatchRule.Exact, res.Resolve(app)!.Rule);
        var mine = d.File("mine.png"); File.WriteAllBytes(mine, Png);
        ov.Set(app.Key, mine);
        var m = res.Resolve(app)!;
        Assert.Equal(MatchRule.Override, m.Rule);
        Assert.NotEqual(mine, m.Path);                       // a private copy: deleting the source does not break the icon
        File.Delete(mine);
        Assert.True(File.Exists(res.Resolve(app)!.Path));
        Assert.True(ov.Reset(app.Key));
        Assert.Equal(MatchRule.Exact, res.Resolve(app)!.Rule);
        res.Configure(false, "Custom", true);
        Assert.Null(res.Resolve(app));
        Assert.Throws<InvalidDataException>(() => ov.Set(app.Key, d.File("x.exe")));
    }

    [Fact]
    public void ExplorerOverride_IsSharedBetweenAppIdPinsAndExecutableWindows()
    {
        using var d = new TempDir();
        var store = new IconOverrideStore(d.File("ov.json"), d.File("ui"));
        var pin = new AppRef { Aumid = AppMatching.ExplorerAumid };
        var window = new AppRef { ExePath = @"C:\Windows\explorer.exe" };
        var source = d.File("folder.png"); File.WriteAllBytes(source, Png);
        store.Set(pin.Key, source);
        Assert.Equal(store.TryGetForApp(pin), store.TryGetForApp(window));
        Assert.Null(store.TryGetForApp(new AppRef { Name = "Explorer", ExePath = @"C:\other.exe" }));
        Assert.True(store.ResetForApp(window));
        Assert.Null(store.TryGetForApp(pin));
        store.Set(window.Key, source);
        Assert.Equal(store.TryGetForApp(window), store.TryGetForApp(pin));
        Assert.True(store.ResetForApp(pin));
        Assert.Null(store.TryGetForApp(window));
    }

    [Fact]
    public void PngWriter_ProducesValidPng()
    {
        var px = new byte[] { 0, 0, 255, 255,   0, 255, 0, 128,   255, 0, 0, 0,   10, 20, 30, 255 };   // BGRA
        var png = PngWriter.EncodeBgra(2, 2, px, premultiplied: false);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        // inflate IDAT and compare the first pixel (RGBA red)
        int pos = 8; byte[]? idat = null;
        while (pos < png.Length)
        {
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IDAT") idat = png.AsSpan(pos + 8, len).ToArray();
            pos += 12 + len;
        }
        using var z = new ZLibStream(new MemoryStream(idat!), CompressionMode.Decompress);
        var raw = new MemoryStream(); z.CopyTo(raw);
        var r = raw.ToArray();
        Assert.Equal(new byte[] { 0, 255, 0, 0, 255 }, r[..5]);
    }

    [Fact]
    public void LegacyPackRemoval_PreservesIndividualIconsAndImportedPacks()
    {
        using var d = new TempDir();
        var svc = Svc(d);
        foreach (var id in new[] { "DockIcons1", "DockIcons2", "DockIcons3" })
        {
            var folder = Path.Combine(svc.RootDirectory, id); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new { id, description = "Набор из архива владельца проекта" }));
            File.WriteAllBytes(Path.Combine(folder, "steam.png"), Png);
        }
        var custom = Path.Combine(svc.RootDirectory, "Custom", "steam.png"); File.WriteAllBytes(custom, Png);
        var mine = d.File("mine.png"); File.WriteAllBytes(mine, Png);
        var overrides = new IconOverrideStore(d.File("ov.json"), d.File("user-icons"));
        overrides.Set("my-app", mine);
        svc.Import(MakeZip(d,"my-pack.zip",("manifest.json",Encoding.UTF8.GetBytes("""{"id":"MyPack"}""")),("steam.png",Png)));
        svc.RemoveLegacyBundledPacks(); svc.RemoveLegacyBundledPacks();
        Assert.All(new[] { "DockIcons1", "DockIcons2", "DockIcons3" }, id => Assert.False(Directory.Exists(Path.Combine(svc.RootDirectory,id))));
        Assert.True(File.Exists(custom)); Assert.NotNull(overrides.TryGet("my-app")); Assert.NotNull(svc.Load("MyPack"));
    }

    [Fact]
    public void LegacyPackRemoval_DoesNotDeleteUserPackWithSameFolderName()
    {
        using var d = new TempDir(); var svc = Svc(d);
        var dir = Path.Combine(svc.RootDirectory, "DockIcons2"); Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "steam.png"), Png);
        svc.RemoveLegacyBundledPacks();
        Assert.NotNull(svc.Load("DockIcons2")); Assert.True(svc.Delete("DockIcons2"));
    }
}
