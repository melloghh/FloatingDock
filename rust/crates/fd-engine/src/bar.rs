//! What the top bar shows that Windows tells us about: the foreground app's name, keyboard layout, battery, network, sound,
//! full-screen windows, and how the dock shares each monitor with maximized and full-screen windows.

use std::collections::HashMap;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use fd_core::DockSettings;
use fd_ipc::server::{IpcError, IpcRequest, IpcResult};
use fd_windows::display::{self, Monitor};
use fd_windows::{native, status, wifi, UiTimer, WindowEventKind};
use serde_json::{json, Value};
use windows::Win32::UI::WindowsAndMessaging::SetForegroundWindow;

use crate::engine::Engine;

#[derive(Default)]
pub struct BarState {
    language_tag: Mutex<String>,
    language_source: Mutex<i64>,
    fullscreen_state: Mutex<String>,
    space_state: Mutex<String>,
    networks: Mutex<Vec<wifi::Network>>,
    fit_attempts: Mutex<HashMap<i64, u32>>,
    network_available: Mutex<Option<bool>>,
    timers: OnceLock<Timers>,
    network_watcher: Mutex<Option<status::NetworkWatcher>>,
}

struct Timers {
    language: UiTimer,
    space: UiTimer,
    fullscreen: UiTimer,
    network: UiTimer,
}

fn bad(m: impl Into<String>) -> IpcError {
    IpcError::bad_params(m)
}

fn hwnd_param(r: &IpcRequest, name: &str) -> Result<i64, IpcError> {
    r.params.get(name).and_then(Value::as_i64).ok_or_else(|| bad(format!("{name} is required.")))
}

impl Engine {
    fn quiet(&self) -> bool {
        self.shutting_down.load(Ordering::SeqCst) || self.server.session_count() == 0
    }

    // ---------------------------------------------------------------------------------------------- keyboard layout

    fn current_language_tag(&self) -> Option<String> {
        let foreground = native::foreground();
        let (mut thread, pid) = native::thread_and_pid(foreground);
        let ui_foreground = self.is_ui_process(pid as i64);
        let mut source = self.bar.language_source.lock().unwrap();
        if !ui_foreground && !foreground.0.is_null() {
            *source = native::hwnd_id(foreground);
        } else if native::is_window(native::hwnd(*source)) {
            thread = native::thread_and_pid(native::hwnd(*source)).0;
        }
        if thread == 0 {
            return None;
        }
        let input = status::input_site(if ui_foreground { native::hwnd(*source) } else { foreground });
        drop(source);
        let (input_thread, input_pid) = native::thread_and_pid(input);
        if input_thread != 0 && !self.is_ui_process(input_pid as i64) {
            thread = input_thread;
        }
        status::language_tag(status::keyboard_layout_of_thread(thread))
    }

    fn publish_language(&self) {
        if self.quiet() || self.diagnostic || !self.settings.current().top_bar_enabled {
            return;
        }
        let Some(tag) = self.current_language_tag() else { return };
        {
            let mut last = self.bar.language_tag.lock().unwrap();
            if *last == tag {
                return;
            }
            *last = tag.clone();
        }
        self.server.publish("language.changed", json!({ "tag": tag }));
    }

    // ---------------------------------------------------------------------------------------------- screen

    fn monitors_node(&self) -> Value {
        Value::Array(
            display::monitors()
                .iter()
                .map(|m| json!({ "x": m.bounds.left, "y": m.bounds.top, "w": m.width(), "h": m.height(), "scale": m.scale, "primary": m.primary, "name": m.name }))
                .collect(),
        )
    }

    /// The window that counts as "the foreground application": the UI's own overlays never end an external fullscreen session.
    fn effective_foreground(&self) -> windows::Win32::Foundation::HWND {
        let fg = native::foreground();
        if self.is_ui_process(native::pid_of(fg) as i64) { native::hwnd(self.running.foreground_root()) } else { fg }
    }

