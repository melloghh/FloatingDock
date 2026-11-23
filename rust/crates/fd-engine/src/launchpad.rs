//! Launchpad data and the app actions shared by the Launchpad, search and the dock: launch, pin, reveal, tiles; plus search and
//! the "shell" commands (open a folder, open a Settings page, power).

use std::collections::HashSet;
use std::path::PathBuf;
use std::process::Command;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use fd_core::search::{self, AppSearchIndex, SearchUsage, StartLayoutPin};
use fd_core::{json_file, AppRef, AppSource, DockSettings};
use fd_ipc::server::{IpcError, IpcRequest, IpcResult};
use fd_windows::{catalog, launcher, tile_import};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::engine::Engine;
use crate::log;

#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct StartPins {
    pub apps: Vec<AppRef>,
    pub exported_pins: usize,
    pub warnings: Vec<String>,
}

/// The cached read of the apps pinned in Windows Start (the Launchpad's catalogue).
#[derive(Default)]
pub struct LaunchpadState {
    cached: Mutex<Option<(StartPins, Instant)>>,
    usage: Mutex<Option<Arc<SearchUsage>>>,
    catalog: Mutex<Option<(Vec<AppRef>, Instant)>>,
    index: Mutex<Option<(Arc<AppSearchIndex>, Vec<AppRef>)>>,
}

fn require_app(r: &IpcRequest) -> Result<AppRef, IpcError> {
    let v = r.params.get("app").ok_or_else(|| IpcError::bad_params("app is required."))?;
    serde_json::from_value::<AppRef>(v.clone()).map_err(|e| IpcError::bad_params(format!("app is not an app description: {e}")))
}

fn apps_node(apps: &[AppRef]) -> Value {
    Value::Array(apps.iter().map(|a| a.to_node()).collect())
}

/// Export-StartLayout is the supported, read-only way to read the Start pins (never Start's private databases).
fn export_start_pins() -> Result<Vec<StartLayoutPin>, String> {
    use std::os::windows::process::CommandExt;
    let mut rnd = [0u8; 8];
    let _ = getrandom::fill(&mut rnd);
    let temporary = std::env::temp_dir().join(format!("FloatingDock-Start-{}.json", hex::encode(rnd)));
    let windir = std::env::var("SystemRoot").unwrap_or_else(|_| "C:\\Windows".into());
    let exe = PathBuf::from(windir).join("System32").join("WindowsPowerShell").join("v1.0").join("powershell.exe");
    let command = format!("Export-StartLayout -Path '{}' -ErrorAction Stop", temporary.to_string_lossy().replace('\'', "''"));
    let mut child = Command::new(exe)
        .args(["-NoProfile", "-NonInteractive", "-Command", &command])
        .creation_flags(0x0800_0000) // CREATE_NO_WINDOW
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::piped())
        .spawn()
        .map_err(|e| format!("Could not export the Windows Start pins. {e}"))?;
    let deadline = Instant::now() + Duration::from_secs(20);
    let status = loop {
        match child.try_wait() {
            Ok(Some(s)) => break s,
            Ok(None) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(50)),
            _ => {
                let _ = child.kill();
                return Err("Windows could not export Start pins. (timed out)".into());
            }
        }
    };
    let result = if status.success() && temporary.is_file() {
        std::fs::read_to_string(&temporary).map_err(|e| e.to_string()).and_then(|t| search::parse_start_layout_pins(t.trim_start_matches('\u{feff}')))
    } else {
        Err("Windows could not export Start pins.".to_string())
    };
    let _ = std::fs::remove_file(&temporary);
    result
}

/// Must run on the shell worker (STA).
fn resolve_pins(pins: &[StartLayoutPin]) -> StartPins {
    let (mut apps, mut warnings) = (Vec::new(), Vec::new());
    let mut catalog_cache: Option<Vec<AppRef>> = None;
    let mut seen = HashSet::new();
    for pin in pins {
        let app = if pin.kind == "desktopAppLink" {
            let path = catalog::expand_env(&pin.value);
            std::path::Path::new(&path).is_file().then(|| catalog::app_from_shortcut(&path, AppSource::StartMenu)).flatten()
        } else {
            let cat = catalog_cache.get_or_insert_with(catalog::load);
            cat.iter()
                .find(|a| {
                    a.aumid.as_deref().is_some_and(|x| x.eq_ignore_ascii_case(&pin.value))
                        || a.shell_parsing_name.as_deref().is_some_and(|x| x.eq_ignore_ascii_case(&pin.value))
                })
                .cloned()
        };
        match app {
            None => warnings.push("An unavailable Start pin was skipped.".to_string()),
            Some(a) => {
                if seen.insert(a.key()) {
                    apps.push(a);
                }
            }
        }
    }
    StartPins { apps, exported_pins: pins.len(), warnings }
}

