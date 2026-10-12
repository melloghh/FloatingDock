using FloatingDock.Core.Models;
using FloatingDock.Core.Services;
using Xunit;

namespace FloatingDock.Core.Tests;

public class ReconcilerTests
{
    private static readonly DockEntryOptions All = new(true, true, true, true);
    private static WindowInfo W(long h, string? exe, string? aumid = null, int pid = 1) => new(h, pid, exe, aumid, "t", "c", false);
    private static AppRef Pin(string name, string? exe = null, string? aumid = null) => new() { Name = name, ExePath = exe, Aumid = aumid };

    [Fact]
    public void Layout_StartLaunchpadPinnedSeparatorRunning()
    {
        var r = new DockEntryReconciler();
        var pinned = new[] { Pin("Chrome", @"C:\chrome.exe"), Pin("Code", @"C:\code.exe") };
        var list = r.Build(All, pinned, new[] { W(1, @"C:\chrome.exe"), W(2, @"C:\other.exe") }, 0);
        Assert.Equal(new[] { DockEntryKind.Start, DockEntryKind.Launchpad, DockEntryKind.App, DockEntryKind.App, DockEntryKind.Separator, DockEntryKind.App }, list.Select(e => e.Kind).ToArray());
        Assert.True(list[2].IsRunning && list[2].IsPinned);
        Assert.False(list[3].IsRunning);
        Assert.False(list[5].IsPinned);
    }

    [Fact]
    public void PinnedAndRunning_IsOneIcon()
    {
        var r = new DockEntryReconciler();
        var list = r.Build(All, new[] { Pin("Chrome", @"C:\Chrome.exe") }, new[] { W(1, @"c:\chrome.EXE"), W(2, @"C:\chrome.exe") }, 2);
        var apps = list.Where(e => e.Kind == DockEntryKind.App).ToList();
        Assert.Single(apps);
        Assert.Equal(2, apps[0].Windows.Count);
        Assert.True(apps[0].IsActive);
    }

    [Fact]
    public void WindowsOfSameProgram_AreMerged_EvenIfOnlySomeReportAumid()
    {
        var r = new DockEntryReconciler();
        var list = r.Build(All, Array.Empty<AppRef>(), new[] { W(1, @"C:\Windows\explorer.exe", "Microsoft.Windows.Explorer"), W(2, @"C:\Windows\explorer.exe") }, 0);
        Assert.Single(list, e => e.Kind == DockEntryKind.App);
    }

    [Fact]
    public void ExplorerPinnedByAumidOnly_MatchesExplorerWindows()
    {
        var r = new DockEntryReconciler();
        var list = r.Build(All, new[] { Pin("File Explorer", aumid: "Microsoft.Windows.Explorer") }, new[] { W(1, @"C:\Windows\explorer.exe") }, 0);
        Assert.Single(list, e => e.Kind == DockEntryKind.App);
        Assert.True(list.Single(e => e.Kind == DockEntryKind.App).IsRunning);
    }

    [Fact]
    public void Settings_AppFrameHostWindowWithAumid_IsNotLost()
    {
        var r = new DockEntryReconciler();
        var host = @"C:\Windows\System32\ApplicationFrameHost.exe";
        var list = r.Build(All, Array.Empty<AppRef>(), new[] { W(10, host, "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel") }, 0);
        var app = list.Single(e => e.Kind == DockEntryKind.App);
        Assert.StartsWith("aumid:", app.Key);
        Assert.Equal("immersivecontrolpanel", app.Name);
    }

    [Fact]
    public void Settings_AppFrameHostWindowWithoutAnyIdentity_IsStillShown()
    {
        var r = new DockEntryReconciler();
        var host = @"C:\Windows\System32\ApplicationFrameHost.exe";
        var list = r.Build(All, Array.Empty<AppRef>(), new[] { W(10, host), W(11, host) }, 0);
        Assert.Equal(2, list.Count(e => e.Kind == DockEntryKind.App));          // two unknown frames: never dropped
    }

