//! The engine's relation to the system: the Windows taskbar (hidden while a UI replaces it, always given back), global hotkeys,
//! the tray icon and its menu, "Start with Windows", display changes and Explorer restarts.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use fd_core::settings::TaskbarMode;
use fd_core::taskbar::{TaskbarCoordinator, TaskbarOutcome, TaskbarResult, TaskbarStateStore};
use fd_core::{hotkey, interface_text, DockSettings};
use fd_ipc::server::IpcError;
use fd_windows::message_window::MessageWindow;
use fd_windows::taskbar_win32::Win32TaskbarSystem;
use fd_windows::tray::{self, MenuItem, TrayIcon};
use fd_windows::{restore_flow, UiTimer};
use serde_json::{json, Value};

use crate::engine::Engine;
use crate::log;

pub const HOTKEY_LAUNCHPAD: i32 = 1;
pub const HOTKEY_RESTORE_TASKBAR: i32 = 2;
pub const HOTKEY_DOCK_FOCUS: i32 = 3;
pub const HOTKEY_SEARCH: i32 = 4;

#[derive(Default)]
pub struct SystemState {
    pub taskbar: OnceLock<Arc<TaskbarCoordinator>>,
    pub msg: OnceLock<Arc<MessageWindow>>,
    pub tray: OnceLock<Arc<TrayIcon>>,
    taskbar_timer: OnceLock<UiTimer>,
    display_timer: OnceLock<UiTimer>,
    hotkeys: Mutex<HashMap<i32, bool>>,
    /// The UI is gone for good: the taskbar was given back and stays untouched until a UI connects again.
    ui_gone: AtomicBool,
    /// Greater than zero while the tray is being read or driven; the taskbar maintenance timer pauses meanwhile.
    tray_operations: AtomicI32,
    tray_worker: OnceLock<Arc<fd_windows::ShellWorker>>,
    tray_items: Mutex<Vec<fd_windows::tray_items::TrayItem>>,
}

pub fn new_taskbar_coordinator(paths: &fd_core::AppPaths) -> Arc<TaskbarCoordinator> {
    Arc::new(TaskbarCoordinator::new(Box::new(Win32TaskbarSystem), TaskbarStateStore::new(&paths.taskbar_state_file()), |m| log::info(m)))
}

impl Engine {
    pub fn taskbar(&self) -> &Arc<TaskbarCoordinator> {
        self.sys.taskbar.get().expect("taskbar coordinator is created at start")
    }

    pub fn tray_operation(&self, begin: bool) {
        if begin {
            self.sys.tray_operations.fetch_add(1, Ordering::SeqCst);
        } else {
            let _ = self.sys.tray_operations.try_update(Ordering::SeqCst, Ordering::SeqCst, |v| Some((v - 1).max(0)));
        }
    }

    /// A balloon next to the tray icon: the only message channel when no window is open.
    pub fn notify(&self, title: &str, text: &str, error: bool) {
        if error {
            log::warn(format!("{title}: {text}"));
        } else {
            log::info(format!("{title}: {text}"));
        }
        if let Some(t) = self.sys.tray.get() {
            t.show_balloon(title, text, error);
        }
    }

    fn report_taskbar(&self, r: &TaskbarResult, what: &str, always: bool) {
        log::info(format!("taskbar {what}: {:?} - {}", r.outcome, r.message));
        if r.needs_attention() || (always && r.outcome == TaskbarOutcome::Ok) {
            self.notify("Windows taskbar", &r.message, !r.success());
        }
    }

    fn apply_taskbar_mode(&self, mode: TaskbarMode, user_change: bool) {
        if self.diagnostic || self.sys.ui_gone.load(Ordering::SeqCst) {
            return; // a test profile never hides the user's taskbar, whatever its settings say
        }
        let r = self.taskbar().apply(mode);
        self.report_taskbar(&r, &format!("apply {}", mode.as_str()), false);
        // The setting must not claim a mode that is not active: after a refused change fall back to Leave.
        if r.outcome == TaskbarOutcome::Refused && user_change && self.settings.current().taskbar_mode != TaskbarMode::Leave {
            self.settings.update(|s| s.taskbar_mode = TaskbarMode::Leave);
        }
    }

    fn maintain_taskbar(&self) {
        let s = self.settings.current();
        if self.diagnostic
            || self.sys.ui_gone.load(Ordering::SeqCst)
            || self.shutting_down.load(Ordering::SeqCst)
            || self.sys.tray_operations.load(Ordering::SeqCst) > 0
            || s.taskbar_mode == TaskbarMode::Leave
        {
            return;
        }
        let r = self.taskbar().maintain(s.taskbar_mode);
        if r.needs_attention() {
            log::warn(format!("taskbar maintenance: {}", r.message));
        }
    }

