//! The engine's composition root: creates every service once and exposes them over IPC. Everything that touches the shell
//! runs on the UI-loop thread; IPC handlers hop onto it where a service needs that.

use std::collections::HashSet;
use std::process::Command;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use fd_core::icons::{IconOverrideStore, IconPackService, IconResolver};
use fd_core::{AppListStore, AppPaths, DockSettings, Event, SettingsStore};
use fd_ipc::{endpoint, protocol, IpcServer};
use fd_windows::icon_extractor::ShellIconExtractor;
use fd_windows::{RunningApps, ShellWorker, UiLoop, UiTimer};
use serde_json::{json, Map, Value};

use crate::log;

pub const VERSION: &str = env!("CARGO_PKG_VERSION");

pub struct Engine {
    pub paths: AppPaths,
    pub ui: Arc<UiLoop>,
    pub args: Vec<String>,
    pub diagnostic: bool,
    pub settings: SettingsStore,
    pub pinned: AppListStore,
    pub tiles: AppListStore,
    pub server: IpcServer,
    pub worker: Arc<ShellWorker>,
    pub packs: Arc<IconPackService>,
    pub overrides: Arc<IconOverrideStore>,
    pub resolver: Arc<IconResolver>,
    pub extractor: Arc<ShellIconExtractor>,
    pub icon_cache: crate::icons::IconCache,
    pub launchpad: crate::launchpad::LaunchpadState,
    pub bar: crate::bar::BarState,
    pub sys: crate::system::SystemState,
    pub media: crate::media::MediaState2,
    pub capture: crate::capture::CaptureState,
    pub pack_watcher: Mutex<Option<Box<dyn std::any::Any + Send>>>,
    pub pack_timer: OnceLock<UiTimer>,
    pub settings_timer: OnceLock<UiTimer>,
    pub applied: Mutex<DockSettings>,
    pub running: Arc<RunningApps>,
    /// Process ids of the UI processes (their windows are not apps).
    pub ui_pids: Arc<Mutex<HashSet<i64>>>,
    pub dock: crate::dock::DockState,
    pub dock_timer: OnceLock<UiTimer>,
    pub shutting_down: AtomicBool,
    pub dock_user_hidden: AtomicBool,
    /// Raised on the UI-loop thread when the engine should stop (quit command, tray exit).
    pub quit_requested: Event<()>,
}

