using System.IO.Compression;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using Xunit;

namespace FloatingDock.Core.Tests;

public class JsonFileTests
{
    [Fact]
    public void WriteAtomic_KeepsPreviousVersion_AndLeavesNoTempFiles()
    {
        using var d = new TempDir();
        var p = d.File("a.json");
        JsonFile.WriteAtomic(p, "{\"v\":1}");
        JsonFile.WriteAtomic(p, "{\"v\":2}");
        Assert.Equal("{\"v\":2}", File.ReadAllText(p));
        Assert.Equal("{\"v\":1}", File.ReadAllText(p + ".bak"));
        Assert.Empty(Directory.GetFiles(d.Path, "*.tmp"));
    }

    [Fact]
    public void WriteAtomic_Failure_Throws_AndCleansUp()
    {
        using var d = new TempDir();
        var p = d.File("a.json");
        Directory.CreateDirectory(p);
        Assert.ThrowsAny<IOException>(() => JsonFile.WriteAtomic(p, "x"));
        Assert.Empty(Directory.GetFiles(d.Path, "*.tmp"));
    }

    [Fact]
    public void Load_FallsBackToBackup_WhenMainIsDamaged()
    {
        using var d = new TempDir();
        var p = d.File("a.json");
        JsonFile.Save(p, new Dictionary<string, int> { ["x"] = 1 });
        JsonFile.Save(p, new Dictionary<string, int> { ["x"] = 2 });
        File.WriteAllText(p, "{ broken");
        var r = JsonFile.Load<Dictionary<string, int>>(p);
        Assert.Equal(LoadStatus.RecoveredFromBackup, r.Status);
        Assert.Equal(1, r.Value!["x"]);
        Assert.True(File.Exists(p + ".corrupt"));
    }

    [Fact]
    public void Load_ReportsMissingAndCorrupt()
    {
        using var d = new TempDir();
        Assert.Equal(LoadStatus.Missing, JsonFile.Load<Dictionary<string, int>>(d.File("none.json")).Status);
        File.WriteAllText(d.File("bad.json"), "nope");
        var r = JsonFile.Load<Dictionary<string, int>>(d.File("bad.json"));
        Assert.Equal(LoadStatus.Corrupt, r.Status);
        Assert.True(r.Damaged);
        Assert.Equal("nope", File.ReadAllText(d.File("bad.json.corrupt")));
    }
}

public class AppListStoreRobustnessTests
{
    private static AppRef App(string exe) => new() { Name = Path.GetFileNameWithoutExtension(exe), ExePath = exe };

    [Fact]
    public void SaveFailure_RollsBackTheChange_AndRaisesSaveFailed()
    {
        using var d = new TempDir();
        var p = d.File("pinned.json");
        var store = new AppListStore(p);
        Assert.True(store.Add(App(@"C:\a.exe")));
        // make every further save fail: a directory takes the place of the file's backup target
        File.Delete(p);
        Directory.CreateDirectory(p);
        Exception? seen = null;
        store.SaveFailed += e => seen = e;
        var changed = 0;
        store.Changed += () => changed++;

        Assert.False(store.Add(App(@"C:\b.exe")));
        Assert.NotNull(seen);
        Assert.NotNull(store.LastError);
        Assert.Equal(0, changed);
        Assert.Single(store.Snapshot());                          // memory still equals what is on disk
        Assert.False(store.Remove(@"exe:c:\zzz.exe"));
        Assert.False(store.Move(store.Snapshot()[0].Key, 5));
    }

    [Fact]
    public void CorruptFile_IsKept_AndNotReportedAsEmptyData()
    {
        using var d = new TempDir();
        var p = d.File("pinned.json");
        File.WriteAllText(p, "{ \"Apps\": [ oops");
        var store = new AppListStore(p);
        Assert.Equal(LoadStatus.Corrupt, store.LoadStatus);
        Assert.Empty(store.Snapshot());
        Assert.Equal("{ \"Apps\": [ oops", File.ReadAllText(p + ".corrupt"));
    }

    [Fact]
    public void LastGoodBackup_IsUsed_WhenTheMainFileIsDamaged()
    {
        using var d = new TempDir();
        var p = d.File("pinned.json");
        var s1 = new AppListStore(p);
        s1.Add(App(@"C:\a.exe"));
        s1.Add(App(@"C:\b.exe"));           // .bak now holds the list with only a.exe
        File.WriteAllText(p, "garbage");
        var s2 = new AppListStore(p);
        Assert.Equal(LoadStatus.RecoveredFromBackup, s2.LoadStatus);
        Assert.Single(s2.Snapshot());
    }