    /// Per monitor: does the foreground window cover it (video, borderless game)? UI windows never count.
    fn fullscreen_node(&self) -> Value {
        let (own, fg) = (std::process::id(), self.effective_foreground());
        let root = native::root_owner(fg);
        let list: Vec<Value> = display::monitors()
            .iter()
            .map(|m| {
                let full = display::is_window_fullscreen_on(fg, m, own) || display::is_window_fullscreen_on(root, m, own);
                json!({ "x": m.bounds.left, "y": m.bounds.top, "fullscreen": full })
            })
            .collect();
        json!({ "monitors": list })
    }

    fn publish_fullscreen(&self) {
        if self.shutting_down.load(Ordering::SeqCst) || !self.settings.current().top_bar_enabled {
            return;
        }
        let node = self.fullscreen_node();
        let text = node.to_string();
        {
            let mut last = self.bar.fullscreen_state.lock().unwrap();
            if *last == text {
                return;
            }
            *last = text;
        }
        self.server.publish("fullscreen.changed", node);
    }

    /// Per monitor: should the dock hide, and may its edge still reveal it?
    pub fn space_node(&self) -> Value {
        let s: Arc<DockSettings> = self.settings.current();
        let game_fullscreen = s.keep_dock_in_fullscreen && display::windows_reports_fullscreen();
        let enabled = space_hides_for_covering_window(&s, game_fullscreen);
        let fit = space_should_fit_fullscreen(&s, game_fullscreen);
        let (own, fg) = (std::process::id(), self.effective_foreground());
        let root = native::root_owner(fg);
        let mut list = Vec::new();
        for m in display::monitors() {
            let covering = display::is_window_fullscreen_on(fg, &m, own) || display::is_window_fullscreen_on(root, &m, own);
            // A video (browser or player at full screen) keeps covering the screen and hides the dock even in "keep" mode.
            let video = covering && s.hide_on_fullscreen && s.keep_dock_in_fullscreen && (display::is_video_host(fg) || display::is_video_host(root));
            if fit && !video {
                self.fit_fullscreen_above_dock(&m, fg, root, own);
            }
            let fullscreen = (enabled && covering) || video;
            let maximized = enabled && (display::is_window_maximized_on(fg, &m, own) || display::is_window_maximized_on(root, &m, own));
            list.push(json!({ "x": m.bounds.left, "y": m.bounds.top, "fullscreen": covering, "hide": fullscreen || maximized, "blockReveal": fullscreen }));
        }
        json!({ "monitors": list })
    }

    /// A window that covers the whole monitor (F11, borderless full screen) ignores the work area, so move it above the reserved
    /// dock strip. Each window is fitted at most twice: an application that insists on its own bounds is left alone.
    fn fit_fullscreen_above_dock(&self, m: &Monitor, fg: windows::Win32::Foundation::HWND, root: windows::Win32::Foundation::HWND, own: u32) {
        let mut attempts = self.bar.fit_attempts.lock().unwrap();
        let mut seen = Vec::new();
        for w in [fg, root] {
            if w.0.is_null() || seen.contains(&native::hwnd_id(w)) {
                continue;
            }
            seen.push(native::hwnd_id(w));
            if !display::is_window_fullscreen_on(w, m, own) {
                continue;
            }
            let n = attempts.get(&native::hwnd_id(w)).copied().unwrap_or(0);
            if n >= 2 {
                continue;
            }
            if display::fit_to_area(w, m.work) {
                attempts.insert(native::hwnd_id(w), n + 1);
            }
        }
        if attempts.len() > 64 {
            attempts.clear();
        }
    }

    pub fn publish_space(&self) {
        if self.quiet() || self.diagnostic {
            return;
        }
        let node = self.space_node();
        let text = node.to_string();
        {
            let mut last = self.bar.space_state.lock().unwrap();
            if *last == text {
                return;
            }
            *last = text;
        }
        self.server.publish("space.changed", node);
    }

    fn check_network_available(&self) {
        let now = status::network_available();
        let changed = {
            let mut last = self.bar.network_available.lock().unwrap();
            let changed = *last != Some(now);
            *last = Some(now);
            changed
        };
        if changed {
            self.server.publish("network.changed", json!({ "available": now }));
        }
    }
}

