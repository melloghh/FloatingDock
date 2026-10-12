using System.IO.Compression;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Icons;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using FloatingDock.Core.Taskbar;
using Xunit;

namespace FloatingDock.Core.Tests;

public class ReleaseRegressionTests
{
    [Fact]
    public void Settings_InvalidEnumsAreNormalizedToSafeDefaults()
    {
        var settings = new DockSettings { WidthMode = (WidthMode)999, Alignment = (DockAlignment)999, MonitorMode = (MonitorMode)999, Theme = (ThemeMode)999, IndicatorStyle = (IndicatorStyle)999, TaskbarMode = (TaskbarMode)999 }.Normalize();
        Assert.Equal(TaskbarMode.Leave, settings.TaskbarMode);
        Assert.Equal(WidthMode.Auto, settings.WidthMode);
        Assert.Equal(DockAlignment.Center, settings.Alignment);
        Assert.Equal(MonitorMode.Primary, settings.MonitorMode);
        Assert.Equal(ThemeMode.Dark, settings.Theme);
        Assert.Equal(IndicatorStyle.Dot, settings.IndicatorStyle);
    }

    [Theory]
    [InlineData(8192, 16384)]
    [InlineData(64, 127)]
    public void IcoHiddenDibDimensionsAreValidated(int width, int height)
    {
        var bytes = new byte[62];
        bytes[2] = 1; bytes[4] = 1; bytes[6] = 16; bytes[7] = 16;
        BitConverter.GetBytes(40u).CopyTo(bytes, 14);
        BitConverter.GetBytes(22u).CopyTo(bytes, 18);
        BitConverter.GetBytes(40u).CopyTo(bytes, 22);
        BitConverter.GetBytes(width).CopyTo(bytes, 26);
        BitConverter.GetBytes(height).CopyTo(bytes, 30);
        using var stream = new MemoryStream(bytes);
        Assert.Null(ImageProbe.Probe(stream, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void PackExport_WhenDestinationIsLocked_PreservesPreviousArchive()
    {
        using var d = new TempDir();
        var packs = new IconPackService(d.File("packs"), d.File("temp"));
        var archive = d.File("export.zip");
        File.WriteAllText(archive, "previous");
        using var held = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.None);
        var failure = Record.Exception(() => packs.Export("Default", archive));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        held.Dispose();
        Assert.Equal("previous", File.ReadAllText(archive));
        Assert.Empty(Directory.GetFiles(d.File(""), "*.tmp-*"));
    }
    private static AppRef App(string name) => new() { Name = name, ExePath = @"C:\" + name + ".exe" };

    [Theory]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":99,\"Apps\":[]}")]
    public void UnreadableAppList_CannotBeOverwrittenByAddOrReplace(string original)
    {
        using var d = new TempDir();
        var path = d.File("pins.json");
        File.WriteAllText(path, original);
        var store = new AppListStore(path);
        int failures = 0;
        store.SaveFailed += _ => failures++;
        Assert.False(store.Add(App("new")));
        Assert.False(store.ReplaceAll(Array.Empty<AppRef>()));
        Assert.Equal(2, failures);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void BackupRecovery_RepairsMainWithoutReplacingGoodBackupWithCorruption()
    {
        using var d = new TempDir();
        var path = d.File("pins.json");
        var original = new AppListStore(path);
        original.Add(App("a")); original.Add(App("b"));
        File.WriteAllText(path, "broken");
        var recovered = new AppListStore(path);
        Assert.True(recovered.Add(App("c")));
        Assert.Single(new AppListStore(path + ".bak").Snapshot());
        Assert.Equal("broken", File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public void FutureSchema_IsNotDowngradedFromAnOlderBackup()
    {
        using var d = new TempDir();
        var path = d.File("pins.json");
        File.WriteAllText(path, "{\"SchemaVersion\":99,\"Apps\":[]}");
        File.WriteAllText(path + ".bak", "[]");
        var store = new AppListStore(path);
        Assert.Equal(LoadStatus.Corrupt, store.LoadStatus);
        Assert.False(store.Add(App("a")));
        Assert.Contains("99", File.ReadAllText(path));
    }

    [Fact]
    public void ActiveTempFiles_AreNotDeletedByAConcurrentLoad()
    {
        using var d = new TempDir();
        var path = d.File("pins.json");
        var temp = path + ".active.tmp";
        File.WriteAllText(temp, "in progress");
        JsonFile.Load<List<AppRef>>(path);
        Assert.True(File.Exists(temp));
    }

    [Fact]
    public void Settings_SaveFailureOnDispose_IsReportedWithoutThrowingOrOverwriting()
    {
        using var d = new TempDir();
        var path = d.File("settings.json");
        File.WriteAllText(path, "broken");
        var store = new SettingsStore(path);
        Exception? failure = null;
        store.SaveFailed += ex => failure = ex;
        store.Update(s => s.IconSize = 60);
        store.Dispose(); store.Dispose();
        Assert.NotNull(failure);
        Assert.Equal("broken", File.ReadAllText(path));
    }

    [Fact]
    public void CorruptOverrides_CannotBeOverwritten()
    {
        using var d = new TempDir();
        var path = d.File("overrides.json");
        File.WriteAllText(path, "broken");
        var store = new IconOverrideStore(path, d.File("icons"));
        Assert.Throws<IOException>(() => store.Set("key", "missing.png"));
        Assert.Throws<IOException>(() => store.Reset("key"));
        Assert.Equal("broken", File.ReadAllText(path));
    }

    [Fact]
    public void IconBackup_StillHasItsImageAfterReplacementAndCorruption()
    {
        using var d = new TempDir();
        var path = d.File("overrides.json");
        var image = d.File("valid.png");
        File.WriteAllBytes(image, PngWriter.EncodeBgra(2, 2, new byte[16], false));
        var store = new IconOverrideStore(path, d.File("icons"));
        store.Set("key", image);
        var first = store.TryGet("key");
        store.Set("key", image);
        Assert.True(File.Exists(first));
        File.WriteAllText(path, "broken");
        var recovered = new IconOverrideStore(path, d.File("icons"));
        Assert.Equal(first, recovered.TryGet("key"));
    }

    [Fact]
    public void PackagedAppsSharingAnExecutable_HaveSeparateKeysAndGroups()
    {
        var a = App("host"); a.Aumid = "Family!First";
        var b = App("host"); b.Aumid = "Family!Second";
        Assert.NotEqual(a.Key, b.Key);
        Assert.False(AppMatching.AreEquivalent(a, b));
        var w1 = new WindowInfo(1, 42, a.ExePath, a.Aumid, "First", "Class", false);
        var w2 = w1 with { Hwnd = 2, Aumid = b.Aumid };
        Assert.NotEqual(AppMatching.GroupKey(w1), AppMatching.GroupKey(w2));
        Assert.True(AppMatching.IsIdentityComplete(null, a.Aumid, false));
    }

    [Fact]
    public void CorruptTaskbarRecord_RefusesChangesEvenWhenWindowsAreVisible()
    {
        using var d = new TempDir();
        var path = d.File("taskbar.json"); File.WriteAllText(path, "broken");
        var fake = new FakeTaskbar();
        var co = new TaskbarCoordinator(fake, new TaskbarStateStore(path));
        Assert.Equal(TaskbarOutcome.NeedsConfirmation, co.Apply(TaskbarMode.AutoHide).Outcome);
        Assert.Equal(0, fake.SetStateCalls);
        Assert.Equal("broken", File.ReadAllText(path));
    }

    [Fact]
    public void TaskbarDelete_WhenBackupCannotBeDeleted_KeepsMainRecord()
    {
        using var d = new TempDir();
        var path = d.File("taskbar.json");
        var store = new TaskbarStateStore(path);
        Assert.True(store.TrySave(new TaskbarStateRecord(), out _));
        File.WriteAllText(path + ".bak", "backup");
        using var held = new FileStream(path + ".bak", FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(store.TryDelete());
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("COM9/icon.png")]
    [InlineData("LPT9.png")]
    [InlineData("../escape.png")]
    [InlineData("a.png", "A.PNG")]
    public void ZipUnsafeNamesAndDuplicateWindowsPaths_AreRejected(string first, string? second = null)
    {
        using var d = new TempDir();
        var archive = d.File("pack.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var name in second is null ? new[] { first } : new[] { first, second })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(PngWriter.EncodeBgra(2, 2, new byte[16], false));
            }
        }
        var packs = new IconPackService(d.File("packs"), d.File("temp"));
        Assert.Throws<IconPackImportException>(() => packs.Import(archive));
    }
}