    [Fact]
    public void LegacyBareArray_IsMigrated_AndNewerSchemaIsRefused()
    {
        using var d = new TempDir();
        var legacy = d.File("legacy.json");
        File.WriteAllText(legacy, "[ { \"Name\": \"Old\", \"ExePath\": \"C:\\\\old.exe\" } ]");
        var s = new AppListStore(legacy);
        Assert.Equal(LoadStatus.Ok, s.LoadStatus);
        Assert.Equal("Old", s.Snapshot().Single().Name);
        Assert.True(s.Add(App(@"C:\new.exe")));
        Assert.Contains("SchemaVersion", File.ReadAllText(legacy));

        var future = d.File("future.json");
        File.WriteAllText(future, "{ \"SchemaVersion\": 99, \"Apps\": [] }");
        var f = new AppListStore(future);
        Assert.Equal(LoadStatus.Corrupt, f.LoadStatus);
        Assert.True(File.Exists(future + ".corrupt"));
    }
}

public class ImageProbeTests
{
    internal static byte[] Png(int w, int h)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        b.AddRange(new[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h });
        b.AddRange(new byte[20]);
        return b.ToArray();
    }

    internal static byte[] Ico(int w, int h)
    {
        var png = PngWriter.EncodeBgra(2, 2, new byte[16], false);
        var b = new List<byte> { 0, 0, 1, 0, 1, 0, (byte)w, (byte)h, 0, 0, 1, 0, 32, 0 };
        b.AddRange(BitConverter.GetBytes((uint)png.Length));
        b.AddRange(BitConverter.GetBytes(22u));
        b.AddRange(png);
        return b.ToArray();
    }

    private static ImageInfo? Probe(byte[] data, out string? err)
    {
        using var ms = new MemoryStream(data);
        return ImageProbe.Probe(ms, out err);
    }

    [Fact]
    public void ValidPng_AndIco_Pass()
    {
        Assert.Equal(new ImageInfo(ImageFormat.Png, 256, 128, 1), Probe(Png(256, 128), out _));
        var ico = Probe(Ico(32, 32), out var err);
        Assert.NotNull(ico);
        Assert.Equal(ImageFormat.Ico, ico!.Value.Format);
        Assert.Null(err);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(5000, 10)]
    [InlineData(4096, 4096)]
    public void PngWithBadDimensions_IsRejected(int w, int h) => Assert.Null(Probe(Png(w, h), out _));

    [Fact]
    public void GarbageTruncatedAndHugeFiles_AreRejected()
    {
        Assert.Null(Probe(new byte[] { 1, 2, 3 }, out _));
        Assert.Null(Probe(System.Text.Encoding.ASCII.GetBytes("GIF89a......................"), out var err));
        Assert.NotNull(err);
        var ico = Ico(16, 16);
        Assert.Null(Probe(ico[..30], out _));                       // directory points beyond the end
        using var d = new TempDir();
        using (var fs = File.Create(d.File("big.png"))) fs.SetLength(IconLimits.MaxFileBytes + 1);
        Assert.Null(ImageProbe.Probe(d.File("big.png"), out err));
        Assert.Contains("large", err);
    }

    [Fact]
    public void PngInsideIco_IsCheckedForItsRealSize()
    {
        var png = Png(8000, 8000);
        var b = new List<byte> { 0, 0, 1, 0, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0 };
        b.AddRange(BitConverter.GetBytes((uint)png.Length));
        b.AddRange(BitConverter.GetBytes(22u));
        b.AddRange(png);
        Assert.Null(Probe(b.ToArray(), out _));
    }
}

public class IconOverrideStoreRobustnessTests
{
    private static string WriteIcon(TempDir d, string name, byte[]? data = null)
    {
        var p = d.File(name);
        File.WriteAllBytes(p, data ?? PngWriter.EncodeBgra(2, 2, new byte[16], false));
        return p;
    }