    /// Tray menu / hotkey / settings button: restore the Windows taskbar and stop hiding it.
    pub fn restore_taskbar_now(&self) {
        let r = restore_flow::run(self.taskbar(), false, !self.diagnostic);
        self.report_taskbar(&r, "manual restore", true);
        if r.success() && self.settings.current().taskbar_mode != TaskbarMode::Leave {
            self.settings.update(|s| s.taskbar_mode = TaskbarMode::Leave);
        }
    }

    /// The taskbar stays hidden only while a UI (the dock) is there to replace it: when the last UI goes away it comes back.
    pub fn on_session_closed(self: &Arc<Self>) {
        if self.shutting_down.load(Ordering::SeqCst) || self.diagnostic {
            return;
        }
        // The supervisor restarts a crashed UI within a moment: give it a few seconds before giving the taskbar back.
        let e = self.clone();
        self.ui.post_after(Duration::from_secs(4), move || {
            if e.shutting_down.load(Ordering::SeqCst) || e.server.session_count() != 0 || e.sys.ui_gone.load(Ordering::SeqCst) {
                return;
            }
            e.sys.ui_gone.store(true, Ordering::SeqCst);
            let r = e.taskbar().restore();
            if r.success() {
                log::info("taskbar: the UI is gone, the Windows taskbar was restored");
            } else {
                log::error(format!("taskbar restore after the UI stopped failed: {}", r.message));
            }
        });
    }

    pub fn on_session_opened(&self) {
        if !self.sys.ui_gone.swap(false, Ordering::SeqCst) {
            return;
        }
        self.apply_taskbar_mode(self.settings.current().taskbar_mode, false);
    }

    fn on_explorer_restarted(self: &Arc<Self>) {
        log::info("Explorer restarted (TaskbarCreated)");
        self.apply_taskbar_mode(self.settings.current().taskbar_mode, false);
        self.schedule_display_refresh();
    }

    pub fn schedule_display_refresh(&self) {
        if let Some(t) = self.sys.display_timer.get() {
            t.once(Duration::from_millis(350));
        }
    }

    // ---------------------------------------------------------------------------------------------- hotkeys

    fn register_hotkey(&self, id: i32, text: &str) {
        if self.diagnostic {
            return; // a test profile must not take the user's global shortcuts
        }
        let Some(msg) = self.sys.msg.get() else { return };
        msg.unregister_hotkey(id);
        self.sys.hotkeys.lock().unwrap().insert(id, false);
        if text.trim().is_empty() {
            self.publish_hotkey_status();
            return;
        }
        let Some((mods, vk)) = hotkey::try_parse(text) else {
            log::warn(format!("invalid hotkey '{text}'"));
            self.publish_hotkey_status();
            return;
        };
        let ok = msg.register_hotkey(id, mods, vk);
        self.sys.hotkeys.lock().unwrap().insert(id, ok);
        if !ok {
            log::warn(format!("hotkey '{text}' is already used by another program"));
        }
        self.publish_hotkey_status();
    }

    fn publish_hotkey_status(&self) {
        self.server.publish("hotkeys.changed", self.hotkey_status_node());
    }

    pub fn hotkey_status_node(&self) -> Value {
        let h = self.sys.hotkeys.lock().unwrap();
        let ok = |id: i32| h.get(&id).copied().unwrap_or(true);
        json!({ "launchpad": ok(HOTKEY_LAUNCHPAD), "restoreTaskbar": ok(HOTKEY_RESTORE_TASKBAR), "dockFocus": ok(HOTKEY_DOCK_FOCUS), "search": ok(HOTKEY_SEARCH) })
    }

    fn on_hotkey(&self, id: i32) {
        match id {
            HOTKEY_SEARCH => self.ui_command("toggleSearch", None),
            HOTKEY_LAUNCHPAD => self.ui_command("toggleLaunchpad", None),
            HOTKEY_RESTORE_TASKBAR => self.restore_taskbar_now(),
            HOTKEY_DOCK_FOCUS => self.ui_command("focusDock", None),
            _ => {}
        }
    }

    // ---------------------------------------------------------------------------------------------- tray

    fn tray_menu(&self) -> Vec<MenuItem> {
        let lang = self.settings.current().language;
        let t = |text: &str| interface_text::get(text, lang);
        let hidden = self.dock_user_hidden.load(Ordering::SeqCst);
        vec![
            MenuItem::new(1, t("Launchpad")),
            MenuItem::new(2, t(if hidden { "Show Dock" } else { "Hide Dock" })),
            MenuItem::new(3, t("Settings…")),
            MenuItem::separator(),
            MenuItem::new(4, t("Restore Windows taskbar")),
            MenuItem::new(5, t("Restart FloatingDock")),
            MenuItem::new(6, t("Exit")),
        ]
    }

