//! Screenshot shortcuts (Win+Shift+S, PrtSc): while the selection overlay is up the UI draws nothing, so the bar and dock never end
//! up in a shot. The engine puts the always-on-top windows of the UI process under the overlay (and back), keeps the overlay over
//! the whole monitor, and tells the UI with `capture.changed`.

use std::collections::HashSet;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};

use fd_windows::capture::{CaptureOverlayMonitor, CaptureShortcutWatcher};
use fd_windows::{native, UiTimer, WinEventWatcher};
use serde_json::json;
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::{SetWindowPos, HWND_NOTOPMOST, HWND_TOPMOST, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, WS_EX_TOPMOST};

use crate::engine::Engine;
use crate::log;

const EVENT_OBJECT_SHOW: u32 = 0x8002;
const EVENT_OBJECT_LOCATIONCHANGE: u32 = 0x800B;

#[derive(Default)]
pub struct CaptureState {
    monitor: CaptureOverlayMonitor,
    shortcut: Mutex<Option<CaptureShortcutWatcher>>,
    timer: OnceLock<UiTimer>,
    heal_timer: OnceLock<UiTimer>,
    pending_until: Mutex<Option<Instant>>,
    hidden: std::sync::atomic::AtomicBool,
    shown_hook: Mutex<Option<WinEventWatcher>>,
    overlay_hook: Mutex<Option<WinEventWatcher>>,
    overlay_watched: Mutex<i64>,
    overlay_fixes: AtomicU32,
    lowered_ui: Mutex<Vec<i64>>,
    /// Every window that was ever put under an overlay: if one of them is found below the other windows outside a capture, it is raised again.
    top_ui: Mutex<HashSet<i64>>,
}

const FLAGS: windows::Win32::UI::WindowsAndMessaging::SET_WINDOW_POS_FLAGS =
    windows::Win32::UI::WindowsAndMessaging::SET_WINDOW_POS_FLAGS(SWP_NOMOVE.0 | SWP_NOSIZE.0 | SWP_NOACTIVATE.0);

/// A window that was put under the overlay with HWND_NOTOPMOST is not always taken back by a plain HWND_TOPMOST (the call succeeds
/// and nothing changes; seen with the dock window). Dropping the style once more first makes it stick.
fn make_topmost(h: HWND) {
    unsafe {
        let _ = SetWindowPos(h, Some(HWND_NOTOPMOST), 0, 0, 0, 0, FLAGS);
        let _ = SetWindowPos(h, Some(HWND_TOPMOST), 0, 0, 0, 0, FLAGS);
    }
}

fn is_topmost(h: HWND) -> bool {
    native::ex_style(h) & WS_EX_TOPMOST.0 as i64 != 0
}

impl Engine {
    pub fn start_capture_watch(self: &Arc<Self>) {
        if self.diagnostic {
            return;
        }
        let e = self.clone();
        let _ = self.capture.timer.set(UiTimer::new(&self.ui, move || e.capture_tick()));
        let e = self.clone();
        let _ = self.capture.heal_timer.set(UiTimer::new(&self.ui, move || e.heal_ui_windows()));
        // The hook runs on the UI-loop thread; the work is posted so the keystroke is never delayed.
        let e = self.clone();
        let watcher = CaptureShortcutWatcher::new(move || {
            let e2 = e.clone();
            e.ui.post(move || e2.begin_capture());
        });
        *self.capture.shortcut.lock().unwrap() = Some(watcher);
    }

    fn begin_capture(self: &Arc<Self>) {
        if self.shutting_down.load(Ordering::SeqCst) {
            return;
        }
        // Nothing is hidden yet: the selection tool freezes the screen first, and the bar and dock must be in that picture. They go
        // away only once its overlay is really on screen (so they never cover it).
        *self.capture.pending_until.lock().unwrap() = Some(Instant::now() + Duration::from_secs(2));
        self.capture.monitor.set_relax_topmost(true);
        if let Some(t) = self.capture.timer.get() {
            t.repeat(Duration::from_millis(30), Duration::from_millis(30));
        }
        self.watch_overlay_shown(true);
    }

    // The overlay is recognised the moment Windows shows it, not up to 30 ms later: the tool shrinks its window ~60 ms after showing
    // it, and by then the stretching hook must already be in place.
    fn watch_overlay_shown(self: &Arc<Self>, on: bool) {
        *self.capture.shown_hook.lock().unwrap() = None;
        if !on || self.shutting_down.load(Ordering::SeqCst) {
            return;
        }
        let e = self.clone();
        let watcher = WinEventWatcher::range(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, 0, move |_, hwnd| {
            if e.capture.hidden.load(Ordering::SeqCst) {
                return;
            }
            let class = native::class_name(hwnd);
            if class == "XamlWindow" || class == "SnipOverlayRootWindow" {
                e.capture_tick();
            }
        });
        *self.capture.shown_hook.lock().unwrap() = Some(watcher);
    }

    fn capture_tick(self: &Arc<Self>) {
        if self.shutting_down.load(Ordering::SeqCst) {
            return;
        }
        self.capture.monitor.scan();
        let pending = self.capture.pending_until.lock().unwrap().is_some_and(|t| Instant::now() < t);
        let active = self.capture.monitor.is_active();
        self.set_capture_hidden(active);
        self.capture.monitor.set_relax_topmost(pending);
        let timer = self.capture.timer.get();
        if !active && !pending {
            self.watch_overlay_shown(false);
            if let Some(t) = timer {
                t.stop();
            }
        } else if active {
            if let Some(t) = timer {
                t.repeat(Duration::from_millis(100), Duration::from_millis(100));
            }
        }
    }

