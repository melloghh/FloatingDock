//! Would the Windows taskbar show this window? Pure rules - the Windows layer only collects the facts.

/// Everything the candidate decision needs to know about a top-level window.
#[derive(Debug, Clone, Default)]
pub struct WindowFacts {
    pub visible: bool,
    pub style: i64,
    pub ex_style: i64,
    pub has_owner: bool,
    pub cloaked: bool,
    pub class_name: String,
    pub is_own_process: bool,
    pub width: i32,
    pub height: i32,
    pub minimized: bool,
}

pub const WS_CHILD: i64 = 0x4000_0000;
pub const WS_EX_TOOLWINDOW: i64 = 0x80;
pub const WS_EX_APPWINDOW: i64 = 0x4_0000;
pub const WS_EX_NOACTIVATE: i64 = 0x0800_0000;
pub const WS_EX_TOPMOST: i64 = 0x8;
pub const WS_EX_LAYERED: i64 = 0x8_0000;
pub const WS_EX_TRANSPARENT: i64 = 0x20;

const IGNORED_CLASSES: [&str; 12] = [
    "Progman",
    "WorkerW",
    "Shell_TrayWnd",
    "Shell_SecondaryTrayWnd",
    "Windows.UI.Core.CoreWindow",
    "NotifyIconOverflowWindow",
    "TopLevelWindowForOverflowXamlIsland",
    "XamlExplorerHostIslandWindow",
    "ForegroundStaging",
    "MultitaskingViewFrame",
    "TaskListThumbnailWnd",
    "Shell_InputSwitchTopLevelWindow",
];

pub fn is_ignored_class(class: &str) -> bool {
    !class.is_empty() && IGNORED_CLASSES.iter().any(|c| c.eq_ignore_ascii_case(class))
}

pub fn is_candidate(f: &WindowFacts) -> bool {
    if !f.visible || f.is_own_process || f.cloaked {
        return false;
    }
    if f.style & WS_CHILD != 0 {
        return false; // child windows (WinEvents also fire for them) are parts of other windows
    }
    let app_window = f.ex_style & WS_EX_APPWINDOW != 0;
    if f.ex_style & WS_EX_TOOLWINDOW != 0 && !app_window {
        return false; // tool windows / palettes
    }
    if f.ex_style & WS_EX_NOACTIVATE != 0 && !app_window {
        return false; // toasts, tooltips, overlays that never take focus
    }
    if !app_window && f.has_owner {
        return false; // dialogs belong to their owner's taskbar button
    }
    // click-through topmost overlays (screen annotation, FPS counters) are not taskbar windows
    if !app_window && f.ex_style & WS_EX_TRANSPARENT != 0 && f.ex_style & WS_EX_LAYERED != 0 && f.ex_style & WS_EX_TOPMOST != 0 {
        return false;
    }
    if is_ignored_class(&f.class_name) {
        return false;
    }
    // a window without area is not one the user can see; a minimized window legitimately reports a tiny / parked rectangle
    if !f.minimized && (f.width <= 0 || f.height <= 0) {
        return false;
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    fn base() -> WindowFacts {
        WindowFacts { visible: true, class_name: "Notepad".into(), width: 800, height: 600, ..Default::default() }
    }

    #[test]
    fn plain_windows_pass_and_overlays_do_not() {
        assert!(is_candidate(&base()));
        assert!(!is_candidate(&WindowFacts { ex_style: WS_EX_TOOLWINDOW, ..base() }));
        assert!(is_candidate(&WindowFacts { ex_style: WS_EX_TOOLWINDOW | WS_EX_APPWINDOW, ..base() }));
        assert!(!is_candidate(&WindowFacts { has_owner: true, ..base() }));
        assert!(!is_candidate(&WindowFacts { class_name: "progman".into(), ..base() }));
        assert!(!is_candidate(&WindowFacts { width: 0, ..base() }));
        assert!(is_candidate(&WindowFacts { width: 0, minimized: true, ..base() }));
        assert!(!is_candidate(&WindowFacts { cloaked: true, ..base() }));
    }
}