    fn on_tray_command(self: &Arc<Self>, id: i32) {
        match id {
            1 => self.ui_command("toggleLaunchpad", None),
            2 => self.set_dock_user_hidden(!self.dock_user_hidden.load(Ordering::SeqCst)),
            3 => self.ui_command("settings", None),
            4 => self.restore_taskbar_now(),
            5 => self.restart(),
            6 => self.shutdown(),
            _ => {}
        }
    }

    // ---------------------------------------------------------------------------------------------- settings -> system

    /// What the settings change in the system (taskbar mode, autostart, hotkeys). `old` is `None` at start.
    pub fn apply_system_settings_part(self: &Arc<Self>, old: Option<&DockSettings>, s: &DockSettings) {
        if old.is_none_or(|o| o.taskbar_mode != s.taskbar_mode) {
            self.apply_taskbar_mode(s.taskbar_mode, old.is_some());
        }
        if let Some(t) = self.sys.taskbar_timer.get() {
            if s.taskbar_mode == TaskbarMode::Leave {
                t.stop();
            } else {
                t.repeat(Duration::from_millis(750), Duration::from_millis(750));
            }
        }
        if !self.diagnostic && old.is_none_or(|o| o.start_with_windows != s.start_with_windows) || (!self.diagnostic && s.start_with_windows != tray::autostart_enabled()) {
            if let Ok(exe) = std::env::current_exe() {
                if let Err(e) = tray::set_autostart(s.start_with_windows, &exe.to_string_lossy()) {
                    log::warn(format!("autostart failed: {e}"));
                }
            }
        }
        if old.is_none_or(|o| o.search_hotkey != s.search_hotkey) {
            self.register_hotkey(HOTKEY_SEARCH, &s.search_hotkey);
        }
        if old.is_none_or(|o| o.launchpad_hotkey != s.launchpad_hotkey) {
            self.register_hotkey(HOTKEY_LAUNCHPAD, &s.launchpad_hotkey);
        }
        if old.is_none_or(|o| o.restore_taskbar_hotkey != s.restore_taskbar_hotkey) {
            self.register_hotkey(HOTKEY_RESTORE_TASKBAR, &s.restore_taskbar_hotkey);
        }
        if old.is_none_or(|o| o.dock_focus_hotkey != s.dock_focus_hotkey) {
            self.register_hotkey(HOTKEY_DOCK_FOCUS, &s.dock_focus_hotkey);
        }
    }
}

/// Creates the system services. Runs once at start, on the UI-loop thread (the message window and the hooks belong to it).
pub fn start(engine: &Arc<Engine>) {
    // A previous run may have been killed with the taskbar changed: put it back before anything is applied.
    let taskbar = new_taskbar_coordinator(&engine.paths);
    let _ = engine.sys.taskbar.set(taskbar.clone());
    let r = restore_flow::run(&taskbar, true, !engine.diagnostic);
    engine.report_taskbar_pub(&r, "start-up recovery");

    match MessageWindow::new() {
        Ok(msg) => {
            let _ = engine.sys.msg.set(msg.clone());
            if !engine.diagnostic {
                let exe = std::env::current_exe().ok().map(|p| p.to_string_lossy().into_owned());
                let tray = TrayIcon::new(msg.clone(), "FloatingDock", exe.as_deref());
                let e = engine.clone();
                tray.set_menu_provider(move || e.tray_menu());
                let e = engine.clone();
                tray.menu_command.subscribe(move |id| {
                    let (e2, id) = (e.clone(), *id);
                    e.ui.post(move || e2.on_tray_command(id));
                });
                let e = engine.clone();
                tray.left_click.subscribe(move |_| {
                    let e2 = e.clone();
                    e.ui.post(move || e2.ui_command("toggleLaunchpad", None));
                });
                let _ = engine.sys.tray.set(tray);
            }
            let e = engine.clone();
            msg.hotkey_pressed.subscribe(move |id| {
                let (e2, id) = (e.clone(), *id);
                e.ui.post(move || e2.on_hotkey(id));
            });
            let e = engine.clone();
            msg.display_changed.subscribe(move |_| {
                let e2 = e.clone();
                e.ui.post(move || e2.schedule_display_refresh());
            });
            let e = engine.clone();
            msg.work_area_or_settings_changed.subscribe(move |_| {
                let e2 = e.clone();
                e.ui.post(move || e2.schedule_display_refresh());
            });
            let e = engine.clone();
            msg.taskbar_created.subscribe(move |_| {
                let e2 = e.clone();
                e.ui.post(move || e2.on_explorer_restarted());
            });
            let e = engine.clone();
            msg.session_ending.subscribe(move |_| {
                let _ = e.taskbar().restore();
                e.settings.flush();
            });
        }
        Err(e) => log::error(format!("the message window could not be created: {e}")),
    }

    let e = engine.clone();
    engine.server.session_closed().subscribe(move |_| {
        let e2 = e.clone();
        e.ui.post(move || e2.on_session_closed());
    });
    let e = engine.clone();
    engine.server.session_opened().subscribe(move |_| {
        let e2 = e.clone();
        e.ui.post(move || e2.on_session_opened());
    });

    let e = engine.clone();
    let _ = engine.sys.taskbar_timer.set(UiTimer::new(&engine.ui, move || e.maintain_taskbar()));
    let e = engine.clone();
    let _ = engine.sys.display_timer.set(UiTimer::new(&engine.ui, move || {
        if !e.shutting_down.load(Ordering::SeqCst) {
            e.server.publish("display.changed", Value::Null);
        }
    }));

    let s = engine.settings.current();
    engine.apply_system_settings_part(None, &s);
}

