//! Keeps the set of taskbar-eligible windows up to date from WinEvent notifications. A window whose identity is not ready yet
//! is looked at again a limited number of times (0.1 / 0.3 / 0.8 / 2 s), never forever. A slow safety-net scan (every 30 s)
//! drops windows whose destroy event was missed. All state is touched on the UI-loop thread; Shell queries run on the
//! [`ShellWorker`].

use std::collections::{HashMap, HashSet};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use fd_core::{Event, WindowInfo};
use indexmap::IndexMap;
use windows::Win32::Foundation::HWND;

use crate::identity::{self, Resolved};
use crate::timer::UiTimer;
use crate::ui_loop::UiLoop;
use crate::win_event::{WinEventWatcher, WindowEventKind};
use crate::{native, window_filter, ShellWorker};

const RETRY_DELAYS_MS: [u64; 4] = [100, 300, 800, 2000];
/// Upper bound of re-resolutions of one window whose identity stays incomplete.
const MAX_INCOMPLETE_CHECKS: u32 = 10;

#[derive(Default)]
struct State {
    windows: IndexMap<i64, WindowInfo>,
    included_own: HashSet<i64>,
    in_flight: HashSet<i64>,
    incomplete: HashSet<i64>,
    incomplete_checks: HashMap<i64, u32>,
    foreground: i64,
    change_pending: bool,
    watcher: Option<WinEventWatcher>,
    own_watcher: Option<WinEventWatcher>,
}

pub struct RunningApps {
    worker: Arc<ShellWorker>,
    ui: Arc<UiLoop>,
    own_pid: u32,
    state: Mutex<State>,
    disposed: AtomicBool,
    safety_net: Mutex<Option<UiTimer>>,
    /// Windows and the root window that is in the foreground. Raised on the UI thread.
    pub changed: Event<(Vec<WindowInfo>, i64)>,
    /// Every native window event (capture overlays are often tool windows and intentionally absent from the app list).
    pub native_window_changed: Event<(WindowEventKind, i64)>,
}

impl RunningApps {
    pub fn new(worker: Arc<ShellWorker>, ui: Arc<UiLoop>) -> Arc<RunningApps> {
        Arc::new(RunningApps {
            worker,
            ui,
            own_pid: std::process::id(),
            state: Mutex::new(State::default()),
            disposed: AtomicBool::new(false),
            safety_net: Mutex::new(None),
            changed: Event::new(),
            native_window_changed: Event::new(),
        })
    }

    pub fn snapshot(&self) -> Vec<WindowInfo> {
        self.state.lock().unwrap().windows.values().cloned().collect()
    }

    pub fn foreground_root(&self) -> i64 {
        self.state.lock().unwrap().foreground
    }

    pub fn start(self: &Arc<Self>) {
        let me = self.clone();
        let watcher = WinEventWatcher::new(0, false, move |kind, h| me.on_window_event(kind, native::hwnd_id(h)));
        self.state.lock().unwrap().watcher = Some(watcher);
        self.full_scan();
        let me = self.clone();
        let timer = UiTimer::new(&self.ui, move || me.full_scan());
        timer.repeat(Duration::from_secs(30), Duration::from_secs(30));
        *self.safety_net.lock().unwrap() = Some(timer);
    }

    /// Lets a window of this product (the UI's settings window) show up as an app.
    pub fn include_own_window(self: &Arc<Self>, id: i64) {
        let need_watcher = {
            let mut st = self.state.lock().unwrap();
            st.included_own.insert(id);
            st.own_watcher.is_none()
        };
        if need_watcher {
            // The included window may belong to another process of this product (the UI), so watch that process.
            let pid = native::pid_of(native::hwnd(id));
            let me = self.clone();
            let w = WinEventWatcher::new(if pid != 0 { pid } else { self.own_pid }, true, move |kind, h| {
                let hid = native::hwnd_id(h);
                if me.state.lock().unwrap().included_own.contains(&hid) {
                    me.on_window_event(kind, hid);
                }
            });
            self.state.lock().unwrap().own_watcher = Some(w);
        }
        self.evaluate(id, 0, false);
        if self.update_foreground() {
            self.schedule_changed();
        }
    }

    pub fn exclude_own_window(self: &Arc<Self>, id: i64) {
        let (removed, none_left) = {
            let mut st = self.state.lock().unwrap();
            st.included_own.remove(&id);
            let removed = st.windows.shift_remove(&id).is_some();
            if removed {
                st.incomplete.remove(&id);
                st.incomplete_checks.remove(&id);
            }
            let none_left = st.included_own.is_empty();
            if none_left {
                st.own_watcher = None;
            }
            (removed, none_left)
        };
        let _ = none_left;
        if removed {
            self.schedule_changed();
        }
    }

