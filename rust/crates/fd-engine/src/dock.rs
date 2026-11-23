//! The dock's model: which slots exist (Start, Launchpad, pinned, running), what a click on one does, and the window list the
//! UI shows. Mirrors `EngineHost.Dock.cs` and the window parts of `EngineHost.cs` / `EngineHost.System.cs`.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use fd_core::dock_entry_reconciler::{DockEntry, DockEntryKind, DockEntryOptions, DockEntryReconciler};
use fd_core::{AppMatching, AppRef, WindowInfo};
use fd_ipc::server::{IpcError, IpcRequest, IpcResult};
use fd_ipc::protocol;
use fd_windows::activator::{self, ActivationResult};
use fd_windows::{launcher, native, UiTimer};
use serde_json::{json, Value};

use crate::engine::Engine;
use crate::log;

#[derive(Default)]
pub struct DockState {
    reconciler: Mutex<DockEntryReconciler>,
    entries: Mutex<Vec<DockEntry>>,
    entries_shape: Mutex<String>,
    windows_shape: Mutex<String>,
    activating: Mutex<HashSet<String>>,
    anchors: Mutex<Anchors>,
    shell_animation: std::sync::OnceLock<fd_windows::shell_animation::ShellAnimationWindow>,
}

/// Where the dock's icons are on screen: the shell uses it as the target of minimize / restore animations.
#[derive(Default, Clone)]
pub struct Anchors {
    pub by_window: HashMap<i64, [i32; 4]>,
    pub anchors: Vec<(String, [i32; 4])>,
    pub fallback: Option<[i32; 4]>,
}

fn start_app() -> AppRef {
    AppRef {
        id: "special-start".into(),
        name: "Start".into(),
        aliases: Some(vec!["Windows".into(), "Start Menu".into(), "Windows Start".into(), "Пуск".into()]),
        ..Default::default()
    }
}

fn launchpad_app() -> AppRef {
    AppRef {
        id: "special-launchpad".into(),
        name: "Launchpad".into(),
        aliases: Some(vec!["Apps".into(), "App Library".into(), "All Apps".into(), "Launcher".into(), "Приложения".into()]),
        ..Default::default()
    }
}

fn opt_str(s: &Option<String>) -> Value {
    s.as_ref().map_or(Value::Null, |v| Value::String(v.clone()))
}

fn require_hwnd(r: &IpcRequest) -> Result<i64, IpcError> {
    r.params.get("hwnd").and_then(Value::as_i64).ok_or_else(|| IpcError::bad_params("hwnd is required."))
}

pub fn window_node(w: &WindowInfo) -> Value {
    json!({
        "hwnd": w.hwnd, "pid": w.process_id, "exePath": opt_str(&w.exe_path), "aumid": opt_str(&w.aumid),
        "title": opt_str(&w.title), "className": opt_str(&w.class_name), "minimized": w.is_minimized,
    })
}