impl Engine {
    fn start_pins_cache_file(&self) -> PathBuf {
        self.paths.cache_dir().join("start-pins.json")
    }

    pub fn load_start_pins_cache(&self) {
        let file = self.start_pins_cache_file();
        if !file.is_file() || std::fs::metadata(&file).map(|m| m.len() >= 1024 * 1024).unwrap_or(true) {
            return;
        }
        let loaded = json_file::load(&file, |t| serde_json::from_str::<StartPins>(t).map_err(Into::into)).value;
        if let Some(saved) = loaded.filter(|s| s.apps.len() <= 256 && s.apps.iter().all(|a| !a.name.trim().is_empty())) {
            *self.launchpad.cached.lock().unwrap() = Some((saved, Instant::now() - Duration::from_secs(3600)));
        }
    }

    pub async fn start_pins(self: &Arc<Self>, force: bool) -> Result<StartPins, IpcError> {
        if !force {
            if let Some((c, at)) = self.launchpad.cached.lock().unwrap().as_ref() {
                if at.elapsed() < Duration::from_secs(120) {
                    return Ok(c.clone());
                }
            }
        }
        let pins = tokio::task::spawn_blocking(export_start_pins).await.map_err(|_| IpcError::failed("stopped"))?.map_err(IpcError::failed)?;
        let resolved = self.worker.run(move || resolve_pins(&pins)).await.map_err(|_| IpcError::failed("The engine is stopping."))?;
        *self.launchpad.cached.lock().unwrap() = Some((resolved.clone(), Instant::now()));
        let (file, copy) = (self.start_pins_cache_file(), resolved.clone());
        tokio::task::spawn_blocking(move || {
            if let Err(e) = json_file::save(&file, &copy) {
                log::warn(format!("Start pins cache could not be saved: {e}"));
            }
        });
        Ok(resolved)
    }

    fn usage(&self) -> Arc<SearchUsage> {
        let mut u = self.launchpad.usage.lock().unwrap();
        u.get_or_insert_with(|| Arc::new(SearchUsage::load(&self.paths.search_usage_file()))).clone()
    }

    fn invalidate_search_apps(&self) {
        *self.launchpad.index.lock().unwrap() = None;
    }

    async fn installed_apps(self: &Arc<Self>) -> Result<Vec<AppRef>, IpcError> {
        {
            let mut c = self.launchpad.catalog.lock().unwrap();
            if c.as_ref().is_some_and(|(_, t)| t.elapsed() > Duration::from_secs(300)) {
                *c = None;
                *self.launchpad.index.lock().unwrap() = None;
            }
            if let Some((apps, _)) = c.as_ref() {
                return Ok(apps.clone());
            }
        }
        let apps = self.worker.run(catalog::load).await.map_err(|_| IpcError::failed("The engine is stopping."))?;
        *self.launchpad.catalog.lock().unwrap() = Some((apps.clone(), Instant::now()));
        Ok(apps)
    }

