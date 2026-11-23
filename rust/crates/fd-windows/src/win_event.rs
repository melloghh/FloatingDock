//! Event-driven window notifications (`SetWinEventHook`). Must be created on a thread with a message loop (the UI thread); no polling.

use std::collections::HashMap;
use std::ffi::c_void;
use std::sync::{Arc, Mutex, OnceLock};

use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Accessibility::{SetWinEventHook, UnhookWinEvent, HWINEVENTHOOK};
use windows::Win32::UI::WindowsAndMessaging::{
    EVENT_OBJECT_CLOAKED, EVENT_OBJECT_CREATE, EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, EVENT_OBJECT_LOCATIONCHANGE,
    EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_SHOW, EVENT_OBJECT_UNCLOAKED, EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND,
    EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZESTART, OBJID_WINDOW, WINEVENT_OUTOFCONTEXT,
    WINEVENT_SKIPOWNPROCESS,
};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WindowEventKind {
    Created,
    Destroyed,
    Shown,
    Hidden,
    Renamed,
    Cloaked,
    Uncloaked,
    Foreground,
    MinimizeStart,
    MinimizeEnd,
    LocationChanged,
    MoveStart,
    MoveEnd,
}

type Callback = Arc<dyn Fn(WindowEventKind, HWND) + Send + Sync>;

fn registry() -> &'static Mutex<HashMap<isize, Callback>> {
    static R: OnceLock<Mutex<HashMap<isize, Callback>>> = OnceLock::new();
    R.get_or_init(|| Mutex::new(HashMap::new()))
}

pub struct WinEventWatcher {
    hooks: Vec<isize>,
}

unsafe extern "system" fn on_event(hook: HWINEVENTHOOK, event: u32, hwnd: HWND, id_object: i32, id_child: i32, _thread: u32, _time: u32) {
    if hwnd.0.is_null() {
        return;
    }
    let whole_window_events = matches!(
        event,
        EVENT_SYSTEM_FOREGROUND | EVENT_SYSTEM_MINIMIZESTART | EVENT_SYSTEM_MINIMIZEEND | EVENT_SYSTEM_MOVESIZESTART | EVENT_SYSTEM_MOVESIZEEND
    );
    if !whole_window_events && (id_object != OBJID_WINDOW.0 || id_child != 0) {
        return; // only whole-window events
    }
    let kind = match event {
        EVENT_OBJECT_CREATE => WindowEventKind::Created,
        EVENT_OBJECT_DESTROY => WindowEventKind::Destroyed,
        EVENT_OBJECT_SHOW => WindowEventKind::Shown,
        EVENT_OBJECT_HIDE => WindowEventKind::Hidden,
        EVENT_OBJECT_NAMECHANGE => WindowEventKind::Renamed,
        EVENT_OBJECT_LOCATIONCHANGE => WindowEventKind::LocationChanged,
        EVENT_SYSTEM_MOVESIZESTART => WindowEventKind::MoveStart,
        EVENT_SYSTEM_MOVESIZEEND => WindowEventKind::MoveEnd,
        EVENT_OBJECT_CLOAKED => WindowEventKind::Cloaked,
        EVENT_OBJECT_UNCLOAKED => WindowEventKind::Uncloaked,
        EVENT_SYSTEM_FOREGROUND => WindowEventKind::Foreground,
        EVENT_SYSTEM_MINIMIZESTART => WindowEventKind::MinimizeStart,
        EVENT_SYSTEM_MINIMIZEEND => WindowEventKind::MinimizeEnd,
        _ => return,
    };
    let cb = registry().lock().ok().and_then(|r| r.get(&(hook.0 as isize)).cloned());
    if let Some(cb) = cb {
        let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| cb(kind, hwnd)));
    }
}

impl WinEventWatcher {
    /// `process_id == 0` watches every process. `include_own` also delivers this process's own windows.
    pub fn new(process_id: u32, include_own: bool, callback: impl Fn(WindowEventKind, HWND) + Send + Sync + 'static) -> WinEventWatcher {
        let cb: Callback = Arc::new(callback);
        let flags = WINEVENT_OUTOFCONTEXT | if include_own { 0 } else { WINEVENT_SKIPOWNPROCESS };
        let mut hooks = Vec::new();
        let ranges = [
            (EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE), // create, destroy, show, hide
            (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_NAMECHANGE),
            (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
            (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
            (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
            (EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND),
        ];
        for (min, max) in ranges {
            let h = unsafe { SetWinEventHook(min, max, None, Some(on_event), process_id, 0, flags) };
            if !h.0.is_null() {
                registry().lock().unwrap().insert(h.0 as isize, cb.clone());
                hooks.push(h.0 as isize);
            }
        }
        WinEventWatcher { hooks }
    }
}

impl WinEventWatcher {
    /// One event range (for example only EVENT_OBJECT_SHOW), one process (`0` = all), own windows included.
    pub fn range(min: u32, max: u32, process_id: u32, callback: impl Fn(WindowEventKind, HWND) + Send + Sync + 'static) -> WinEventWatcher {
        let cb: Callback = Arc::new(callback);
        let mut hooks = Vec::new();
        let h = unsafe { SetWinEventHook(min, max, None, Some(on_event), process_id, 0, WINEVENT_OUTOFCONTEXT) };
        if !h.0.is_null() {
            registry().lock().unwrap().insert(h.0 as isize, cb);
            hooks.push(h.0 as isize);
        }
        WinEventWatcher { hooks }
    }
}

impl Drop for WinEventWatcher {
    fn drop(&mut self) {
        for h in self.hooks.drain(..) {
            registry().lock().unwrap().remove(&h);
            unsafe {
                let _ = UnhookWinEvent(HWINEVENTHOOK(h as *mut c_void));
            }
        }
    }
}