fn space_hides_for_covering_window(s: &DockSettings, windows_reports_fullscreen: bool) -> bool {
    s.hide_on_fullscreen && (!s.keep_dock_in_fullscreen || windows_reports_fullscreen)
}

fn space_should_reserve(s: &DockSettings, user_hidden: bool) -> bool {
    (s.reserve_work_area || s.keep_dock_in_fullscreen)
        && s.taskbar_mode != fd_core::settings::TaskbarMode::Leave
        && !s.auto_hide
        && !user_hidden
}

fn space_should_fit_fullscreen(s: &DockSettings, windows_reports_fullscreen: bool) -> bool {
    s.keep_dock_in_fullscreen && space_should_reserve(s, false) && !windows_reports_fullscreen
}

pub fn register(engine: &Arc<Engine>) {
    let s = &engine.server;

    s.register_sync("battery.get", |_| {
        Ok(match status::battery() {
            Some(b) => json!({ "present": true, "percent": b.percent, "charging": b.charging }),
            None => json!({ "present": false }),
        })
    });
    s.register_sync("network.available", |_| Ok(Value::Bool(status::network_available())));
    let e = engine.clone();
    s.register_sync("language.current", move |_| {
        let tag = e.current_language_tag().unwrap_or_else(|| e.bar.language_tag.lock().unwrap().clone());
        Ok(json!({ "tag": tag }))
    });
    let e = engine.clone();
    s.register_sync("fullscreen.get", move |_| Ok(e.fullscreen_node()));
    let e = engine.clone();
    s.register_sync("monitors.get", move |_| Ok(e.monitors_node()));

    let e = engine.clone();
    s.register("space.get", move |_| {
        let e = e.clone();
        async move {
            let e2 = e.clone();
            e.ui.invoke(move || e2.space_node()).await.map_err(|_| IpcError::failed("The engine is stopping."))
        }
    });

    // The layout belongs to the window the user was working in, not to the menu that was just opened.
    let e = engine.clone();
    s.register_sync("language.list", move |r| {
        let target = r.params.get("target").and_then(Value::as_i64).unwrap_or_else(|| e.running.foreground_root());
        let thread = native::thread_and_pid(status::input_site(native::hwnd(target))).0;
        let current = status::keyboard_layout_of_thread(thread);
        let layouts: Vec<Value> = status::installed_layouts()
            .iter()
            .map(|l| json!({ "handle": l.handle, "name": l.name, "tag": l.tag, "current": l.handle == current }))
            .collect();
        Ok(json!({ "target": target, "layouts": layouts }))
    });
    s.register_sync("language.set", |r| {
        let target = native::hwnd(hwnd_param(&r, "target")?);
        let handle = hwnd_param(&r, "handle")?;
        unsafe {
            let _ = SetForegroundWindow(target);
        }
        Ok(json!({ "ok": status::request_layout(target, handle) }))
    });

    // ---- sound
    let e = engine.clone();
    s.register("audio.get", move |_| {
        let e = e.clone();
        async move {
            let v = e.worker.run(status::read_volume).await.map_err(|_| IpcError::failed("stopping"))?.map_err(IpcError::failed)?;
            Ok(json!({ "volume": v.volume, "muted": v.muted }))
        }
    });
    let e = engine.clone();
    s.register("audio.set", move |r| {
        let e = e.clone();
        async move {
            let volume = r.params.get("volume").and_then(Value::as_f64).map(|v| v as f32);
            let muted = r.params.get("muted").and_then(Value::as_bool);
            e.worker
                .run(move || {
                    if let Some(v) = volume {
                        status::set_volume(v)?;
                    }
                    if let Some(m) = muted {
                        status::set_muted(m)?;
                    }
                    Ok::<(), String>(())
                })
                .await
                .map_err(|_| IpcError::failed("stopping"))?
                .map_err(IpcError::failed)?;
            Ok(Value::Null)
        }
    });

    // ---- network
    let e = engine.clone();
    s.register("network.get", move |r| network_get(e.clone(), r));
    let e = engine.clone();
    s.register("network.toggle", move |r| {
        let e = e.clone();
        async move {
            let name = r.str("name")?.to_string();
            let network = e
                .bar
                .networks
                .lock()
                .unwrap()
                .iter()
                .find(|n| n.name == name)
                .cloned()
                .ok_or_else(|| bad("Unknown network. Refresh the list."))?;
            if !network.connected && network.profile.is_empty() {
                return Ok(json!({ "needsWindows": true }));
            }
            tokio::task::spawn_blocking(move || if network.connected { wifi::disconnect(&network) } else { wifi::connect_saved(&network) })
                .await
                .map_err(|_| IpcError::failed("stopped"))?
                .map_err(IpcError::failed)?;
            Ok(json!({ "needsWindows": false }))
        }
    });
}

