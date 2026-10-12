using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using Xunit;

namespace FloatingDock.Core.Tests;

public sealed class Dock103Tests
{
    [Fact]
    public void StartLayout_OnlyReadsPinsAndPreservesOrder()
    {
        var pins = StartLayoutPins.Parse("""{"pinnedList":[{"desktopAppLink":"a.lnk"},{"packagedAppId":"Pkg!App"},{"desktopAppId":"Win32.Id"},{"packagedAppId":"pkg!app"},{"unknown":"Ignored"}],"allApps":[{"desktopAppLink":"uninstall.lnk"}]}""");
        Assert.Equal(new[] { "a.lnk", "Pkg!App", "Win32.Id" }, pins.Select(p => p.Value));
    }

    [Fact]
    public void StartLayout_FoldersFlattenAtTheirOriginalPosition()
    {
        var pins = StartLayoutPins.Parse("""{"pinnedList":[{"packagedAppId":"a"},{"folder":{"pinnedList":[{"desktopAppId":"b"},{"desktopAppId":"c"}]}},{"packagedAppId":"d"}]}""");
        Assert.Equal(new[] { "a", "b", "c", "d" }, pins.Select(p => p.Value));
    }

    [Fact]
    public void EmptyStartLayout_DoesNotTurnIntoAllApps()
    {
        Assert.Empty(StartLayoutPins.Parse("{\"pinnedList\":[]}"));
        Assert.Throws<InvalidDataException>(() => StartLayoutPins.Parse("{}"));
        Assert.Throws<InvalidDataException>(() => StartLayoutPins.Parse(new string(' ', 1024 * 1024 + 1)));
    }

    [Fact]
    public void Separators_PersistAndCanBeMovedWithoutLosingAppOrder()
    {
        using var dir = new TempDir();
        var store = new AppListStore(dir.File("pins.json"));
        var appA = new AppRef { Name = "A", ExePath = @"C:\A.exe" };
        var appB = new AppRef { Name = "B", ExePath = @"C:\B.exe" };
        var divider = AppRef.Separator();
        Assert.True(store.ReplaceAll(new[] { appA, divider, appB }));
        var loaded = new AppListStore(dir.File("pins.json"));
        Assert.True(loaded.Snapshot()[1].IsSeparator);
        Assert.Equal(divider.Key, loaded.Snapshot()[1].Key);
        Assert.True(loaded.Move(divider.Key, 2));
        Assert.Equal(new[] { appA.Key, appB.Key, divider.Key }, loaded.Snapshot().Select(a => a.Key));
        Assert.True(loaded.Remove(divider.Key));
        Assert.Equal(2, new AppListStore(dir.File("pins.json")).Snapshot().Count);
    }

    [Fact]
    public void Separators_AreNotGroupedAsRunningApplications()
    {
        var a = new AppRef { Name = "A", ExePath = @"C:\A.exe" };
        var divider = AppRef.Separator();
        var b = new AppRef { Name = "B", ExePath = @"C:\B.exe" };
        var window = new WindowInfo(100, 1, @"C:\A.exe", null, "A", "Test", false);
        var entries = new DockEntryReconciler().Build(new(false, false, true, true), new[] { a, divider, b }, new[] { window }, 100);
        Assert.Equal(new[] { DockEntryKind.App, DockEntryKind.Separator, DockEntryKind.App }, entries.Select(e => e.Kind));
        Assert.True(entries[0].IsRunning);
        Assert.False(entries[1].IsRunning);
        Assert.True(entries[1].IsPinned);
        Assert.Equal(divider.Key, entries[1].Key);
        Assert.False(AppMatching.Matches(divider, window));
    }

    [Fact]
    public void OldSchemaAppList_CanAddSeparatorsAndUpgradeWithoutDroppingApps()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("pins.json"), """{"SchemaVersion":1,"Apps":[{"Name":"A","ExePath":"C:\\A.exe"}]}""");
        var store = new AppListStore(dir.File("pins.json"));
        Assert.True(store.Add(AppRef.Separator()));
        Assert.Equal(2, new AppListStore(dir.File("pins.json")).Snapshot().Count);
        Assert.Contains("\"SchemaVersion\": 2", File.ReadAllText(dir.File("pins.json")));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(60, 60)]
    [InlineData(1000, 144)]
    public void FrameLimit_IsBounded(int requested, int expected)
    {
        Assert.Equal(expected, new DockSettings { AnimationFps = requested }.Normalize().AnimationFps);
    }
}
