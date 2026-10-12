using FloatingDock.Core.Configuration;
using FloatingDock.Core.Taskbar;
using Xunit;

namespace FloatingDock.Core.Tests;

/// <summary>A taskbar that can be told to misbehave.</summary>
internal sealed class FakeTaskbar : ITaskbarSystem
{
    public sealed class Win { public long Handle; public string Class = TaskbarFlags.MainClass; public string Monitor = "\\\\.\\DISPLAY1"; public bool Visible = true; public bool Exists = true; public byte[]? Region; public bool Clipped; }

    public bool Present = true;
    public int Flags;
    public List<Win> Windows = new() { new Win { Handle = 100 } };
    public bool FailSetVisible, FailSetState;
    public int SetStateCalls, SetVisibleCalls;

    public bool TryGetAppBarState(out int flags) { flags = Flags; return Present; }
    public bool TrySetAppBarState(int flags) { SetStateCalls++; if (FailSetState || !Present) return false; Flags = flags & 3; return true; }
    public IReadOnlyList<TaskbarWindow> GetTaskbarWindows() => !Present ? new List<TaskbarWindow>() : Windows.Where(w => w.Exists).Select(w => new TaskbarWindow(w.Handle, w.Class, w.Monitor, w.Visible)).ToList();
    public bool IsWindow(long handle) => Windows.Any(w => w.Handle == handle && w.Exists);
    public bool TrySetVisible(long handle, bool visible)
    {
        SetVisibleCalls++;
        var w = Windows.FirstOrDefault(x => x.Handle == handle && x.Exists);
        if (w is null || FailSetVisible) return false;
        w.Visible = visible;
        return true;
    }

    public bool AutoHide => (Flags & TaskbarFlags.AutoHide) != 0;
    public bool FailRegionRestore;
    public bool TryGetRegion(long handle, out byte[]? data) { data = Windows.FirstOrDefault(w => w.Handle == handle && w.Exists)?.Region; return IsWindow(handle); }
    public bool TrySetRegion(long handle, byte[]? data)
    {
        var w = Windows.FirstOrDefault(w => w.Handle == handle && w.Exists);
        if (w is null || FailRegionRestore) return false;
        w.Region = data; w.Clipped = false; return true;
    }
    public bool TryClipEmpty(long handle)
    {
        var w = Windows.FirstOrDefault(w => w.Handle == handle && w.Exists);
        if (w is null || FailSetVisible) return false;
        w.Clipped = true; return true;
    }
    public bool IsClippedEmpty(long handle) => Windows.Any(w => w.Handle == handle && w.Exists && w.Clipped);
    /// <summary>Explorer restarted: all windows are new, visible, with new handles; the auto-hide setting survives (it is stored in the registry).</summary>
    public void RestartExplorer()
    {
        long next = Windows.Max(w => w.Handle) + 1000;
        foreach (var w in Windows.Where(w => w.Exists).ToList()) { w.Exists = false; Windows.Add(new Win { Handle = next++, Class = w.Class, Monitor = w.Monitor, Visible = true }); }
    }
}

public class TaskbarCoordinatorTests
{
    [Fact]
    public void NewSettings_ReplaceTaskbarAndTrackCursorDirectly()
    {
        var settings = new DockSettings();
        Assert.Equal(TaskbarMode.HideCompletely, settings.TaskbarMode);
        Assert.Equal(10, settings.Responsiveness);
    }

    [Fact]
    public void RegionIntent_IsPersistedAndCrashRecoveryRestoresTheOriginalRegion()
    {
        var fake = new FakeTaskbar();
        var original = new byte[48]; original[0] = 32; original[4] = 1; original[8] = 1;
        fake.Windows[0].Region = original;
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        Assert.True(co.Apply(TaskbarMode.HideCompletely).Success);
        var saved = store.Load().Record!;
        Assert.Equal(2, saved.SchemaVersion);
        Assert.True(saved.Windows[0].RegionChangedByUs);
        Assert.Equal(original, saved.Windows[0].OriginalRegion);
        var next = new TaskbarCoordinator(fake, store, null, () => 4343);
        Assert.True(next.RecoverFromPreviousRun().Success);
        Assert.False(fake.Windows[0].Clipped);
        Assert.Equal(original, fake.Windows[0].Region);
        Assert.Equal(TaskbarStateStatus.Missing, store.Load().Status);
    }

