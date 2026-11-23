//! Screenshot shortcuts (Win+Shift+S, PrtSc): the watcher sees the keystroke without consuming it, the monitor recognises the
//! selection overlay of the Windows capture tools among the top-level windows.

use std::collections::HashSet;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

use fd_core::win_path;
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::Graphics::Gdi::{MonitorFromWindow, MONITOR_DEFAULTTONEAREST};
use windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, SetWindowPos, SetWindowsHookExW, UnhookWindowsHookEx, HHOOK, KBDLLHOOKSTRUCT, SWP_NOACTIVATE, SWP_NOZORDER, WH_KEYBOARD_LL,
    WM_KEYDOWN, WM_SYSKEYDOWN, WS_CAPTION, WS_EX_TOPMOST,
};

use crate::{display, native};

const SWP_NOCOPYBITS: u32 = 0x100;
const SWP_NOSENDCHANGING: u32 = 0x400;

/// The selection tool of Windows: a window of one of the capture programs that covers the work area (it is fitted to it once a
/// bar or dock reserve space), is topmost (unless `relax_topmost`), not framed (except the new Snipping Tool's XAML window).
pub fn is_selection_overlay_policy(executable: Option<&str>, visible: bool, cloaked: bool, framed: bool, topmost: bool, covers_monitor: bool, window_class: Option<&str>) -> bool {
    let Some(exe) = executable else { return false };
    if !visible || cloaked || !topmost || !covers_monitor {
        return false;
    }
    let name = win_path::file_name(exe);
    // The new Snipping Tool uses a full-monitor XamlWindow with WS_CAPTION still set.
    let modern = name.eq_ignore_ascii_case("SnippingTool.exe") && matches!(window_class, Some("XamlWindow") | Some("SnipOverlayRootWindow"));
    if framed && !modern {
        return false;
    }
    ["ScreenClippingHost.exe", "SnippingTool.exe", "ScreenSketch.exe"].iter().any(|n| name.eq_ignore_ascii_case(n))
}

pub fn is_selection_overlay(hwnd: HWND, relax_topmost: bool) -> bool {
    if hwnd.0.is_null() || !native::is_visible(hwnd) || native::is_iconic(hwnd) {
        return false;
    }
    let topmost = native::ex_style(hwnd) & WS_EX_TOPMOST.0 as i64 != 0;
    if !topmost && !relax_topmost {
        return false;
    }
    let framed = native::style(hwnd) & WS_CAPTION.0 as i64 != 0;
    let class = native::class_name(hwnd);
    if framed && class != "XamlWindow" && class != "SnipOverlayRootWindow" {
        return false;
    }
    if native::is_cloaked(hwnd) {
        return false;
    }
    let Some(bounds) = native::window_rect(hwnd) else { return false };
    let monitor = unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) };
    let Some(m) = display::describe(monitor) else { return false };
    // Windows fits the selection window to the work area once the bar or dock reserve space (observed: 0,44,1920,1013 on a
    // 1920x1080 screen), so covering the work area is enough; otherwise the overlay would be "gone" a moment after it appeared.
    let slack = 2;
    let area = m.work;
    let covers = bounds.left <= area.left + slack && bounds.top <= area.top + slack && bounds.right >= area.right - slack && bounds.bottom >= area.bottom - slack;
    if !covers {
        return false;
    }
    let exe = native::exe_path(native::pid_of(hwnd));
    is_selection_overlay_policy(exe.as_deref(), true, false, framed, true, covers, Some(&class))
}

/// Tracks visible Windows capture overlays. The ordinary screenshot editor is not an overlay.
#[derive(Default)]
pub struct CaptureOverlayMonitor {
    overlays: Mutex<Vec<i64>>,
    relax_topmost: AtomicBool,
}

impl CaptureOverlayMonitor {
    pub fn is_active(&self) -> bool {
        !self.overlays.lock().unwrap().is_empty()
    }

    /// The window of the selection overlay that was seen last (0 when there is none).
    pub fn current(&self) -> i64 {
        self.overlays.lock().unwrap().first().copied().unwrap_or(0)
    }

    /// While a screenshot shortcut was just pressed the selection overlay may not carry the topmost style (it lives in a shell band).
    pub fn set_relax_topmost(&self, relax: bool) {
        self.relax_topmost.store(relax, Ordering::SeqCst);
    }

    pub fn scan(&self) {
        let relax = self.relax_topmost.load(Ordering::SeqCst);
        let mut found: Vec<i64> = Vec::new();
        native::enum_windows(|h| {
            if is_selection_overlay(h, relax) {
                found.push(native::hwnd_id(h));
            }
            true
        });
        let mut seen = HashSet::new();
        found.retain(|h| seen.insert(*h));
        *self.overlays.lock().unwrap() = found;
    }