    [Fact]
    public void Set_ReplacesIcon_AndKeepsTheOldFileForJsonBackupRecovery()
    {
        using var d = new TempDir();
        var store = new IconOverrideStore(d.File("o.json"), d.File("icons"));
        store.Set("exe:a", WriteIcon(d, "1.png"));
        var first = store.TryGet("exe:a")!;
        store.Set("exe:a", WriteIcon(d, "2.png"));
        var second = store.TryGet("exe:a")!;
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Empty(Directory.GetFiles(d.File("icons"), "*.tmp"));
        Assert.Equal(second, new IconOverrideStore(d.File("o.json"), d.File("icons")).TryGet("exe:a"));       // persisted
    }

    [Fact]
    public void Set_WhenTheJsonCannotBeSaved_KeepsTheOldIcon_AndRemovesTheNewFile()
    {
        using var d = new TempDir();
        var store = new IconOverrideStore(d.File("o.json"), d.File("icons"));
        store.Set("exe:a", WriteIcon(d, "1.png"));
        var old = store.TryGet("exe:a")!;
        File.Delete(d.File("o.json"));
        Directory.CreateDirectory(d.File("o.json"));                // saving now fails
        Assert.ThrowsAny<IOException>(() => store.Set("exe:a", WriteIcon(d, "2.png")));
        Assert.Equal(old, store.TryGet("exe:a"));
        Assert.True(File.Exists(old));
        Assert.Single(Directory.GetFiles(d.File("icons")));         // no orphan of the failed attempt, no temp file
    }