    fn is_candidate(&self, id: i64) -> bool {
        let own = if self.state.lock().unwrap().included_own.contains(&id) { 0 } else { self.own_pid };
        window_filter::is_candidate(native::hwnd(id), own)
    }

    pub fn full_scan(self: &Arc<Self>) {
        if self.disposed.load(Ordering::SeqCst) {
            return;
        }
        let mut current: Vec<i64> = window_filter::enumerate_candidates(self.own_pid).into_iter().map(native::hwnd_id).collect();
        let included: Vec<i64> = self.state.lock().unwrap().included_own.iter().copied().collect();
        for id in included {
            if self.is_candidate(id) {
                current.push(id);
            }
        }
        let current_set: HashSet<i64> = current.iter().copied().collect();
        let removed = {
            let mut st = self.state.lock().unwrap();
            let gone: Vec<i64> = st.windows.keys().filter(|k| !current_set.contains(k)).copied().collect();
            for g in &gone {
                st.windows.shift_remove(g);
                st.incomplete.remove(g);
                st.incomplete_checks.remove(g);
            }
            !gone.is_empty()
        };
        for id in current {
            let known = self.state.lock().unwrap().windows.contains_key(&id);
            if !known {
                self.evaluate(id, 0, false);
            }
        }
        if self.update_foreground() || removed {
            self.schedule_changed();
        }
    }

    fn on_window_event(self: &Arc<Self>, kind: WindowEventKind, id: i64) {
        if self.disposed.load(Ordering::SeqCst) {
            return;
        }
        self.native_window_changed.emit(&(kind, id));
        match kind {
            // Only overlay/fullscreen tracking needs geometry events, never redo shell identity for a moving window.
            WindowEventKind::LocationChanged | WindowEventKind::MoveStart | WindowEventKind::MoveEnd => {}
            WindowEventKind::Destroyed | WindowEventKind::Cloaked => {
                if self.remove_window(id) {
                    self.schedule_changed();
                }
            }
            WindowEventKind::Foreground => {
                if self.update_foreground() {
                    self.schedule_changed();
                }
                if !self.try_reidentify(id) {
                    self.evaluate(id, 0, false); // a window that became foreground may be new to us, or unidentified until now
                }
            }
            WindowEventKind::MinimizeStart | WindowEventKind::MinimizeEnd => {
                let minimized = native::is_iconic(native::hwnd(id));
                let changed = {
                    let mut st = self.state.lock().unwrap();
                    match st.windows.get_mut(&id) {
                        Some(w) => {
                            w.is_minimized = minimized;
                            true
                        }
                        None => false,
                    }
                };
                if changed {
                    self.schedule_changed();
                }
            }
            WindowEventKind::Renamed => {
                let known = self.state.lock().unwrap().windows.contains_key(&id);
                if known && !self.try_reidentify(id) {
                    // Browser/media titles change often. Identity is already resolved: do not reopen the process.
                    let title = native::title(native::hwnd(id));
                    let changed = {
                        let mut st = self.state.lock().unwrap();
                        match st.windows.get_mut(&id) {
                            Some(w) if w.title.as_deref() != Some(title.as_str()) => {
                                w.title = Some(title);
                                true
                            }
                            _ => false,
                        }
                    };
                    if changed {
                        self.schedule_changed();
                    }
                }
            }
            _ => {
                // created / shown / hidden / uncloaked
                if !self.try_reidentify(id) {
                    self.evaluate(id, 0, false);
                }
            }
        }
    }

    fn remove_window(&self, id: i64) -> bool {
        let mut st = self.state.lock().unwrap();
        st.incomplete.remove(&id);
        st.incomplete_checks.remove(&id);
        st.windows.shift_remove(&id).is_some()
    }

    /// An event for a window whose identity is still incomplete triggers one more resolution - bounded per window.
    fn try_reidentify(self: &Arc<Self>, id: i64) -> bool {
        let give_up = {
            let st = self.state.lock().unwrap();
            if !st.incomplete.contains(&id) {
                return false;
            }
            st.incomplete_checks.get(&id).copied().unwrap_or(0) >= MAX_INCOMPLETE_CHECKS
        };
        if give_up {
            return true; // keep what we have, do not poll
        }
        self.evaluate(id, RETRY_DELAYS_MS.len(), true); // attempt == len: no timer chain, the event flow drives further tries
        true
    }