impl Engine {
    /// Must run on the UI-loop thread: the WinEvent hooks and the message window belong to it.
    pub fn start(paths: AppPaths, ui: Arc<UiLoop>, args: Vec<String>, diagnostic: bool) -> Arc<Engine> {
        if let Err(e) = paths.ensure_created() {
            log::error(format!("the data folders could not be created: {e}"));
        }
        log::info(format!("engine {VERSION} starting, pid {}", std::process::id()));

        let settings = SettingsStore::open(&paths.config_file());
        settings.save_failed().subscribe(|e| log::warn(format!("settings could not be saved: {e}")));
        // The installer's "Start with Windows" task: the program owns this setting, so the installer just asks for it.
        if args.iter().any(|a| a == "--enable-autostart") && !settings.current().start_with_windows {
            settings.update(|s| s.start_with_windows = true);
        }
        // A diagnostic profile must never touch the user's desktop: leave the taskbar alone and do not autostart.
        if diagnostic {
            settings.update(|s| {
                s.taskbar_mode = fd_core::settings::TaskbarMode::Leave;
                s.start_with_windows = false;
            });
        }
        let first_run = !paths.pinned_file().exists();
        let pinned = AppListStore::open(&paths.pinned_file());
        let tiles = AppListStore::open(&paths.app_library_file());
        pinned.save_failed().subscribe(|e| log::warn(format!("pinned apps could not be saved: {e}")));
        tiles.save_failed().subscribe(|e| log::warn(format!("Launchpad tiles could not be saved: {e}")));

        let packs = Arc::new(IconPackService::new(&paths.icon_packs_dir(), &paths.cache_dir().join("tmp")));
        packs.remove_legacy_bundled_packs();
        if packs.load(&settings.current().active_icon_pack).is_none() {
            settings.update(|s| s.active_icon_pack = fd_core::icons::pack_service::DEFAULT_PACK_ID.to_string());
        }
        let overrides = Arc::new(IconOverrideStore::open(&paths.icon_overrides_file(), &paths.user_icons_dir()));
        let resolver = Arc::new(IconResolver::new(overrides.clone(), packs.clone()));
        let initial = settings.current();
        resolver.configure(initial.use_icon_pack, &initial.active_icon_pack, initial.prefer_dark_variants);
        let extractor = Arc::new(ShellIconExtractor::new(&paths.system_icon_cache_dir()));
        let worker = Arc::new(ShellWorker::new());
        let running = RunningApps::new(worker.clone(), ui.clone());
        let ui_pids: Arc<Mutex<HashSet<i64>>> = Arc::default();
        let pids = ui_pids.clone();
        fd_windows::window_filter::set_extra_own_process(move |pid| pids.lock().unwrap().contains(&(pid as i64)));

        let engine = Arc::new(Engine {
            paths,
            ui,
            args,
            diagnostic,
            settings,
            pinned,
            tiles,
            server: IpcServer::new(VERSION),
            worker,
            packs,
            overrides,
            resolver,
            extractor,
            icon_cache: Default::default(),
            launchpad: Default::default(),
            bar: Default::default(),
            sys: Default::default(),
            media: Default::default(),
            capture: Default::default(),
            pack_watcher: Mutex::new(None),
            pack_timer: OnceLock::new(),
            settings_timer: OnceLock::new(),
            applied: Mutex::new((*initial).clone()),
            running,
            ui_pids,
            dock: Default::default(),
            dock_timer: OnceLock::new(),
            shutting_down: AtomicBool::new(false),
            dock_user_hidden: AtomicBool::new(false),
            quit_requested: Event::new(),
        });

        // the UI's own windows must not show up as apps
        let pids = engine.ui_pids.clone();
        engine.server.session_hello().subscribe(move |s| {
            if s.client_pid() != 0 {
                pids.lock().unwrap().insert(s.client_pid());
            }
        });
        let pids = engine.ui_pids.clone();
        engine.server.session_closed().subscribe(move |s| {
            if s.client_pid() != 0 {
                pids.lock().unwrap().remove(&s.client_pid());
            }
        });
        let e = engine.clone();
        engine.server.session_opened().subscribe(move |s| log::info(format!("client #{} connected", s.id())));
        engine
            .server
            .session_closed()
            .subscribe(move |s| log::info(format!("client #{} ({}) disconnected", s.id(), s.client_name().unwrap_or_default())));
        drop(e);

        crate::handlers::register(&engine);
        crate::dock::register(&engine);
        crate::icons::register(&engine);
        crate::launchpad::register(&engine);
        crate::bar::register(&engine);
        crate::system::register(&engine);
        crate::media::register(&engine);
        if let Err(e) = engine.server.start() {
            log::error(format!("the IPC server could not start: {e}"));
        }
        endpoint::publish(&engine.paths.root, engine.server.endpoint());

        let e = engine.clone();
        engine.settings.changed().subscribe(move |s| e.server.publish(protocol::events::SETTINGS_CHANGED, s.to_value()));
        let e = engine.clone();
        engine.pinned.changed().subscribe(move |_| e.server.publish(protocol::events::PINNED_CHANGED, e.pinned_node()));
        let e = engine.clone();
        engine.tiles.changed().subscribe(move |_| {
            e.server.publish("tiles.changed", Value::Null);
            e.server.publish("launchpad.changed", Value::Null);
        });

        crate::dock::wire(&engine);
        crate::bar::wire(&engine);
        crate::system::start(&engine);
        crate::media::wire(&engine);
        engine.start_capture_watch();

        let e = engine.clone();
        let _ = engine.settings_timer.set(UiTimer::new(&engine.ui, move || e.apply_pending_settings()));
        let e = engine.clone();
        engine.settings.changed().subscribe(move |_| e.schedule_settings_apply());
        let e = engine.clone();
        let _ = engine.pack_timer.set(UiTimer::new(&engine.ui, move || {
            e.resolver.reload();
            e.invalidate_icons();
            e.server.publish("packs.changed", Value::Null);
        }));
        let e = engine.clone();
        engine.overrides.changed.subscribe(move |_| {
            let e2 = e.clone();
            e.ui.post(move || e2.invalidate_icons());
        });
        crate::icons::watch_packs(&engine);
        engine.running.start();
        if first_run {
            engine.seed_pinned_from_windows_taskbar();
        }
        if !engine.diagnostic {
            // warm the Launchpad catalogue so that the first open is instant
            let e = engine.clone();
            engine.ui.post_after(Duration::from_millis(600), move || {
                let e2 = e.clone();
                e.server.spawn(async move {
                    if let Err(err) = e2.start_pins(false).await {
                        log::warn(format!("Start pins preload failed: {}", err.message));
                    }
                });
            });
        }

        let ep = engine.server.endpoint();
        let shown = ep.rsplit_once(':').map(|(a, _)| a).unwrap_or(ep);
        log::info(format!(
            "engine ready on {shown} (token hidden) (profile {}, settings {:?})",
            engine.paths.root.display(),
            engine.settings.load_status()
        ));
        engine
    }