    [Fact]
    public void Set_RejectsInvalidFiles_WithoutTouchingAnything()
    {
        using var d = new TempDir();
        var store = new IconOverrideStore(d.File("o.json"), d.File("icons"));
        Assert.Throws<InvalidDataException>(() => store.Set("k", WriteIcon(d, "x.png", ImageProbeTests.Png(9000, 9000))));
        Assert.Throws<InvalidDataException>(() => store.Set("k", WriteIcon(d, "y.png", new byte[100])));
        Assert.Throws<InvalidDataException>(() => store.Set("k", WriteIcon(d, "z.ico")));            // PNG data with an .ico name
        Assert.Throws<InvalidDataException>(() => store.Set("k", WriteIcon(d, "z.gif")));
        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(d.File("o.json")));
    }

    [Fact]
    public void HandEditedJson_CannotPointOutsideTheIconFolder()
    {
        using var d = new TempDir();
        var outside = WriteIcon(d, "secret.png");
        var icons = d.File("icons");
        Directory.CreateDirectory(icons);
        File.WriteAllText(d.File("o.json"), System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1, Overrides = new Dictionary<string, string> { ["k"] = outside, ["rel"] = Path.Combine(icons, "..", "secret.png") } }));
        var store = new IconOverrideStore(d.File("o.json"), icons);
        Assert.Null(store.TryGet("k"));
        Assert.Null(store.TryGet("rel"));
        Assert.False(store.IsOwnedFile(outside));
    }

    [Fact]
    public void CorruptJson_IsReported_AndLeftNextToTheCopy_AndStaleTempFilesAreRemoved()
    {
        using var d = new TempDir();
        var icons = d.File("icons");
        Directory.CreateDirectory(icons);
        File.WriteAllText(Path.Combine(icons, "abc.png.123.tmp"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(icons, "abc.png.123.tmp"), DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(d.File("o.json"), "{{{");
        var store = new IconOverrideStore(d.File("o.json"), icons);
        Assert.Equal(LoadStatus.Corrupt, store.LoadStatus);
        Assert.Equal(0, store.Count);
        Assert.True(File.Exists(d.File("o.json.corrupt")));
        Assert.Empty(Directory.GetFiles(icons, "*.tmp"));
    }

    [Fact]
    public void Reset_RemovesOverride_AndKeepsImageForBackup()
    {
        using var d = new TempDir();
        var store = new IconOverrideStore(d.File("o.json"), d.File("icons"));
        store.Set("k", WriteIcon(d, "1.png"));
        var f = store.TryGet("k")!;
        Assert.True(store.Reset("k"));
        Assert.True(File.Exists(f));
        Assert.False(store.Has("k"));
        Assert.False(store.Reset("k"));
    }
}

public class IconPackLimitTests
{
    private static string MakeZip(TempDir d, params (string Name, byte[] Data)[] files)
    {
        var zip = d.File("pack.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var (n, data) in files) { var e = z.CreateEntry(n); using var s = e.Open(); s.Write(data); }
        return zip;
    }

    [Fact]
    public void Import_SkipsImagesThatBreakTheLimits_AndKeepsTheGoodOnes()
    {
        using var d = new TempDir();
        var svc = new IconPackService(d.File("packs"), d.File("tmp"));
        var good = PngWriter.EncodeBgra(2, 2, new byte[16], false);
        var pack = svc.Import(MakeZip(d, ("p/good.png", good), ("p/huge.png", ImageProbeTests.Png(10000, 10000)), ("p/fake.png", new byte[50])));
        Assert.Equal(1, pack.IconCount);
        Assert.Contains(pack.Warnings, w => w.Contains("huge.png"));
        Assert.Contains(pack.Warnings, w => w.Contains("fake.png"));
    }

    [Fact]
    public void Import_FailsWhenNoUsableIconRemains()
    {
        using var d = new TempDir();
        var svc = new IconPackService(d.File("packs"), d.File("tmp"));
        Assert.Throws<IconPackImportException>(() => svc.Import(MakeZip(d, ("only.png", ImageProbeTests.Png(10000, 10000)))));
        Assert.DoesNotContain(Directory.GetDirectories(d.File("packs")), x => !x.EndsWith("Default") && !x.EndsWith("Custom"));
    }

    [Fact]
    public void Import_RejectsOversizedImageEntries()
    {
        using var d = new TempDir();
        var svc = new IconPackService(d.File("packs"), d.File("tmp"));
        var big = new byte[(int)IconLimits.MaxFileBytes + 1024];
        Array.Copy(ImageProbeTests.Png(8, 8), big, 32);
        Assert.Throws<IconPackImportException>(() => svc.Import(MakeZip(d, ("big.png", big), ("ok.png", PngWriter.EncodeBgra(2, 2, new byte[16], false)))));
    }

    [Fact]
    public void LoadedPack_IgnoresOversizedFilesOnDisk()
    {
        using var d = new TempDir();
        var dir = d.File("pack");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ok.png"), PngWriter.EncodeBgra(2, 2, new byte[16], false));
        using (var fs = File.Create(Path.Combine(dir, "giant.png"))) fs.SetLength(IconLimits.MaxFileBytes + 1);
        var pack = IconPack.Load(dir);
        Assert.Equal(1, pack.IconCount);
        Assert.Contains(pack.Warnings, w => w.Contains("giant.png"));
    }
}

public class WindowFilterRulesTests
{
    private static WindowFacts Normal(int w = 800, int h = 600) => new(true, 0x10CF0000, 0, false, false, "Notepad", false, w, h, false);

    [Fact]
    public void NormalWindow_IsCandidate() => Assert.True(WindowFilterRules.IsCandidate(Normal()));

    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(0, 0)]
    [InlineData(-5, 600)]
    public void WindowWithoutArea_IsRejected_EvenWhenOnlyOneSideIsZero(int w, int h) => Assert.False(WindowFilterRules.IsCandidate(Normal(w, h)));

    [Fact]
    public void MinimizedWindow_WithParkedRectangle_IsStillACandidate() =>
        Assert.True(WindowFilterRules.IsCandidate(Normal(0, 0) with { Minimized = true }));

    [Fact]
    public void ChildToolOwnedCloakedHiddenOwnAndSystemWindows_AreRejected()
    {
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { Style = WindowFilterRules.WS_CHILD }));
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { ExStyle = WindowFilterRules.WS_EX_TOOLWINDOW }));
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { ExStyle = WindowFilterRules.WS_EX_NOACTIVATE }));
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { HasOwner = true }));
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { Cloaked = true }));
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { Visible = false }));
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { IsOwnProcess = true }));
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { ClassName = "Shell_TrayWnd" }));
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { ExStyle = WindowFilterRules.WS_EX_TRANSPARENT | WindowFilterRules.WS_EX_LAYERED | WindowFilterRules.WS_EX_TOPMOST }));
    }

    [Fact]
    public void AppWindowStyle_OverridesToolOwnedAndNoActivate_ButNeverCloakedOrChild()
    {
        var app = WindowFilterRules.WS_EX_APPWINDOW;
        Assert.True(WindowFilterRules.IsCandidate(Normal() with { ExStyle = app | WindowFilterRules.WS_EX_TOOLWINDOW | WindowFilterRules.WS_EX_NOACTIVATE, HasOwner = true }));
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { ExStyle = app, Cloaked = true }));
        Assert.False(WindowFilterRules.IsCandidate(Normal() with { ExStyle = app, Style = WindowFilterRules.WS_CHILD }));
    }

    [Fact]
    public void EmptyTitle_IsNotAReasonToDropAWindow() => Assert.True(WindowFilterRules.IsCandidate(Normal()));     // the title is not even an input: valid apps may have none
}

