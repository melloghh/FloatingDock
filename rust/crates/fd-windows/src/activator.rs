//! Brings application windows to the front (restoring minimized ones). Uses only standard window operations; never touches
//! Windows' own animations.

use std::time::Duration;

use fd_core::WindowInfo;
use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows::Win32::System::Threading::{AttachThreadInput, GetCurrentThreadId};
use windows::Win32::UI::Input::KeyboardAndMouse::IsWindowEnabled;
use windows::Win32::UI::WindowsAndMessaging::{
    BringWindowToTop, GetAncestor, GetLastActivePopup, PostMessageW, SetForegroundWindow, ShowWindow, ShowWindowAsync, SwitchToThisWindow,
    GA_ROOTOWNER, SW_MINIMIZE, SW_RESTORE, WM_CLOSE,
};

use crate::native;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ActivationResult {
    Activated,
    Minimized,
    NoLiveWindow,
    Denied,
}

enum Plan {
    Done(ActivationResult),
    Activate(i64),
}

/// Everything that touches an HWND happens in these synchronous steps: an HWND is not `Send` and must not live across an await.
fn plan(windows: &[WindowInfo], minimize_if_active: bool) -> Plan {
    let live: std::collections::HashSet<i64> = windows.iter().filter(|w| is_live(w)).map(|w| w.hwnd).collect();
    if live.is_empty() {
        return Plan::Done(ActivationResult::NoLiveWindow);
    }
    let root = native::hwnd_id(native::root_owner(native::foreground()));
    if minimize_if_active && live.contains(&root) && !native::is_iconic(native::hwnd(root)) {
        unsafe {
            let _ = ShowWindowAsync(native::hwnd(root), SW_MINIMIZE);
        }
        return Plan::Done(ActivationResult::Minimized);
    }
    let mut first = 0i64;
    native::enum_windows(|h| {
        if live.contains(&native::hwnd_id(h)) {
            first = native::hwnd_id(h);
            return false;
        }
        true
    });
    if first == 0 {
        return Plan::Done(ActivationResult::NoLiveWindow);
    }
    let mut target = native::hwnd(first);
    // An owned dialog, rather than its disabled owner, is the usable window.
    for _ in 0..8 {
        let popup = unsafe { GetLastActivePopup(target) };
        if popup.0.is_null() || popup == target || !native::is_visible(popup) {
            break;
        }
        target = popup;
        if unsafe { IsWindowEnabled(target).as_bool() } {
            break;
        }
    }
    Plan::Activate(native::hwnd_id(target))
}

/// One activation try; false when the window is gone.
fn attempt(id: i64, round: u32) -> bool {
    let t = native::hwnd(id);
    if !native::is_window(t) {
        return false;
    }
    activate(t);
    if round == 1 && (native::foreground() != t || native::is_iconic(t)) {
        // Compatibility fallback for windows whose higher-integrity thread rejects input attachment (e.g. Task Manager).
        unsafe { SwitchToThisWindow(t, true) };
    }
    true
}

fn is_active(id: i64) -> bool {
    let t = native::hwnd(id);
    let active = native::foreground();
    !native::is_iconic(t) && (active == t || unsafe { GetAncestor(active, GA_ROOTOWNER) } == t)
}

/// Verifies current HWND/PID, restores asynchronously and activates a modal popup when its owner is disabled; the result is
/// verified - never reports activation merely because an API was called.
pub async fn activate_or_minimize(windows: &[WindowInfo], minimize_if_active: bool) -> ActivationResult {
    let id = match plan(windows, minimize_if_active) {
        Plan::Done(r) => return r,
        Plan::Activate(id) => id,
    };
    for round in 0..4 {
        if !attempt(id, round) {
            return ActivationResult::NoLiveWindow;
        }
        tokio::time::sleep(Duration::from_millis(65)).await;
        if is_active(id) {
            return ActivationResult::Activated;
        }
    }
    ActivationResult::Denied
}

pub fn is_live(window: &WindowInfo) -> bool {
    let h = native::hwnd(window.hwnd);
    if !native::is_window(h) || !native::is_visible(h) {
        return false;
    }
    let pid = native::pid_of(h);
    if pid as i32 != window.process_id {
        // Packaged apps can be hosted by ApplicationFrameHost: the snapshot PID is the app's child CoreWindow.
        if native::class_name(h) != "ApplicationFrameWindow" {
            return false;
        }
        let mut child_matches = false;
        native::enum_children(h, |c| {
            if native::pid_of(c) as i32 == window.process_id {
                child_matches = true;
            }
            !child_matches
        });
        if !child_matches {
            return false;
        }
    }
    !native::is_cloaked(h)
}

/// Brings one window to the front, restoring it when minimized.
pub fn activate(hwnd: HWND) {
    if !native::is_window(hwnd) {
        return;
    }
    if native::is_iconic(hwnd) {
        unsafe {
            let _ = ShowWindow(hwnd, SW_RESTORE);
        }
    }
    // Cross-input-queue activation is asynchronous. A successful request can precede GetForegroundWindow changing; the async
    // caller verifies it. Attaching input here would turn that normal delay into a UI-thread stall.
    if unsafe { SetForegroundWindow(hwnd).as_bool() } {
        return;
    }
    // Foreground lock: attach to the current foreground thread so SetForegroundWindow is allowed.
    let fg = native::foreground();
    let fg_thread = if fg.0.is_null() { 0 } else { native::thread_and_pid(fg).0 };
    let me = unsafe { GetCurrentThreadId() };
    let target_thread = native::thread_and_pid(hwnd).0;
    unsafe {
        let attached = fg_thread != 0 && fg_thread != me && AttachThreadInput(me, fg_thread, true).as_bool();
        let target_attached = target_thread != me && target_thread != fg_thread && AttachThreadInput(me, target_thread, true).as_bool();
        let _ = BringWindowToTop(hwnd);
        let _ = SetForegroundWindow(hwnd);
        if target_attached {
            let _ = AttachThreadInput(me, target_thread, false);
        }
        if attached {
            let _ = AttachThreadInput(me, fg_thread, false);
        }
    }
}

/// The standard close request (WM_CLOSE), exactly like the taskbar's "Close window".
pub fn request_close(windows: &[WindowInfo]) {
    for w in windows {
        post_close(w.hwnd);
    }
}

pub fn post_close(hwnd: i64) {
    unsafe {
        let _ = PostMessageW(Some(native::hwnd(hwnd)), WM_CLOSE, WPARAM(0), LPARAM(0));
    }
}
