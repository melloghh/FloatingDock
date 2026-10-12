using FloatingDock.Core.Taskbar;
using FloatingDock.Windows.Integration;

namespace FloatingDock.Windows.Taskbar;

/// <summary>
/// The user-facing restore flows shared by the running app (tray menu, hotkey, start-up recovery) and the command line (<c>--restore-taskbar</c>):
/// restore from the recovery record; when there is no usable record but the taskbar looks modified, ask for an explicit, confirmed forced restore.
/// Nothing is changed silently when the original state is unknown.
/// </summary>
public static class TaskbarRestoreFlow
{
    public const string ForceQuestion =
        "FloatingDock cannot find a valid record of the original taskbar state, and the Windows taskbar is hidden or auto-hidden.\n\n" +
        "Force restore will show every taskbar window and turn OFF \"Automatically hide the taskbar\" (the Windows default).\n\n" +
        "Do you want to do that now?";

    /// <param name="interactive">False for silent callers (uninstaller): a forced restore is never started without asking, so the result is NeedsConfirmation.</param>
    public static TaskbarResult Run(TaskbarController taskbar, bool recoverOnly, bool interactive)
    {
        var r = recoverOnly ? taskbar.RecoverFromPreviousRun() : taskbar.Restore();
        bool unknownState = r.Outcome == TaskbarOutcome.NeedsConfirmation || (!recoverOnly && r.Outcome == TaskbarOutcome.NothingToDo && taskbar.AnyTaskbarWindowHidden());
        if (!unknownState) return r;
        if (!interactive) return new TaskbarResult(TaskbarOutcome.NeedsConfirmation, "The original taskbar state is unknown; a forced restore needs the user's confirmation.");
        if (!NativeDialogs.Confirm(ForceQuestion, Native.NativeMethods.MB_ICONWARNING)) return new TaskbarResult(TaskbarOutcome.NeedsConfirmation, "Forced restore was declined; nothing was changed.");
        return taskbar.ForceRestore(disableAutoHide: true);
    }
}