public class UserNotificationPolicyTests
{
    [Theory]
    [InlineData(0, 3, true)]            // S_OK + D3D fullscreen
    [InlineData(0, 4, true)]            // presentation mode
    [InlineData(0, 5, false)]           // accepts notifications
    [InlineData(0, 2, false)]
    [InlineData(0, 99, false)]          // unknown value
    [InlineData(unchecked((int)0x80004005), 3, false)]     // E_FAIL: the out value is garbage and must be ignored
    [InlineData(-1, 4, false)]
    public void Interpretation(int hr, int state, bool expected) => Assert.Equal(expected, UserNotificationPolicy.IsFullscreen(hr, state));
}

public class IdentityRulesTests
{
    private const string Host = @"C:\Windows\System32\ApplicationFrameHost.exe";

    [Fact]
    public void ChildPidAlone_DoesNotCompleteAHostedWindow()
    {
        Assert.False(AppMatching.IsIdentityComplete(Host, null, isFrameHost: true));       // only a PID of the hosted app was found: still unknown
        Assert.False(AppMatching.IsIdentityComplete(null, null, isFrameHost: true));
    }

    [Fact]
    public void HostedWindow_IsCompleteWithAnAumid_OrTheRealExe()
    {
        Assert.True(AppMatching.IsIdentityComplete(Host, "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", true));
        Assert.True(AppMatching.IsIdentityComplete(@"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", null, true));
    }

    [Fact]
    public void Win32Window_NeedsItsExe()
    {
        Assert.True(AppMatching.IsIdentityComplete(@"C:\Program Files\App\app.exe", null, false));
        Assert.False(AppMatching.IsIdentityComplete(null, "some.aumid", false));
        Assert.False(AppMatching.IsIdentityComplete(Host, null, false));
    }

    [Fact]
    public void HostProcess_IsNeverTheGroupIdentity()
    {
        var w = new WindowInfo(1, 10, Host, null, "x", "ApplicationFrameWindow", false);
        Assert.StartsWith("win:", AppMatching.GroupKey(w));          // kept as its own entry, never merged into a shared "ApplicationFrameHost" group
        var w2 = w with { Aumid = "pkg_abc!app" };
        Assert.Equal("aumid:pkg_abc!app", AppMatching.GroupKey(w2));
        Assert.Null(AppMatching.FromWindow(w2).ExePath);
    }

    [Fact]
    public void DifferentPackagedApps_SharingAnExe_AreNotMerged()
    {
        var app = new AppRef { Name = "PWA 1", ExePath = @"C:\edge\msedge.exe", Aumid = "Edge.pwa1!App" };
        var other = new WindowInfo(1, 1, @"C:\edge\msedge.exe", "Edge.pwa2!App", "t", "Chrome_WidgetWin_1", false);
        var same = other with { Aumid = "edge.PWA1!app" };
        Assert.False(AppMatching.Matches(app, other));
        Assert.True(AppMatching.Matches(app, same));
        Assert.True(AppMatching.Matches(new AppRef { ExePath = @"C:\edge\msedge.exe" }, other));      // an app pinned by exe still claims its windows
    }

    [Fact]
    public void PinnedByExeAndByAumid_AreShownOnce_AndDifferentAppsStaySeparate()
    {
        var a = new AppRef { Name = "Calc", ExePath = @"C:\calc\calc.exe", Aumid = "MS.Calc_x!App" };
        var b = new AppRef { Name = "Calc (store)", Aumid = "ms.calc_x!app" };
        var c = new AppRef { Name = "Other", ExePath = @"C:\other.exe" };
        Assert.True(AppMatching.AreEquivalent(a, b));
        Assert.False(AppMatching.AreEquivalent(a, c));
        var rec = new DockEntryReconciler();
        var entries = rec.Build(new DockEntryOptions(false, false, true, true), new[] { a, b, c }, Array.Empty<WindowInfo>(), 0);
        Assert.Equal(new[] { "Calc", "Other" }, entries.Select(e => e.Name));
    }
}