pub fn register(engine: &Arc<Engine>) {
    let e = engine.clone();
    engine.server.register_sync("hotkey.status", move |_| Ok(e.hotkey_status_node()));
    let e = engine.clone();
    engine.server.register_sync("taskbar.restore", move |_| {
        let e2 = e.clone();
        e.ui.post(move || e2.restore_taskbar_now());
        Ok(Value::Null)
    });

    // ---- tray icons of Explorer (read through UI Automation)
    let e = engine.clone();
    engine.server.register("tray.items", move |_| {
        let e = e.clone();
        async move {
            e.tray_operation(true);
            let worker = e.sys.tray_worker.get_or_init(|| Arc::new(fd_windows::ShellWorker::new())).clone();
            let read = worker.run(fd_windows::tray_items::read_items).await;
            e.tray_operation(false);
            let items = read.unwrap_or_default();
            let node: Vec<Value> = items
                .iter()
                .enumerate()
                .map(|(i, t)| {
                    use base64::Engine as _;
                    json!({
                        "index": i, "name": t.name,
                        "icon": t.icon.as_ref().map(|b| base64::engine::general_purpose::STANDARD.encode(b)),
                        "iconPixels": fd_windows::tray_items::png_pixel_size(t.icon.as_deref()),
                    })
                })
                .collect();
            *e.sys.tray_items.lock().unwrap() = items;
            Ok(json!({ "items": node }))
        }
    });
    let e = engine.clone();
    engine.server.register("tray.activate", move |r| {
        let e = e.clone();
        async move {
            let index = r.i64_or("index", -1);
            let item = usize::try_from(index)
                .ok()
                .and_then(|i| e.sys.tray_items.lock().unwrap().get(i).cloned())
                .ok_or_else(|| IpcError::bad_params("That tray icon is no longer available."))?;
            // The right-click menu of a tray icon is not ported yet (see tray_items.rs).
            if r.bool_or("context", false) {
                log::info("tray.activate: the context menu of a tray icon is not supported by this engine yet");
                return Ok(json!({ "ok": false }));
            }
            e.tray_operation(true);
            let worker = e.sys.tray_worker.get_or_init(|| Arc::new(fd_windows::ShellWorker::new())).clone();
            let ok = worker.run(move || fd_windows::tray_items::activate_item(&item)).await.unwrap_or(false);
            e.tray_operation(false);
            Ok(json!({ "ok": ok }))
        }
    });
}

impl Engine {
    pub fn report_taskbar_pub(&self, r: &TaskbarResult, what: &str) {
        self.report_taskbar(r, what, false);
    }

    /// Gives the taskbar back, stops the services of this module. Part of the final cleanup.
    pub fn dispose_system(&self) {
        if let Some(t) = self.sys.taskbar_timer.get() {
            t.stop();
        }
        if let Some(t) = self.sys.display_timer.get() {
            t.stop();
        }
        if let Some(tc) = self.sys.taskbar.get() {
            let r = tc.restore();
            if !r.success() {
                log::warn(format!("taskbar restore on exit failed: {}", r.message));
            }
        }
        if let Some(t) = self.sys.tray.get() {
            t.dispose();
        }
        if let Some(m) = self.sys.msg.get() {
            m.dispose();
        }
    }
}