    async fn search_query(self: &Arc<Self>, text: &str) -> IpcResult {
        let query = text.trim().to_string();
        let settings: Arc<DockSettings> = self.settings.current();
        let usage = self.usage();
        if query.is_empty() && !usage.has_entries() {
            return Ok(json!({ "results": [], "status": "idle" }));
        }
        let installed = self.installed_apps().await?;
        let (index, apps) = {
            let mut guard = self.launchpad.index.lock().unwrap();
            if guard.is_none() {
                let mut seen = HashSet::new();
                let all: Vec<AppRef> = self
                    .tiles
                    .snapshot()
                    .into_iter()
                    .chain(self.pinned.snapshot())
                    .chain(installed)
                    .filter(search::palette_includes)
                    .filter(|a| seen.insert(a.key()))
                    .collect();
                *guard = Some((Arc::new(AppSearchIndex::new(all.clone())), all));
            }
            guard.clone().unwrap()
        };
        let limit = settings.search_result_limit as usize;
        let mut results: Vec<Value> = Vec::new();
        if !query.is_empty() {
            let normalized = search::normalize(&query);
            for (id, name) in [("cmd:settings", "FloatingDock settings"), ("cmd:launchpad", "Launchpad")] {
                let shown = fd_core::interface_text::get(name, settings.language);
                if search::normalize(&shown).contains(&normalized) || search::normalize(name).contains(&normalized) {
                    results.push(json!({ "id": id, "kind": "Command", "name": name }));
                }
            }
        }
        let found = if query.is_empty() { usage.suggestions(&apps, 8.min(limit)) } else { index.search(&query) };
        for app in found.iter().take(limit) {
            results.push(json!({ "id": format!("app:{}", app.key()), "kind": "App", "name": app.name, "app": app.to_node() }));
        }
        let status = if results.is_empty() { "none" } else { "ok" };
        results.truncate(limit);
        Ok(json!({ "results": results, "status": status }))
    }

    async fn search_open(self: &Arc<Self>, id: &str) -> IpcResult {
        match id {
            "cmd:settings" => {
                self.ui_command("settings", None);
                return Ok(Value::Null);
            }
            "cmd:launchpad" => {
                self.ui_command("toggleLaunchpad", None);
                return Ok(Value::Null);
            }
            _ => {}
        }
        let Some(key) = id.strip_prefix("app:") else { return Err(IpcError::bad_params("Unknown result.")) };
        let app = self
            .launchpad
            .index
            .lock()
            .unwrap()
            .as_ref()
            .and_then(|(_, apps)| apps.iter().find(|a| a.key() == key).cloned())
            .ok_or_else(|| IpcError::bad_params("That result is no longer available."))?;
        let usage = self.usage();
        let file = self.paths.search_usage_file();
        let _ = self.worker.run(move || {
            if !launcher::launch(&app, false) {
                return;
            }
            usage.record(&app, search::iso_now());
            if let Err(e) = json_file::save(&file, &usage.snapshot()) {
                log::warn(format!("Search suggestions could not be saved: {e}"));
            }
        });
        Ok(Value::Null)
    }

    pub fn seed_pinned_from_windows_taskbar(self: &Arc<Self>) {
        let e = self.clone();
        let _ = self.worker.run(move || {
            for p in catalog::read_windows_taskbar_pins() {
                e.pinned.add(&p, None);
            }
        });
    }
}