    [Fact]
    public void FailedRegionRestore_KeepsRecoveryRecordForRetry()
    {
        var fake = new FakeTaskbar();
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        fake.FailRegionRestore = true;
        Assert.Equal(TaskbarOutcome.RetryNeeded, co.Restore().Outcome);
        Assert.Equal(TaskbarStateStatus.Ok, store.Load().Status);
        fake.FailRegionRestore = false;
        Assert.True(co.Restore().Success);
        Assert.False(fake.Windows[0].Clipped);
    }

    [Fact]
    public void AVisibleWindow_WithEmptyRegion_RemainsHiddenWithoutRepeatedWrites()
    {
        var fake = new FakeTaskbar();
        var (co, _, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        fake.Windows[0].Visible = true;
        int calls = fake.SetVisibleCalls;
        Assert.Equal(TaskbarOutcome.NothingToDo, co.Maintain(TaskbarMode.HideCompletely).Outcome);
        Assert.Equal(calls, fake.SetVisibleCalls);
        Assert.True(fake.Windows[0].Clipped);
        Assert.True(co.Restore().Success);
        Assert.False(fake.Windows[0].Clipped);
    }

    [Fact]
    public void Maintain_WhenExplorerShowsTaskbarAgain_PreservesTheOriginalRestoreState()
    {
        var fake = new FakeTaskbar();
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        Assert.True(co.Apply(TaskbarMode.HideCompletely).Success);
        fake.Windows[0].Visible = true;
        fake.Windows[0].Clipped = false;
        Assert.Equal(TaskbarOutcome.Ok, co.Maintain(TaskbarMode.HideCompletely).Outcome);
        Assert.False(fake.Windows[0].Visible);
        Assert.True(store.Load().Record!.Windows[0].OriginalVisible);
        Assert.True(co.Restore().Success);
        Assert.True(fake.Windows[0].Visible);
        Assert.False(fake.AutoHide);
    }

    [Fact]
    public void Maintain_SteadyState_DoesNotWriteFilesOrChangeSystemState()
    {
        var fake = new FakeTaskbar();
        var (co, _, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        int calls = fake.SetStateCalls + fake.SetVisibleCalls;
        using var locked = new FileStream(dir.File("taskbar-state.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(TaskbarOutcome.NothingToDo, co.Maintain(TaskbarMode.HideCompletely).Outcome);
        Assert.Equal(calls, fake.SetStateCalls + fake.SetVisibleCalls);
    }

    [Fact]
    public void Maintain_WithoutOwnedRecoveryRecord_RefusesToHide()
    {
        var fake = new FakeTaskbar();
        var (co, _, dir) = Make(fake);
        using var __ = dir;
        Assert.Equal(TaskbarOutcome.Refused, co.Maintain(TaskbarMode.HideCompletely).Outcome);
        Assert.True(fake.Windows[0].Visible);
        Assert.Equal(0, fake.SetStateCalls + fake.SetVisibleCalls);
    }

    [Fact]
    public void Maintain_AfterExplorerRestart_ReconcilesNewHandlesAndRestoresThem()
    {
        var fake = new FakeTaskbar();
        var (co, _, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        fake.RestartExplorer();
        Assert.Equal(TaskbarOutcome.Ok, co.Maintain(TaskbarMode.HideCompletely).Outcome);
        Assert.All(fake.GetTaskbarWindows(), window => Assert.False(window.Visible));
        Assert.True(co.Restore().Success);
        Assert.All(fake.GetTaskbarWindows(), window => Assert.True(window.Visible));
        Assert.False(fake.AutoHide);
    }

    private static (TaskbarCoordinator Co, TaskbarStateStore Store, TempDir Dir) Make(FakeTaskbar fake, TempDir? dir = null)
    {
        dir ??= new TempDir();
        var store = new TaskbarStateStore(dir.File("taskbar-state.json"));
        return (new TaskbarCoordinator(fake, store, null, () => 4242), store, dir);
    }

    [Fact]
    public void HideCompletely_ThenRestore_PutsEverythingBack_AndDeletesRecordOnlyAfterVerification()
    {
        var fake = new FakeTaskbar();
        fake.Windows.Add(new FakeTaskbar.Win { Handle = 200, Class = TaskbarFlags.SecondaryClass, Monitor = "\\\\.\\DISPLAY2" });
        var (co, store, dir) = Make(fake);
        using var __ = dir;

        var r = co.Apply(TaskbarMode.HideCompletely);
        Assert.Equal(TaskbarOutcome.Ok, r.Outcome);
        Assert.True(fake.AutoHide);
        Assert.All(fake.Windows, w => Assert.False(w.Visible));
        var rec = store.Load().Record!;
        Assert.Equal(TaskbarPhase.Applied, rec.Phase);
        Assert.False(rec.OriginalAutoHide);
        Assert.True(rec.AutoHideChangedByUs);
        Assert.Equal(2, rec.Windows.Count);
        Assert.All(rec.Windows, w => Assert.True(w.OriginalVisible));

        var back = co.Restore();
        Assert.Equal(TaskbarOutcome.Ok, back.Outcome);
        Assert.False(fake.AutoHide);
        Assert.All(fake.Windows, w => Assert.True(w.Visible));
        Assert.Equal(TaskbarStateStatus.Missing, store.Load().Status);
    }

    [Fact]
    public void OriginallyAutoHidden_StaysAutoHidden_AfterRestore()
    {
        var fake = new FakeTaskbar { Flags = TaskbarFlags.AutoHide | TaskbarFlags.AlwaysOnTop };
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        Assert.Equal(TaskbarOutcome.Ok, co.Apply(TaskbarMode.HideCompletely).Outcome);
        Assert.False(store.Load().Record!.AutoHideChangedByUs);
        Assert.Equal(0, fake.SetStateCalls);                       // already auto-hide: the setting is not touched at all
        co.Restore();
        Assert.True(fake.AutoHide);                                // never turned into a permanently visible taskbar
        Assert.Equal(0, fake.SetStateCalls);
        Assert.True(fake.Windows[0].Visible);
    }

    [Fact]
    public void WhenTheRecordCannotBeSaved_NothingIsChanged()
    {
        var fake = new FakeTaskbar();
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("taskbar-state.json"));              // a directory in the way: every save fails
        var (co, _, _) = Make(fake, dir);
        var r = co.Apply(TaskbarMode.HideCompletely);
        Assert.False(r.Success);
        Assert.Equal(0, fake.SetStateCalls);
        Assert.Equal(0, fake.SetVisibleCalls);
        Assert.False(fake.AutoHide);
        Assert.True(fake.Windows[0].Visible);
    }

    [Fact]
    public void LeaveMode_WithoutRecord_DoesNothing()
    {
        var fake = new FakeTaskbar { Flags = TaskbarFlags.AutoHide };
        var (co, _, dir) = Make(fake);
        using var __ = dir;
        var r = co.Apply(TaskbarMode.Leave);
        Assert.Equal(TaskbarOutcome.NothingToDo, r.Outcome);
        Assert.Equal(0, fake.SetStateCalls + fake.SetVisibleCalls);
        Assert.Equal(TaskbarOutcome.NothingToDo, co.Restore().Outcome);       // normal exit with mode Leave
    }

    [Fact]
    public void FailedRestore_KeepsTheRecord_AndCanBeRetried()
    {
        var fake = new FakeTaskbar();
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        fake.FailSetVisible = true;
        var r = co.Restore();
        Assert.Equal(TaskbarOutcome.RetryNeeded, r.Outcome);
        Assert.Equal(TaskbarStateStatus.Ok, store.Load().Status);             // record kept
        Assert.False(fake.Windows[0].Visible);

        fake.FailSetVisible = false;
        Assert.Equal(TaskbarOutcome.Ok, co.Restore().Outcome);
        Assert.True(fake.Windows[0].Visible);
        Assert.Equal(TaskbarStateStatus.Missing, store.Load().Status);
    }

    [Fact]
    public void Restore_WhileExplorerIsNotRunning_KeepsTheRecord()
    {
        var fake = new FakeTaskbar();
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.AutoHide);
        fake.Present = false;
        Assert.Equal(TaskbarOutcome.RetryNeeded, co.Restore().Outcome);
        Assert.Equal(TaskbarStateStatus.Ok, store.Load().Status);
        fake.Present = true;
        Assert.Equal(TaskbarOutcome.Ok, co.Restore().Outcome);
        Assert.False(fake.AutoHide);
    }

    [Fact]
    public void HideFails_FallsBackToAutoHide_AndShowsTheWindowsAgain()
    {
        var fake = new FakeTaskbar();
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        fake.FailSetVisible = true;           // hiding is refused; nothing was hidden, so nothing needs showing
        var r = co.Apply(TaskbarMode.HideCompletely);
        Assert.Equal(TaskbarOutcome.Degraded, r.Outcome);
        Assert.True(r.Success);
        Assert.True(fake.AutoHide);
        Assert.True(fake.Windows[0].Visible);
        Assert.Equal(TaskbarMode.AutoHide, store.Load().Record!.AppliedMode);
    }

    [Fact]
    public void AutoHideCannotBeSet_RollsBack_AndReportsRefused()
    {
        var fake = new FakeTaskbar { FailSetState = true };
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        var r = co.Apply(TaskbarMode.HideCompletely);
        Assert.Equal(TaskbarOutcome.Refused, r.Outcome);
        Assert.True(fake.Windows[0].Visible);
        Assert.Equal(TaskbarStateStatus.Missing, store.Load().Status);        // nothing left to recover
    }

    [Fact]
    public void CrashRecovery_NewProcessRestoresFromTheRecord()
    {
        var fake = new FakeTaskbar();
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        // the process is killed here: no Restore() runs. A new process starts with the same state file:
        var second = new TaskbarCoordinator(fake, store);
        Assert.True(second.HasPendingRestore);
        var r = second.RecoverFromPreviousRun();
        Assert.Equal(TaskbarOutcome.Ok, r.Outcome);
        Assert.True(fake.Windows[0].Visible);
        Assert.False(fake.AutoHide);
        Assert.False(second.HasPendingRestore);
    }

    [Fact]
    public void CrashBetweenIntentAndChange_IsRecoveredFromThePendingRecord()
    {
        var fake = new FakeTaskbar();
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        var rec = store.Load().Record!;
        rec.Phase = TaskbarPhase.Pending;                        // simulate a crash in the middle: only the intent was recorded
        rec.Windows[0].HiddenByUs = false;
        store.TrySave(rec, out var ignored);
        Assert.Equal(TaskbarOutcome.Ok, new TaskbarCoordinator(fake, store).RecoverFromPreviousRun().Outcome);
        Assert.True(fake.Windows[0].Visible);
        Assert.False(fake.AutoHide);
    }

    [Fact]
    public void DamagedRecord_WithHiddenTaskbar_NeedsConfirmation_AndChangesNothing_UntilForced()
    {
        var fake = new FakeTaskbar { Flags = TaskbarFlags.AutoHide };
        fake.Windows[0].Visible = false;
        using var dir = new TempDir();
        File.WriteAllText(dir.File("taskbar-state.json"), "{ this is not json");
        var (co, store, _) = Make(fake, dir);

        var r = co.RecoverFromPreviousRun();
        Assert.Equal(TaskbarOutcome.NeedsConfirmation, r.Outcome);
        Assert.Equal(0, fake.SetStateCalls + fake.SetVisibleCalls);       // no silent changes
        Assert.True(File.Exists(dir.File("taskbar-state.json.corrupt")));
        Assert.Equal(TaskbarOutcome.NeedsConfirmation, co.Restore().Outcome);
        Assert.Equal(TaskbarOutcome.NeedsConfirmation, co.Apply(TaskbarMode.AutoHide).Outcome);
        Assert.Equal(0, fake.SetStateCalls + fake.SetVisibleCalls);

        var forced = co.ForceRestore(disableAutoHide: true);
        Assert.Equal(TaskbarOutcome.Ok, forced.Outcome);
        Assert.True(fake.Windows[0].Visible);
        Assert.False(fake.AutoHide);
        Assert.Equal(TaskbarStateStatus.Missing, store.Load().Status);
    }

    [Fact]
    public void RecordFromANewerVersion_IsNotTrusted()
    {
        var fake = new FakeTaskbar();
        using var dir = new TempDir();
        File.WriteAllText(dir.File("taskbar-state.json"), "{\"SchemaVersion\": 99, \"Phase\": \"Applied\", \"AppliedMode\": \"HideCompletely\", \"Windows\": []}");
        var (co, store, _) = Make(fake, dir);
        Assert.Equal(TaskbarStateStatus.Corrupt, store.Load().Status);
        Assert.Equal(TaskbarOutcome.NeedsConfirmation, co.RecoverFromPreviousRun().Outcome); // visibility cannot establish the original auto-hide state
    }

    [Fact]
    public void ReApplyAfterExplorerRestart_UsesTheOriginalState_NotTheHiddenOne()
    {
        var fake = new FakeTaskbar();
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        fake.RestartExplorer();                                            // new handles, visible again
        Assert.Equal(TaskbarOutcome.Ok, co.Apply(TaskbarMode.HideCompletely).Outcome);
        Assert.All(fake.GetTaskbarWindows(), w => Assert.False(w.Visible));
        var rec = store.Load().Record!;
        Assert.Single(rec.Windows);
        Assert.True(rec.Windows[0].OriginalVisible);
        Assert.False(rec.OriginalAutoHide);
        co.Restore();
        Assert.All(fake.GetTaskbarWindows(), w => Assert.True(w.Visible));
        Assert.False(fake.AutoHide);
    }

    [Fact]
    public void RestoreAfterExplorerRestart_ThenRestore_IsClean()
    {
        var fake = new FakeTaskbar();
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        fake.RestartExplorer();
        Assert.Equal(TaskbarOutcome.Ok, co.Restore().Outcome);
        Assert.False(fake.AutoHide);                                       // the auto-hide setting survived the restart and is reverted
        Assert.Equal(TaskbarStateStatus.Missing, store.Load().Status);
    }

    [Fact]
    public void MonitorHotPlug_NewSecondaryTaskbarIsHiddenAndLaterRestored()
    {
        var fake = new FakeTaskbar();
        var (co, _, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        fake.Windows.Add(new FakeTaskbar.Win { Handle = 300, Class = TaskbarFlags.SecondaryClass, Monitor = "\\\\.\\DISPLAY3" });
        co.Apply(TaskbarMode.HideCompletely);
        Assert.False(fake.Windows.Single(w => w.Handle == 300).Visible);
        co.Restore();
        Assert.All(fake.Windows, w => Assert.True(w.Visible));
    }

    [Fact]
    public void WindowThatWasNotVisibleBefore_IsNotShownByRestore()
    {
        var fake = new FakeTaskbar();
        fake.Windows.Add(new FakeTaskbar.Win { Handle = 200, Class = TaskbarFlags.SecondaryClass, Monitor = "\\\\.\\DISPLAY2", Visible = false });
        var (co, _, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        co.Restore();
        Assert.True(fake.Windows[0].Visible);
        Assert.False(fake.Windows[1].Visible);
    }

    [Fact]
    public void SwitchingToAutoHide_ShowsTheWindowsThatWereHidden()
    {
        var fake = new FakeTaskbar();
        var (co, _, dir) = Make(fake);
        using var __ = dir;
        co.Apply(TaskbarMode.HideCompletely);
        Assert.False(fake.Windows[0].Visible);
        co.Apply(TaskbarMode.AutoHide);
        Assert.True(fake.Windows[0].Visible);
        Assert.True(fake.AutoHide);
        co.Apply(TaskbarMode.Leave);
        Assert.False(fake.AutoHide);
    }

    [Fact]
    public void ExplorerMissing_Apply_IsRefused()
    {
        var fake = new FakeTaskbar { Present = false };
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        Assert.Equal(TaskbarOutcome.Refused, co.Apply(TaskbarMode.AutoHide).Outcome);
        Assert.Equal(TaskbarStateStatus.Missing, store.Load().Status);
    }

    [Fact]
    public void RestoredPhaseRecord_IsIgnored()
    {
        var fake = new FakeTaskbar { Flags = TaskbarFlags.AutoHide };
        var (co, store, dir) = Make(fake);
        using var __ = dir;
        store.TrySave(new TaskbarStateRecord { Phase = TaskbarPhase.Restored, AppliedMode = TaskbarMode.HideCompletely, OriginalAppBarFlags = 0, AutoHideChangedByUs = true }, out var ignored);
        Assert.Equal(TaskbarOutcome.NothingToDo, co.Restore().Outcome);
        Assert.True(fake.AutoHide);                                // a stale record must not revert settings the user chose since
    }
}
