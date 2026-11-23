//! The user-facing restore flows shared by the running app (tray menu, hotkey, start-up recovery) and the command line
//! (`--restore-taskbar`): restore from the recovery record; when there is no usable record but the taskbar looks modified, ask
//! for an explicit, confirmed forced restore. Nothing is changed silently when the original state is unknown.

use fd_core::taskbar::{TaskbarCoordinator, TaskbarOutcome, TaskbarResult};

use crate::tray;

pub const FORCE_QUESTION: &str = "FloatingDock cannot find a valid record of the original taskbar state, and the Windows taskbar is hidden or auto-hidden.\n\n\
Force restore will show every taskbar window and turn OFF \"Automatically hide the taskbar\" (the Windows default).\n\n\
Do you want to do that now?";

/// `interactive` false for silent callers (uninstaller): a forced restore is never started without asking, so the result is
/// `NeedsConfirmation`.
pub fn run(taskbar: &TaskbarCoordinator, recover_only: bool, interactive: bool) -> TaskbarResult {
    let r = if recover_only { taskbar.recover_from_previous_run() } else { taskbar.restore() };
    let unknown_state = r.outcome == TaskbarOutcome::NeedsConfirmation
        || (!recover_only && r.outcome == TaskbarOutcome::NothingToDo && taskbar.any_taskbar_window_hidden());
    if !unknown_state {
        return r;
    }
    if !interactive {
        return TaskbarResult { outcome: TaskbarOutcome::NeedsConfirmation, message: "The original taskbar state is unknown; a forced restore needs the user's confirmation.".into() };
    }
    if !tray::confirm(FORCE_QUESTION, true) {
        return TaskbarResult { outcome: TaskbarOutcome::NeedsConfirmation, message: "Forced restore was declined; nothing was changed.".into() };
    }
    taskbar.force_restore(true)
}