pub fn register(engine: &Arc<Engine>) {
    let s = &engine.server;

    let e = engine.clone();
    s.register(protocol::methods::WINDOWS_LIST, move |_| {
        let e = e.clone();
        async move {
            let rx = e.ui.invoke({
                let e = e.clone();
                move || e.windows_node()
            });
            rx.await.map_err(|_| IpcError::failed("The engine is stopping."))
        }
    });

    let e = engine.clone();
    s.register("dock.entries", move |_| {
        let e = e.clone();
        async move {
            let rx = e.ui.invoke({
                let e = e.clone();
                move || {
                    e.rebuild_dock_entries(false);
                    e.dock_entries_node(true)
                }
            });
            rx.await.map_err(|_| IpcError::failed("The engine is stopping."))
        }
    });

    let e = engine.clone();
    s.register("dock.activate", move |r| {
        let e = e.clone();
        async move { e.activate_entry(r.str("key")?.to_string(), r.bool_or("newInstance", false)).await }
    });

    let e = engine.clone();
    s.register_sync("dock.close", move |r| {
        if let Some(entry) = e.find_entry(r.str("key")?) {
            activator::request_close(&entry.windows);
        }
        Ok(Value::Null)
    });

    let e = engine.clone();
    s.register_sync("dock.menuInfo", move |r| {
        let key = r.str("key")?;
        let entry = e.find_entry(key);
        let pinned = e.pinned.snapshot();
        let index = pinned.iter().position(|p| p.key() == key).map_or(-1, |i| i as i64);
        let app = entry.as_ref().and_then(|en| en.app.as_ref());
        Ok(json!({
            "found": entry.is_some(),
            "pinned": entry.as_ref().is_some_and(|en| en.is_pinned),
            "running": entry.as_ref().is_some_and(|en| en.is_running()),
            "windows": entry.as_ref().map_or(0, |en| en.windows.len()),
            "hasLocation": app.is_some_and(|a| launcher::location_of(a).is_some()),
            "hasOverride": app.is_some_and(|a| e.has_icon_override(a)),
            "pinnedIndex": index,
            "pinnedCount": pinned.len(),
        }))
    });

    s.register_sync("windows.close", |r| {
        activator::post_close(require_hwnd(&r)?);
        Ok(Value::Null)
    });

    s.register_sync("windows.rect", |r| {
        let h = native::hwnd(require_hwnd(&r)?);
        match native::window_rect(h) {
            Some(rc) => Ok(json!({ "w": rc.right - rc.left, "h": rc.bottom - rc.top, "minimized": native::is_iconic(h) })),
            None => Ok(Value::Null),
        }
    });

    s.register_sync("windows.activate", |r| {
        activator::activate(native::hwnd(require_hwnd(&r)?));
        Ok(Value::Null)
    });

    let e = engine.clone();
    s.register_sync("windows.include", move |r| {
        let id = require_hwnd(&r)?;
        let e2 = e.clone();
        e.ui.post(move || e2.running.include_own_window(id));
        Ok(Value::Null)
    });
    let e = engine.clone();
    s.register_sync("windows.exclude", move |r| {
        let id = require_hwnd(&r)?;
        let e2 = e.clone();
        e.ui.post(move || e2.running.exclude_own_window(id));
        Ok(Value::Null)
    });

    let e = engine.clone();
    s.register_sync("dock.anchors", move |r| {
        let rect = |o: &Value| -> Option<[i32; 4]> {
            let g = |k: &str| o.get(k).and_then(Value::as_i64).map(|v| v as i32);
            Some([g("left")?, g("top")?, g("right")?, g("bottom")?])
        };
        let mut anchors = Vec::new();
        for a in r.params.get("anchors").and_then(Value::as_array).into_iter().flatten() {
            if let (Some(key), Some(rc)) = (a.get("key").and_then(Value::as_str), rect(a)) {
                anchors.push((key.to_string(), rc));
            }
        }
        let fallback = r.params.get("fallback").and_then(rect);
        let mut by_window = HashMap::new();
        for (key, rc) in &anchors {
            if let Some(entry) = e.find_entry(key) {
                for w in &entry.windows {
                    by_window.insert(w.hwnd, *rc);
                }
            }
        }
        *e.dock.anchors.lock().unwrap() = Anchors { by_window, anchors, fallback };
        // The shell asks where minimize / restore animations fly to; answer it from now on (the window is created on first use).
        if e.dock.shell_animation.get().is_none() && !e.diagnostic {
            let e2 = e.clone();
            match fd_windows::shell_animation::ShellAnimationWindow::start(move |w| e2.shell_animation_rect(w)) {
                Ok(win) => {
                    log::info(format!("shell animation target registered ({})", if win.registered() { "shell hook on" } else { "shell hook refused" }));
                    let _ = e.dock.shell_animation.set(win);
                }
                Err(err) => log::warn(format!("shell animation window failed: {err}")),
            }
        }
        Ok(Value::Null)
    });
}

/// Hooks the model to the events that change it. Runs once at start.
pub fn wire(engine: &Arc<Engine>) {
    let e = engine.clone();
    let timer = UiTimer::new(&engine.ui, move || e.rebuild_dock_entries(true));
    let _ = engine.dock_timer.set(timer);

    let e = engine.clone();
    engine.running.changed.subscribe(move |_| {
        e.publish_windows_if_changed();
        e.schedule_dock_entries();
    });
    let e = engine.clone();
    engine.pinned.changed().subscribe(move |_| e.schedule_dock_entries());
    let e = engine.clone();
    engine.settings.changed().subscribe(move |_| e.schedule_dock_entries());
}