pub fn register(engine: &Arc<Engine>) {
    let s = &engine.server;
    engine.load_start_pins_cache();
    let e = engine.clone();
    engine.pinned.changed().subscribe(move |_| e.invalidate_search_apps());
    let e = engine.clone();
    engine.tiles.changed().subscribe(move |_| e.invalidate_search_apps());

    let e = engine.clone();
    s.register("launchpad.apps", move |r| {
        let e = e.clone();
        async move {
            let catalog = e.start_pins(r.bool_or("refresh", false)).await?;
            let user = e.tiles.snapshot();
            let user_keys: HashSet<String> = user.iter().map(|u| u.key()).collect();
            let user_count = user.len();
            let all: Vec<AppRef> = user.into_iter().chain(catalog.apps.iter().filter(|a| !user_keys.contains(&a.key())).cloned()).collect();
            Ok(json!({ "apps": apps_node(&all), "exportedPins": catalog.exported_pins, "userCount": user_count }))
        }
    });

    let e = engine.clone();
    s.register_sync("apps.launch", move |r| {
        let (app, again) = (require_app(&r)?, r.bool_or("newInstance", false));
        let _ = e.worker.run(move || {
            if !launcher::launch(&app, again) {
                log::warn(format!("launch failed: {}", app.name));
            }
        });
        Ok(Value::Null)
    });
    s.register_sync("apps.reveal", |r| {
        let location = launcher::location_of(&require_app(&r)?);
        if let Some(l) = &location {
            launcher::reveal_in_explorer(l);
        }
        Ok(json!({ "ok": location.is_some() }))
    });
    let e = engine.clone();
    s.register_sync("apps.info", move |r| {
        let app = require_app(&r)?;
        let tiles: Vec<String> = e.tiles.snapshot().iter().map(|a| a.key()).collect();
        Ok(json!({
            "pinned": e.pinned.contains(&app.key()), "isTile": e.tiles.contains(&app.key()),
            "hasOverride": e.overrides.has(&app), "hasLocation": launcher::location_of(&app).is_some(),
            "tileIndex": tiles.iter().position(|k| *k == app.key()).map_or(-1, |i| i as i64), "tileCount": tiles.len(),
        }))
    });
    let e = engine.clone();
    s.register_sync("pinned.addApp", move |r| Ok(json!({ "added": e.pinned.add(&require_app(&r)?, None) })));

    let e = engine.clone();
    s.register("pinned.add", move |r| {
        let e = e.clone();
        async move {
            let path = r.str("path")?.to_string();
            let dir = e.paths.app_library_shortcuts_dir();
            let e2 = e.clone();
            let res = e.worker.run(move || tile_import::import_and_save(&path, &dir, &e2.pinned)).await.map_err(|_| IpcError::failed("stopping"))?;
            Ok(json!({ "status": res.status.as_str(), "message": res.message }))
        }
    });
    let e = engine.clone();
    s.register("tiles.add", move |r| {
        let e = e.clone();
        async move {
            let path = r.str("path")?.to_string();
            let dir = e.paths.app_library_shortcuts_dir();
            let e2 = e.clone();
            let res = e.worker.run(move || tile_import::import_and_save(&path, &dir, &e2.tiles)).await.map_err(|_| IpcError::failed("stopping"))?;
            Ok(json!({ "status": res.status.as_str(), "message": res.message }))
        }
    });
    let e = engine.clone();
    s.register("pinned.importTaskbar", move |_| {
        let e = e.clone();
        async move {
            let e2 = e.clone();
            let added = e
                .worker
                .run(move || catalog::read_windows_taskbar_pins().iter().filter(|p| e2.pinned.add(p, None)).count())
                .await
                .map_err(|_| IpcError::failed("stopping"))?;
            Ok(json!({ "added": added }))
        }
    });

    let e = engine.clone();
    s.register("search.query", move |r| {
        let e = e.clone();
        async move { e.search_query(r.opt_str("text").unwrap_or("")).await }
    });
    let e = engine.clone();
    s.register("search.open", move |r| {
        let e = e.clone();
        async move { e.search_open(r.str("id")?).await }
    });

    // ---- shell and power
    let e = engine.clone();
    s.register_sync("shell.open", move |r| {
        let folder = match r.str("target")? {
            "data" => e.paths.root.clone(),
            "logs" => e.paths.logs_dir(),
            "packs" => e.packs.root_directory().to_path_buf(),
            other => return Err(IpcError::bad_params(format!("Unknown folder: {other}"))),
        };
        launcher::open_folder(&folder);
        Ok(Value::Null)
    });
    let e = engine.clone();
    s.register_sync("shell.execute", move |r| {
        let uri = r.str("uri")?.to_string();
        // Only the Windows settings pages the menus link to; never an arbitrary command.
        if !(uri.starts_with("ms-settings:") || uri == "ms-availablenetworks:") {
            return Err(IpcError::bad_params("That link is not allowed."));
        }
        if !e.diagnostic {
            let _ = e.worker.run(move || launcher::execute(&uri, None, None));
        }
        Ok(Value::Null)
    });
    let e = engine.clone();
    s.register_sync("power.run", move |r| {
        use std::os::windows::process::CommandExt;
        let action = r.opt_str("action");
        if !matches!(action, Some("shutdown") | Some("restart")) {
            return Err(IpcError::bad_params("action must be shutdown or restart."));
        }
        // A diagnostic engine must never power off the machine it is being tested on.
        if e.diagnostic {
            return Ok(json!({ "ran": false }));
        }
        let windir = std::env::var("SystemRoot").unwrap_or_else(|_| "C:\\Windows".into());
        let started = Command::new(PathBuf::from(windir).join("System32").join("shutdown.exe"))
            .args([if action == Some("restart") { "/r" } else { "/s" }, "/t", "0"])
            .creation_flags(0x0800_0000)
            .spawn();
        match started {
            Ok(_) => Ok(json!({ "ran": true })),
            Err(err) => {
                log::warn(format!("Power action failed: {err}"));
                e.notify("FloatingDock", "Could not start the power action.", true);
                Ok(json!({ "ran": false }))
            }
        }
    });
}