    pub fn is_ui_process(&self, pid: i64) -> bool {
        pid == std::process::id() as i64 || self.ui_pids.lock().unwrap().contains(&pid)
    }

    pub fn pinned_node(&self) -> Value {
        Value::Array(self.pinned.snapshot().iter().map(|a| a.to_node()).collect())
    }

    /// Tells the UI to do something that only it can show (open a window...).
    pub fn ui_command(&self, name: &str, extra: Option<&Map<String, Value>>) {
        let mut data = extra.cloned().unwrap_or_default();
        data.insert("command".into(), Value::String(name.to_string()));
        self.server.publish("ui.command", Value::Object(data));
    }

    pub fn set_dock_user_hidden(&self, hidden: bool) {
        self.dock_user_hidden.store(hidden, Ordering::SeqCst);
        self.server.publish("dock.hiddenChanged", json!({ "hidden": hidden }));
    }

    /// A quit request: the reply goes out first, then the engine stops.
    pub fn quit(self: &Arc<Self>, who: &str) -> Value {
        log::info(format!("quit requested by {who}"));
        let e = self.clone();
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(150));
            let e2 = e.clone();
            e.ui.post(move || e2.shutdown());
        });
        json!({ "quitting": true })
    }

    pub fn restart(self: &Arc<Self>) {
        match std::env::current_exe() {
            Ok(exe) => {
                let mut cmd = Command::new(exe);
                cmd.arg("--wait-for-exit").arg(std::process::id().to_string());
                cmd.args(self.args.iter().filter(|a| *a != "--quit"));
                if let Err(e) = cmd.spawn() {
                    log::error(format!("restart failed: {e}"));
                    return;
                }
            }
            Err(e) => {
                log::error(format!("restart failed: {e}"));
                return;
            }
        }
        self.shutdown();
    }

    pub fn shutdown(&self) {
        if self.shutting_down.swap(true, Ordering::SeqCst) {
            return;
        }
        if let Some(tc) = self.sys.taskbar.get() {
            let r = tc.restore();
            if !r.success() {
                log::warn(format!("taskbar restore on exit failed: {}", r.message));
            }
        }
        self.server.publish("engine.stopping", Value::Null);
        self.quit_requested.emit(&());
    }

    /// Final cleanup after the message loop ended.
    pub fn dispose(&self) {
        self.shutting_down.store(true, Ordering::SeqCst);
        log::info("stopping: running apps");
        self.running.dispose();
        self.dispose_capture();
        self.dispose_dock();
        log::info("stopping: system services");
        self.dispose_system();
        log::info("stopping: ipc server");
        self.server.shutdown();
        endpoint::clear_published(&self.paths.root);
        log::info("stopping: shell worker");
        self.worker.dispose();
        log::info("stopping: settings");
        self.settings.flush();
        log::info("engine stopped");
    }
}
