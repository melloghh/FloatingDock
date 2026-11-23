//! Decides whether a top-level window is something the Windows taskbar would show. The rules live in
//! `fd_core::window_filter_rules`; this module only collects the facts.

use std::sync::{Arc, RwLock};

use fd_core::window_filter_rules::{self, WindowFacts};
use windows::Win32::Foundation::HWND;

use crate::native;

type OwnProcessCheck = Arc<dyn Fn(u32) -> bool + Send + Sync>;

static EXTRA_OWN_PROCESS: RwLock<Option<OwnProcessCheck>> = RwLock::new(None);

const IGNORED_EXES: [&str; 6] =
    ["searchhost.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe", "textinputhost.exe", "searchui.exe", "shellhost.exe"];

/// Other processes of this product (the UI) whose windows must not appear as apps.
pub fn set_extra_own_process(check: impl Fn(u32) -> bool + Send + Sync + 'static) {
    *EXTRA_OWN_PROCESS.write().unwrap() = Some(Arc::new(check));
}

pub fn is_ignored_exe(exe_path: Option<&str>) -> bool {
    exe_path.is_some_and(|p| {
        let name = fd_core::win_path::file_name(p).to_lowercase();
        IGNORED_EXES.contains(&name.as_str())
    })
}

/// Cheap checks that need no process access.
pub fn is_candidate(hwnd: HWND, own_pid: u32) -> bool {
    if hwnd.0.is_null() || !native::is_window(hwnd) || !native::is_visible(hwnd) {
        return false;
    }
    let pid = native::pid_of(hwnd);
    let minimized = native::is_iconic(hwnd);
    let Some(r) = native::window_rect(hwnd) else { return false };
    let extra = EXTRA_OWN_PROCESS.read().unwrap().clone();
    let facts = WindowFacts {
        visible: true,
        style: native::style(hwnd),
        ex_style: native::ex_style(hwnd),
        has_owner: native::has_owner(hwnd),
        cloaked: native::is_cloaked(hwnd),
        class_name: native::class_name(hwnd),
        is_own_process: own_pid != 0 && (pid == own_pid || extra.is_some_and(|f| f(pid))),
        width: r.right - r.left,
        height: r.bottom - r.top,
        minimized,
    };
    window_filter_rules::is_candidate(&facts)
}

pub fn enumerate_candidates(own_pid: u32) -> Vec<HWND> {
    let mut list = Vec::new();
    native::enum_windows(|h| {
        if is_candidate(h, own_pid) {
            list.push(h);
        }
        true
    });
    list
}
