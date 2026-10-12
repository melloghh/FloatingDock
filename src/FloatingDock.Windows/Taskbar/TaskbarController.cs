using FloatingDock.Core.Configuration;
using FloatingDock.Core.Taskbar;

namespace FloatingDock.Windows.Taskbar;

/// <summary>
/// Controls the original Windows taskbar. All decisions live in <see cref="TaskbarCoordinator"/> (unit-tested); this class wires it to Win32 and the recovery file
/// <c>taskbar-state.json</c>. No service, no second process: recovery happens (a) on exit, (b) from the tray menu / hotkey / <c>--restore-taskbar</c> (works while the dock is not running)
/// and (c) on the next start, before the configured mode is applied. A forced kill (Task Manager, power loss) cannot run any code - the record on disk is what makes (c) possible.
/// <list type="bullet">
/// <item><b>AutoHide</b> uses the documented AppBar state (ABM_SETSTATE / ABS_AUTOHIDE).</item>
/// <item><b>HideCompletely</b> clips taskbar windows to an empty region and hides them. The original region is persisted and restored; no injection into Explorer.</item>
/// </list>
/// </summary>
public sealed class TaskbarController
{
    private readonly TaskbarCoordinator _coordinator;

    public TaskbarController(string stateFile, Action<string>? log = null)
        : this(new Win32TaskbarSystem(), stateFile, log) { }

    public TaskbarController(ITaskbarSystem system, string stateFile, Action<string>? log = null)
    {
        _coordinator = new TaskbarCoordinator(system, new TaskbarStateStore(stateFile), log);
    }

    public bool HasPendingRestore => _coordinator.HasPendingRestore;
    public TaskbarResult Apply(TaskbarMode mode) => _coordinator.Apply(mode);
    public TaskbarResult Maintain(TaskbarMode mode) => _coordinator.Maintain(mode);
    /// <summary>Restores only what FloatingDock changed. Safe to call any number of times.</summary>
    public TaskbarResult Restore() => _coordinator.Restore();
    public TaskbarResult RecoverFromPreviousRun() => _coordinator.RecoverFromPreviousRun();
    public bool AnyTaskbarWindowHidden() => _coordinator.AnyTaskbarWindowHidden();
    public TaskbarResult ForceRestore(bool disableAutoHide) => _coordinator.ForceRestore(disableAutoHide);
}