impl Engine {
    /// Called on the shell thread: the target rectangle of a window's minimize / restore animation.
    pub fn shell_animation_rect(&self, window: windows::Win32::Foundation::HWND) -> Option<windows::Win32::Foundation::RECT> {
        use windows::Win32::Foundation::RECT;
        let a = self.dock.anchors.lock().unwrap().clone();
        let to_rect = |r: &[i32; 4]| RECT { left: r[0], top: r[1], right: r[2], bottom: r[3] };
        let window = native::root_owner(window);
        if let Some(exact) = a.by_window.get(&native::hwnd_id(window)) {
            return Some(to_rect(exact));
        }
        if !native::is_window(window) {
            return None;
        }
        let pid = native::pid_of(window);
        let exe = native::exe_path(pid);
        let aumid = if AppMatching::is_host_exe(exe.as_deref()) { native::process_aumid(pid) } else { None };
        let info = WindowInfo {
            hwnd: native::hwnd_id(window),
            process_id: pid as i32,
            exe_path: exe.clone(),
            aumid,
            title: None,
            class_name: None,
            is_minimized: native::is_iconic(window),
        };
        let entries = self.dock.entries.lock().unwrap().clone();
        for (key, rect) in &a.anchors {
            if let Some(app) = entries.iter().find(|e| e.key == *key).and_then(|e| e.app.as_ref()) {
                if AppMatching::matches(app, &info) {
                    return Some(to_rect(rect));
                }
            }
        }
        if self.is_ui_process(pid as i64) || fd_windows::window_filter::is_ignored_exe(exe.as_deref()) {
            return None;
        }
        a.fallback.as_ref().map(to_rect)
    }

    pub fn dispose_dock(&self) {
        if let Some(w) = self.dock.shell_animation.get() {
            w.dispose();
        }
    }


    pub fn find_entry(&self, key: &str) -> Option<DockEntry> {
        self.dock.entries.lock().unwrap().iter().find(|e| e.key == key).cloned()
    }

    pub fn schedule_dock_entries(&self) {
        if self.shutting_down.load(std::sync::atomic::Ordering::SeqCst) {
            return;
        }
        if let Some(t) = self.dock_timer.get() {
            t.once(Duration::from_millis(25));
        }
    }

    pub fn rebuild_dock_entries(&self, publish: bool) {
        if self.shutting_down.load(std::sync::atomic::Ordering::SeqCst) {
            return;
        }
        let s = self.settings.current();
        let entries = self.dock.reconciler.lock().unwrap().build(
            DockEntryOptions {
                show_start: s.show_start_button,
                show_launchpad: s.show_launchpad_button,
                show_pinned: s.show_pinned,
                show_running: s.show_running,
            },
            &self.pinned.snapshot(),
            &self.running.snapshot(),
            self.running.foreground_root(),
        );
        *self.dock.entries.lock().unwrap() = entries;
        // Window titles change all the time and the dock draws none of them: only a change in what the dock shows is published.
        // Previews ask for the current titles when they open (dock.entries).
        let shape = self.dock_entries_node(false).to_string();
        {
            let mut last = self.dock.entries_shape.lock().unwrap();
            if *last == shape {
                return;
            }
            *last = shape;
        }
        if publish {
            self.server.publish("dock.entries", self.dock_entries_node(true));
        }
    }

    pub fn dock_entries_node(&self, with_titles: bool) -> Value {
        let list: Vec<Value> = self
            .dock
            .entries
            .lock()
            .unwrap()
            .iter()
            .map(|e| {
                let windows: Vec<Value> = e
                    .windows
                    .iter()
                    .map(|w| json!({ "hwnd": w.hwnd, "title": if with_titles { opt_str(&w.title) } else { Value::String(String::new()) }, "minimized": w.is_minimized }))
                    .collect();
                let app = match e.kind {
                    DockEntryKind::Start => Some(start_app()),
                    DockEntryKind::Launchpad => Some(launchpad_app()),
                    _ => e.app.clone(),
                };
                json!({
                    "key": e.key, "kind": e.kind.as_str(), "name": e.name, "pinned": e.is_pinned, "running": e.is_running(),
                    "active": e.is_active, "app": app.map_or(Value::Null, |a| a.to_node()), "windows": windows,
                })
            })
            .collect();
        Value::Array(list)
    }