async fn network_get(e: Arc<Engine>, r: IpcRequest) -> IpcResult {
    let scan = r.bool_or("scan", false);
    // Windows' Wi-Fi service can stall; the menu must still answer.
    let work = tokio::task::spawn_blocking(move || (status::adapters(), wifi::read(scan)));
    let (adapters, wireless) = tokio::time::timeout(Duration::from_secs(12), work)
        .await
        .map_err(|_| IpcError::failed("The Wi-Fi service did not answer."))?
        .map_err(|_| IpcError::failed("stopped"))?;
    *e.bar.networks.lock().unwrap() = wireless.networks.clone();
    let adapter_list: Vec<Value> = adapters.iter().map(|a| json!({ "name": a.name, "kind": a.kind, "address": a.address })).collect();
    let networks: Vec<Value> = wireless
        .networks
        .iter()
        .map(|n| json!({ "name": n.name, "signal": n.signal, "connected": n.connected, "secured": n.secured, "available": n.available, "saved": !n.profile.is_empty() }))
        .collect();
    Ok(json!({ "adapters": adapter_list, "networks": networks, "error": wireless.error }))
}

/// Timers and event hooks of the bar. Runs once at start, on the UI-loop thread.
pub fn wire(engine: &Arc<Engine>) {
    let (e1, e2, e3, e4) = (engine.clone(), engine.clone(), engine.clone(), engine.clone());
    let timers = Timers {
        // The keyboard layout is a property of the focused window, so it is read on a short timer, only while a UI is listening.
        language: UiTimer::new(&engine.ui, move || e1.publish_language()),
        // A slow safety net next to the foreground events: some windows change size without any event.
        space: UiTimer::new(&engine.ui, move || e2.publish_space()),
        fullscreen: UiTimer::new(&engine.ui, move || {
            e3.publish_fullscreen();
            e3.publish_space();
        }),
        network: UiTimer::new(&engine.ui, move || e4.check_network_available()),
    };
    timers.language.repeat(Duration::from_millis(1000), Duration::from_millis(300));
    timers.space.repeat(Duration::from_millis(2000), Duration::from_millis(1000));
    let _ = engine.bar.timers.set(timers);

    let e = engine.clone();
    engine.running.native_window_changed.subscribe(move |(kind, hwnd)| {
        if e.shutting_down.load(Ordering::SeqCst) || !e.settings.current().top_bar_enabled {
            return;
        }
        let foreground = native::hwnd_id(native::foreground());
        let changed = *kind == WindowEventKind::Foreground;
        if !changed && !(*kind == WindowEventKind::LocationChanged && *hwnd == foreground) {
            return;
        }
        // Batch movement and always inspect the final geometry.
        if let Some(t) = e.bar.timers.get() {
            t.fullscreen.once(Duration::from_millis(if changed { 0 } else { 50 }));
        }
    });
    let e = engine.clone();
    engine.running.changed.subscribe(move |_| {
        let e2 = e.clone();
        e.ui.post(move || e2.publish_space());
    });

    let e = engine.clone();
    *engine.bar.network_watcher.lock().unwrap() = status::watch_network_changes(move || {
        if let Some(t) = e.bar.timers.get() {
            t.network.once(Duration::from_millis(400)); // many interface events come in a burst
        }
    });
}