    fn update_foreground(&self) -> bool {
        let fg = native::foreground();
        let mut root = 0i64;
        if !fg.0.is_null() {
            root = native::hwnd_id(native::root_owner(fg));
        }
        let mut st = self.state.lock().unwrap();
        if !fg.0.is_null() && !st.windows.contains_key(&root) && st.windows.contains_key(&native::hwnd_id(fg)) {
            root = native::hwnd_id(fg); // the foreground window may be an owned dialog of a known window
        }
        if root == st.foreground {
            return false;
        }
        st.foreground = root;
        true
    }

    /// `refresh`: re-resolve even if the window is already known (retry of an incomplete identity).
    fn evaluate(self: &Arc<Self>, id: i64, attempt: usize, refresh: bool) {
        if !self.is_candidate(id) {
            if self.remove_window(id) {
                self.schedule_changed();
            }
            return;
        }
        {
            let mut st = self.state.lock().unwrap();
            if !refresh && st.windows.contains_key(&id) {
                return; // already known
            }
            if !st.in_flight.insert(id) {
                return;
            }
        }
        // HWND ownership belongs to the frame process. The resolver may return a different logical PID for the app hosted inside.
        let owner_pid = native::pid_of(native::hwnd(id));
        let me = self.clone();
        let _ = self.worker.run(move || {
            let resolved = identity::resolve(native::hwnd(id));
            let ui = me.ui.clone();
            ui.post(move || me.on_resolved(id, attempt, owner_pid, resolved));
        });
    }

    fn on_resolved(self: &Arc<Self>, id: i64, attempt: usize, owner_pid: u32, res: Resolved) {
        self.state.lock().unwrap().in_flight.remove(&id);
        if self.disposed.load(Ordering::SeqCst) {
            return;
        }
        let current_pid = native::pid_of(native::hwnd(id));
        if current_pid != owner_pid {
            self.evaluate(id, 0, true);
            return;
        }
        if !self.is_candidate(id) || window_filter::is_ignored_exe(res.info.exe_path.as_deref()) {
            if self.remove_window(id) {
                self.schedule_changed();
            }
            return;
        }
        let (changed, retry) = {
            let mut st = self.state.lock().unwrap();
            let changed = st.windows.get(&id) != Some(&res.info);
            if changed {
                st.windows.insert(id, res.info);
            }
            let mut retry = None;
            if res.complete {
                st.incomplete.remove(&id);
                st.incomplete_checks.remove(&id);
            } else {
                st.incomplete.insert(id);
                let n = st.incomplete_checks.entry(id).or_insert(0);
                *n += 1;
                if attempt < RETRY_DELAYS_MS.len() && *n < MAX_INCOMPLETE_CHECKS {
                    retry = Some(attempt);
                }
            }
            (changed, retry)
        };
        if changed {
            self.schedule_changed();
        }
        if let Some(attempt) = retry {
            let me = self.clone();
            self.ui.post_after(Duration::from_millis(RETRY_DELAYS_MS[attempt]), move || {
                let still = !me.disposed.load(Ordering::SeqCst) && me.state.lock().unwrap().incomplete.contains(&id);
                if still {
                    me.evaluate(id, attempt + 1, true);
                }
            });
        }
    }

    fn schedule_changed(self: &Arc<Self>) {
        {
            let mut st = self.state.lock().unwrap();
            if st.change_pending {
                return;
            }
            st.change_pending = true;
        }
        let me = self.clone();
        self.ui.post_after(Duration::from_millis(40), move || me.raise_changed());
    }

    fn raise_changed(&self) {
        let (list, fg) = {
            let mut st = self.state.lock().unwrap();
            st.change_pending = false;
            (st.windows.values().cloned().collect::<Vec<_>>(), st.foreground)
        };
        if self.disposed.load(Ordering::SeqCst) {
            return;
        }
        self.changed.emit(&(list, fg));
    }

    pub fn dispose(&self) {
        if self.disposed.swap(true, Ordering::SeqCst) {
            return;
        }
        if let Some(t) = self.safety_net.lock().unwrap().take() {
            t.stop();
        }
        let mut st = self.state.lock().unwrap();
        st.watcher = None;
        st.own_watcher = None;
    }
}

#[allow(dead_code)]
fn _assert_send_sync() {
    fn is<T: Send + Sync>() {}
    is::<RunningApps>();
    let _ = HWND::default();
}