    /// Must run on the UI-loop thread (the running-apps service is not thread-safe).
    pub fn windows_node(&self) -> Value {
        let windows: Vec<Value> = self.running.snapshot().iter().map(window_node).collect();
        json!({ "foreground": self.running.foreground_root(), "foregroundName": self.foreground_name(), "windows": windows })
    }

    /// Titles change all the time and nothing that listens draws them: only the foreground app and the set of windows are published.
    pub fn publish_windows_if_changed(&self) {
        let mut shape = format!("{}|{}", self.running.foreground_root(), self.foreground_name().unwrap_or_default());
        for w in self.running.snapshot() {
            shape.push_str(&format!("|{}{}", w.hwnd, if w.is_minimized { 'm' } else { 'n' }));
        }
        {
            let mut last = self.dock.windows_shape.lock().unwrap();
            if *last == shape {
                return;
            }
            *last = shape;
        }
        self.server.publish(protocol::events::WINDOWS_CHANGED, self.windows_node());
    }

    /// The foreground app's display name for the bar: a pinned app's name, else a tidy name from the window.
    pub fn foreground_name(&self) -> Option<String> {
        let foreground = self.running.foreground_root();
        let windows = self.running.snapshot();
        let Some(current) = windows.iter().find(|w| w.hwnd == foreground) else {
            let class = native::class_name(native::hwnd(foreground));
            return matches!(class.as_str(), "Progman" | "WorkerW").then(|| "Desktop".to_string());
        };
        if let Some(p) = self.pinned.snapshot().iter().find(|p| AppMatching::matches(p, current)) {
            return Some(p.name.clone());
        }
        let name = AppMatching::default_name(current);
        Some(match name.to_lowercase().as_str() {
            "explorer" => "File Explorer".to_string(),
            "floatingdock" => "FloatingDock settings".to_string(),
            "taskmgr" => "Task Manager".to_string(),
            "msedge" => "Microsoft Edge".to_string(),
            "chrome" => "Google Chrome".to_string(),
            "steam" => "Steam".to_string(),
            "chatgpt" => "ChatGPT".to_string(),
            "code" => "Visual Studio Code".to_string(),
            _ => {
                let mut c = name.chars();
                match c.next() {
                    Some(f) => f.to_uppercase().collect::<String>() + c.as_str(),
                    None => "Desktop".to_string(),
                }
            }
        })
    }

    /// A click on a dock slot. Running apps are activated (or minimized when already in front); others are launched.
    pub async fn activate_entry(self: Arc<Self>, key: String, new_instance: bool) -> IpcResult {
        let entry = self.find_entry(&key).ok_or_else(|| IpcError::bad_params("That dock item is no longer there."))?;
        match entry.kind {
            DockEntryKind::Start => launcher::toggle_start_menu(),
            DockEntryKind::Launchpad => self.ui_command("toggleLaunchpad", None),
            DockEntryKind::Separator => {}
            DockEntryKind::App if entry.is_running() && !new_instance => {
                if !self.dock.activating.lock().unwrap().insert(key.clone()) {
                    return Ok(Value::Null);
                }
                let minimize = self.settings.current().minimize_on_repeat_click;
                let result = activator::activate_or_minimize(&entry.windows, minimize).await;
                self.dock.activating.lock().unwrap().remove(&key);
                match result {
                    ActivationResult::NoLiveWindow => {
                        let e = self.clone();
                        self.ui.post(move || e.running.full_scan());
                        if let Some(app) = entry.app.clone() {
                            let _ = self.worker.run(move || launcher::launch(&app, false));
                        }
                    }
                    ActivationResult::Denied => self.notify(
                        &format!("Could not switch to {}", entry.name),
                        "Windows did not give this window focus. Check for an open dialog or a different virtual desktop.",
                        true,
                    ),
                    _ => {}
                }
            }
            DockEntryKind::App => {
                if let Some(app) = entry.app.clone() {
                    let e = self.clone();
                    let _ = self.worker.run(move || {
                        if !launcher::launch(&app, new_instance) {
                            log::warn(format!("launch failed: {}", app.name));
                            e.notify(&format!("Could not open {}", app.name), "The shortcut or application is unavailable.", true);
                        }
                    });
                }
            }
        }
        Ok(Value::Null)
    }
}