    /// The selection overlay is fitted to the work area, so with a bar or dock reserving space it shrinks and shows the frozen screen
    /// scaled down inside, with our live bar and dock outside it. Stretches it over the whole monitor. True when the window moved.
    pub fn fit_overlay_to_monitor(&self) -> bool {
        let id = self.current();
        let hwnd = native::hwnd(id);
        if id == 0 {
            return false;
        }
        let Some(bounds) = native::window_rect(hwnd) else { return false };
        let monitor = unsafe { MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) };
        let Some(m) = display::describe(monitor) else { return false };
        let b = m.bounds;
        if bounds.left == b.left && bounds.top == b.top && bounds.right == b.right && bounds.bottom == b.bottom {
            return false;
        }
        unsafe {
            SetWindowPos(
                hwnd,
                None,
                b.left,
                b.top,
                b.right - b.left,
                b.bottom - b.top,
                SWP_NOZORDER | SWP_NOACTIVATE | windows::Win32::UI::WindowsAndMessaging::SET_WINDOW_POS_FLAGS(SWP_NOSENDCHANGING | SWP_NOCOPYBITS),
            )
            .is_ok()
        }
    }
}

// ------------------------------------------------------------------------------------------------ shortcut watcher

type Callback = Arc<dyn Fn() + Send + Sync>;

fn callback_slot() -> &'static Mutex<Option<Callback>> {
    static S: OnceLock<Mutex<Option<Callback>>> = OnceLock::new();
    S.get_or_init(|| Mutex::new(None))
}

static HOOK: Mutex<isize> = Mutex::new(0);

/// A key that starts a screenshot: PrtSc, or S with the Windows key and Shift.
pub fn is_capture_shortcut(key: u32, windows: bool, shift: bool) -> bool {
    key == 0x2C || (key == 0x53 && windows && shift)
}

unsafe extern "system" fn on_key(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code >= 0 && (wparam.0 as u32 == WM_KEYDOWN || wparam.0 as u32 == WM_SYSKEYDOWN) {
        let key = (*(lparam.0 as *const KBDLLHOOKSTRUCT)).vkCode;
        let down = |vk: i32| GetAsyncKeyState(vk) as u16 & 0x8000 != 0;
        if is_capture_shortcut(key, down(0x5B) || down(0x5C), down(0x10)) {
            let cb = callback_slot().lock().ok().and_then(|c| c.clone());
            if let Some(cb) = cb {
                let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| cb())); // always pass the user's shortcut through
            }
        }
    }
    let hook = *HOOK.lock().unwrap();
    CallNextHookEx(Some(HHOOK(hook as *mut std::ffi::c_void)), code, wparam, lparam)
}

/// Observes screenshot shortcuts without consuming or injecting keyboard input. Owns no polling timer. Must be created on the
/// UI-loop thread (the hook is delivered through its message loop).
pub struct CaptureShortcutWatcher {
    installed: bool,
}

impl CaptureShortcutWatcher {
    pub fn new(capture: impl Fn() + Send + Sync + 'static) -> CaptureShortcutWatcher {
        *callback_slot().lock().unwrap() = Some(Arc::new(capture));
        let hook = unsafe {
            let inst = windows::Win32::System::LibraryLoader::GetModuleHandleW(None).ok();
            SetWindowsHookExW(WH_KEYBOARD_LL, Some(on_key), inst.map(|i| i.into()), 0)
        };
        match hook {
            Ok(h) => {
                *HOOK.lock().unwrap() = h.0 as isize;
                CaptureShortcutWatcher { installed: true }
            }
            Err(_) => CaptureShortcutWatcher { installed: false },
        }
    }
}

impl Drop for CaptureShortcutWatcher {
    fn drop(&mut self) {
        if self.installed {
            let h = std::mem::take(&mut *HOOK.lock().unwrap());
            if h != 0 {
                unsafe {
                    let _ = UnhookWindowsHookEx(HHOOK(h as *mut std::ffi::c_void));
                }
            }
            *callback_slot().lock().unwrap() = None;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn policy_accepts_only_the_capture_tools() {
        let ok = |exe: &str, framed: bool, class: Option<&str>| is_selection_overlay_policy(Some(exe), true, false, framed, true, true, class);
        assert!(ok(r"C:\Windows\System32\ScreenClippingHost.exe", false, None));
        assert!(ok(r"C:\x\SnippingTool.exe", true, Some("XamlWindow")), "the new tool keeps its caption");
        assert!(!ok(r"C:\x\SnippingTool.exe", true, Some("Other")));
        assert!(!ok(r"C:\x\notepad.exe", false, None));
        assert!(!is_selection_overlay_policy(Some(r"C:\x\ScreenSketch.exe"), true, false, false, false, true, None), "needs topmost");
        assert!(!is_selection_overlay_policy(None, true, false, false, true, true, None));
    }

    #[test]
    fn shortcuts() {
        assert!(is_capture_shortcut(0x2C, false, false));
        assert!(is_capture_shortcut(0x53, true, true));
        assert!(!is_capture_shortcut(0x53, true, false));
    }
}