    // ---- keeping the selection overlay over the whole monitor
    // Snipping Tool keeps shrinking its window to the work area (twice in the first 400 ms: it reacts to every move). A timer would
    // let each shrink show for a few frames; a WinEvent hook runs right after the move, so the window is back before the next frame.
    fn watch_overlay_size(self: &Arc<Self>, on: bool) {
        if self.capture.overlay_hook.lock().unwrap().take().is_some() {
            log::info(format!("capture overlay had to be stretched {} times", self.capture.overlay_fixes.load(Ordering::SeqCst)));
        }
        if !on {
            return;
        }
        let watched = self.capture.monitor.current();
        *self.capture.overlay_watched.lock().unwrap() = watched;
        if watched == 0 {
            return;
        }
        let pid = native::pid_of(native::hwnd(watched));
        self.capture.overlay_fixes.store(0, Ordering::SeqCst);
        self.fit_overlay();
        let e = self.clone();
        let watcher = WinEventWatcher::range(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, pid, move |_, hwnd| {
            if native::hwnd_id(hwnd) != watched || e.capture.overlay_fixes.load(Ordering::SeqCst) > 400 {
                return; // never fight forever
            }
            e.fit_overlay();
        });
        *self.capture.overlay_hook.lock().unwrap() = Some(watcher);
    }

    fn fit_overlay(&self) {
        if self.capture.monitor.fit_overlay_to_monitor() {
            let n = self.capture.overlay_fixes.fetch_add(1, Ordering::SeqCst) + 1;
            log::info(format!("capture overlay stretched over the whole monitor (#{n})"));
        }
    }

    fn heal_ui_windows(&self) {
        if self.shutting_down.load(Ordering::SeqCst) || self.capture.hidden.load(Ordering::SeqCst) {
            return;
        }
        let ids: Vec<i64> = self.capture.top_ui.lock().unwrap().iter().copied().collect();
        if ids.is_empty() {
            return;
        }
        for id in ids {
            let h = native::hwnd(id);
            if !native::is_window(h) {
                self.capture.top_ui.lock().unwrap().remove(&id);
                continue;
            }
            if is_topmost(h) {
                continue;
            }
            log::info(format!("window {id:X} lost its always-on-top state: raised again"));
            make_topmost(h);
        }
    }

    /// Puts the always-on-top windows of the UI process under the selection overlay (and back). Done here, in the engine, so it does
    /// not depend on the UI process getting the event in time: the overlay would otherwise have our bar and dock above it.
    fn place_ui_windows(self: &Arc<Self>, under_overlay: bool) {
        if under_overlay {
            let mut lowered = Vec::new();
            native::enum_windows(|h| {
                if native::class_name(h) == "FloatingDock.UiWindow" && is_topmost(h) {
                    lowered.push(native::hwnd_id(h));
                }
                true
            });
            for id in &lowered {
                self.capture.top_ui.lock().unwrap().insert(*id);
                unsafe {
                    let _ = SetWindowPos(native::hwnd(*id), Some(HWND_NOTOPMOST), 0, 0, 0, 0, FLAGS);
                }
            }
            *self.capture.lowered_ui.lock().unwrap() = lowered;
            if let Some(t) = self.capture.heal_timer.get() {
                t.repeat(Duration::from_millis(700), Duration::from_millis(700));
            }
        } else {
            let raised = std::mem::take(&mut *self.capture.lowered_ui.lock().unwrap());
            for id in &raised {
                if native::is_window(native::hwnd(*id)) {
                    make_topmost(native::hwnd(*id));
                }
            }
            // A window sometimes does not take the style at once (the dock window, while the overlay is still closing): try
            // again every 30 ms until all are up.
            self.retry_raise(raised, 1);
        }
    }

    fn retry_raise(self: &Arc<Self>, windows: Vec<i64>, attempt: u32) {
        if self.capture.hidden.load(Ordering::SeqCst) {
            return; // another capture started: the windows belong under it again
        }
        let all_up = windows.iter().all(|id| !native::is_window(native::hwnd(*id)) || is_topmost(native::hwnd(*id)));
        if all_up || attempt > 20 {
            if attempt > 1 {
                log::info(format!("raised the windows again after {} retries", attempt - 1));
            }
            return;
        }
        for id in &windows {
            let h = native::hwnd(*id);
            if native::is_window(h) && !is_topmost(h) {
                log::info(format!("raising window {id:X} again: it was not on top after the capture"));
                make_topmost(h);
            }
        }
        let e = self.clone();
        self.ui.post_after(Duration::from_millis(30), move || e.retry_raise(windows, attempt + 1));
    }

    fn set_capture_hidden(self: &Arc<Self>, hidden: bool) {
        if self.capture.hidden.swap(hidden, Ordering::SeqCst) == hidden {
            return;
        }
        log::info(if hidden { "capture overlay detected: bar and dock go below it" } else { "capture overlay gone" });
        self.place_ui_windows(hidden);
        self.watch_overlay_size(hidden);
        self.server.publish("capture.changed", json!({ "hidden": hidden }));
    }

    pub fn dispose_capture(&self) {
        *self.capture.shortcut.lock().unwrap() = None;
        *self.capture.shown_hook.lock().unwrap() = None;
        *self.capture.overlay_hook.lock().unwrap() = None;
        if let Some(t) = self.capture.timer.get() {
            t.stop();
        }
        if let Some(t) = self.capture.heal_timer.get() {
            t.stop();
        }
    }
}