    [Fact]
    public void ClosingOneOfTwoWindows_KeepsRunning_ClosingLast_RemovesUnpinnedAndKeepsPinned()
    {
        var r = new DockEntryReconciler();
        var pinned = new[] { Pin("A", @"C:\a.exe") };
        var two = new[] { W(1, @"C:\a.exe"), W(2, @"C:\a.exe"), W(3, @"C:\b.exe") };
        Assert.True(r.Build(All, pinned, two, 0).First(e => e.Name == "A").IsRunning);
        var one = new[] { W(2, @"C:\a.exe"), W(3, @"C:\b.exe") };
        Assert.True(r.Build(All, pinned, one, 0).First(e => e.Name == "A").IsRunning);
        var none = new[] { W(3, @"C:\b.exe") };
        var list = r.Build(All, pinned, none, 0);
        var a = list.First(e => e.Name == "A");
        Assert.False(a.IsRunning); Assert.True(a.IsPinned);
        Assert.Single(list, e => e.Kind == DockEntryKind.App && !e.IsPinned);
        list = r.Build(All, pinned, Array.Empty<WindowInfo>(), 0);
        Assert.DoesNotContain(list, e => e.Kind == DockEntryKind.App && !e.IsPinned);
        Assert.DoesNotContain(list, e => e.Kind == DockEntryKind.Separator);
    }

    [Fact]
    public void RunningOrder_IsStable_FirstSeenFirst()
    {
        var r = new DockEntryReconciler();
        var names = new List<string>();
        r.Build(All, Array.Empty<AppRef>(), new[] { W(1, @"C:\z.exe") }, 0);
        var list = r.Build(All, Array.Empty<AppRef>(), new[] { W(2, @"C:\a.exe"), W(1, @"C:\z.exe") }, 0);
        Assert.Equal(new[] { "z", "a" }, list.Where(e => e.Kind == DockEntryKind.App).Select(e => e.Name).ToArray());
    }

    [Fact]
    public void SamePinnedTwice_ShowsOnce()
    {
        var r = new DockEntryReconciler();
        var list = r.Build(All, new[] { Pin("A", @"C:\a.exe"), Pin("A2", @"C:\A.exe") }, Array.Empty<WindowInfo>(), 0);
        Assert.Single(list, e => e.Kind == DockEntryKind.App);
    }

    [Fact]
    public void PinningRunningApp_KeepsTheSameKey_SoTheIconDoesNotBlink()
    {
        var r = new DockEntryReconciler();
        var w = W(1, @"C:\a.exe", "SomeAumid");
        var before = r.Build(All, Array.Empty<AppRef>(), new[] { w }, 0).Single(e => e.Kind == DockEntryKind.App);
        var pinned = AppMatching.FromWindow(w);
        var after = r.Build(All, new[] { pinned }, new[] { w }, 0).Single(e => e.Kind == DockEntryKind.App);
        Assert.Equal(before.Key, after.Key);
        Assert.True(after.IsPinned);
    }

    [Fact]
    public void Options_HidePinned_ShowsPinnedOnlyWhileRunning()
    {
        var r = new DockEntryReconciler();
        var opt = new DockEntryOptions(false, false, false, true);
        var list = r.Build(opt, new[] { Pin("A", @"C:\a.exe"), Pin("B", @"C:\b.exe") }, new[] { W(1, @"C:\a.exe") }, 0);
        Assert.Equal(new[] { "A" }, list.Where(e => e.Kind == DockEntryKind.App).Select(e => e.Name).ToArray());
        var noRunning = new DockEntryOptions(false, false, true, false);
        list = r.Build(noRunning, new[] { Pin("A", @"C:\a.exe") }, new[] { W(1, @"C:\a.exe"), W(2, @"C:\x.exe") }, 0);
        Assert.Single(list, e => e.Kind == DockEntryKind.App);
    }
}
