//! The picture of an app as PNG bytes: user override > active icon pack > Windows shell. Shared by every UI, so the dock,
//! Launchpad and settings always show the same icon. Handlers for `icons.*`, `appicons.list` and `packs.*`.

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use base64::Engine as _;
use fd_core::icons::image_probe::{self, MAX_FILE_BYTES};
use fd_core::{AppMatching, AppRef};
use fd_ipc::server::{IpcError, IpcRequest};
use indexmap::IndexMap;
use serde_json::{json, Value};

use crate::engine::Engine;
use crate::log;

const MAX_CACHE_ENTRIES: usize = 512;

#[derive(Default)]
pub struct IconCache {
    generation: Mutex<u64>,
    map: Mutex<IndexMap<String, Option<Arc<Vec<u8>>>>>,
}

/// The file's bytes if it is a valid, small-enough PNG / ICO; `None` (and a log line) otherwise.
pub fn read_limited(file: Option<&Path>) -> Option<Vec<u8>> {
    let file = file?;
    let meta = std::fs::metadata(file).ok()?;
    if !meta.is_file() || meta.len() > MAX_FILE_BYTES {
        return None;
    }
    let bytes = std::fs::read(file).ok()?;
    match image_probe::probe_bytes(&bytes) {
        Ok(_) => Some(bytes),
        Err(why) => {
            log::warn(format!("icon rejected ({}): {why}", file.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()));
            None
        }
    }
}

fn require_app(r: &IpcRequest) -> Result<AppRef, IpcError> {
    let v = r.params.get("app").ok_or_else(|| IpcError::bad_params("app is required."))?;
    serde_json::from_value::<AppRef>(v.clone()).map_err(|e| IpcError::bad_params(format!("app is not an app description: {e}")))
}

impl Engine {
    /// Raised after overrides or packs changed; UIs reload their images.
    pub fn invalidate_icons(&self) {
        *self.icon_cache.generation.lock().unwrap() += 1;
        self.icon_cache.map.lock().unwrap().clear();
        self.server.publish("icons.changed", Value::Null);
    }

    pub async fn icon_png(self: &Arc<Self>, app: AppRef, px: i64, system_fallback: bool) -> Option<Arc<Vec<u8>>> {
        let px = px.clamp(16, 256) as i32;
        let generation = *self.icon_cache.generation.lock().unwrap();
        let key = format!("{generation}|{}|{px}|{system_fallback}", app.key());
        if let Some(hit) = self.icon_cache.map.lock().unwrap().get(&key) {
            return hit.clone();
        }
        let result = self.load_icon(&app, px, system_fallback).await.map(Arc::new);
        if *self.icon_cache.generation.lock().unwrap() == generation {
            let mut map = self.icon_cache.map.lock().unwrap();
            if map.len() >= MAX_CACHE_ENTRIES {
                map.shift_remove_index(0);
            }
            map.insert(key, result.clone());
        }
        result
    }

    async fn load_icon(self: &Arc<Self>, app: &AppRef, px: i32, system_fallback: bool) -> Option<Vec<u8>> {
        let resolver = self.resolver.clone();
        let a = app.clone();
        let from_pack = tokio::task::spawn_blocking(move || read_limited(resolver.resolve(&a).map(|m| m.path).as_deref())).await.ok()?;
        if from_pack.is_some() || !system_fallback {
            return from_pack;
        }
        if AppMatching::is_explorer(app) {
            let bundled = std::env::current_exe().ok().and_then(|e| e.parent().map(|d| d.join("Assets").join("Explorer.png")));
            if let Some(b) = read_limited(bundled.as_deref()) {
                return Some(b);
            }
        }
        let extractor = self.extractor.clone();
        let a = app.clone();
        let file: Option<PathBuf> = self.worker.run(move || extractor.get_icon_png(&a, px)).await.ok()?;
        tokio::task::spawn_blocking(move || read_limited(file.as_deref())).await.ok()?
    }

    pub fn has_icon_override(&self, app: &AppRef) -> bool {
        self.overrides.has(app)
    }

    fn all_apps_for_icons(&self) -> Vec<AppRef> {
        let mut seen = HashSet::new();
        self.pinned.snapshot().into_iter().chain(self.tiles.snapshot()).filter(|a| !a.is_separator && seen.insert(a.key())).collect()
    }

    pub fn schedule_pack_reload(&self) {
        if let Some(t) = self.pack_timer.get() {
            t.once(Duration::from_millis(600));
        }
    }

    fn after_packs_changed(self: &Arc<Self>) {
        let e = self.clone();
        self.ui.post(move || {
            e.invalidate_icons();
            e.server.publish("packs.changed", Value::Null);
        });
    }
}

fn node_for_pack(p: &fd_core::icons::IconPack) -> Value {
    json!({
        "id": p.id,
        "folder": p.directory.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
        "name": p.name, "version": p.version, "description": p.description,
        "iconCount": p.icon_count(), "isBuiltIn": p.is_built_in, "warnings": p.warnings,
    })
}

pub fn register(engine: &Arc<Engine>) {
    let s = &engine.server;

    let e = engine.clone();
    s.register("icons.get", move |r| {
        let e = e.clone();
        async move {
            let app = require_app(&r)?;
            let bytes = e.icon_png(app, r.i64_or("size", 48), r.bool_or("fallback", true)).await;
            Ok(json!({ "image": bytes.map(|b| base64::engine::general_purpose::STANDARD.encode(b.as_slice())) }))
        }
    });

    let e = engine.clone();
    s.register_sync("appicons.list", move |_| {
        let list: Vec<Value> = e
            .all_apps_for_icons()
            .iter()
            .map(|a| {
                let mut node = a.to_node();
                node["hasOverride"] = Value::Bool(e.overrides.has(a));
                node
            })
            .collect();
        Ok(Value::Array(list))
    });

    let e = engine.clone();
    s.register("icons.override", move |r| {
        let e = e.clone();
        async move {
            let key = r.str("key")?.to_string();
            let path = r.str("path")?.to_string();
            let p = PathBuf::from(&path);
            let bytes = tokio::task::spawn_blocking(move || read_limited(Some(&p))).await.ok().flatten()
                .ok_or_else(|| IpcError::bad_params("The file is not a valid PNG or ICO within the icon limits."))?;
            e.overrides.set(&key, Path::new(&path), Some(&bytes)).map_err(IpcError::failed)?;
            Ok(Value::Null)
        }
    });

    let e = engine.clone();
    s.register_sync("icons.reset", move |r| Ok(json!({ "reset": e.overrides.reset(r.str("key")?).map_err(IpcError::failed)? })));

    let e = engine.clone();
    s.register_sync("packs.list", move |_| Ok(Value::Array(e.packs.list_packs().iter().map(node_for_pack).collect())));

    let e = engine.clone();
    s.register_sync("packs.apply", move |r| {
        let pack = e.packs.load(r.str("folder")?).ok_or_else(|| IpcError::bad_params("Unknown icon pack."))?;
        let folder = pack.directory.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        e.settings.update(|d| {
            d.active_icon_pack = folder.clone();
            d.use_icon_pack = true;
        });
        let dark = e.settings.current().prefer_dark_variants;
        for app in e.all_apps_for_icons() {
            if pack.find(&app.icon_names(), dark).is_some() {
                let _ = e.overrides.reset_for_app(&app);
            }
        }
        let e2 = e.clone();
        e.ui.post(move || e2.invalidate_icons());
        Ok(Value::Null)
    });

    let e = engine.clone();
    s.register("packs.inspect", move |r| {
        let e = e.clone();
        async move {
            let path = PathBuf::from(r.str("path")?);
            let preview = tokio::task::spawn_blocking(move || e.packs.inspect(&path)).await
                .map_err(|_| IpcError::failed("The task stopped unexpectedly."))?
                .map_err(|x| IpcError::bad_params(x.0))?;
            Ok(json!({
                "name": preview.name, "version": preview.version, "description": preview.description, "iconCount": preview.icon_count,
                "iconNames": preview.icon_names.iter().take(20).collect::<Vec<_>>(), "problems": preview.problems,
            }))
        }
    });

    let e = engine.clone();
    s.register("packs.import", move |r| {
        let e = e.clone();
        async move {
            let path = PathBuf::from(r.str("path")?);
            let packs = e.packs.clone();
            let pack = tokio::task::spawn_blocking(move || packs.import(&path, true)).await
                .map_err(|_| IpcError::failed("The task stopped unexpectedly."))?
                .map_err(|x| IpcError::bad_params(x.0))?;
            let folder = pack.directory.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            e.settings.update(|d| {
                d.active_icon_pack = folder.clone();
                d.use_icon_pack = true;
            });
            e.resolver.reload();
            e.after_packs_changed();
            Ok(json!({ "folder": folder }))
        }
    });

    let e = engine.clone();
    s.register("packs.export", move |r| {
        let e = e.clone();
        async move {
            let (folder, path) = (r.str("folder")?.to_string(), PathBuf::from(r.str("path")?));
            tokio::task::spawn_blocking(move || e.packs.export(&folder, &path)).await
                .map_err(|_| IpcError::failed("The task stopped unexpectedly."))?
                .map_err(|x| IpcError::bad_params(x.0))?;
            Ok(Value::Null)
        }
    });

    let e = engine.clone();
    s.register_sync("packs.delete", move |r| {
        e.packs.delete(r.str("folder")?);
        e.settings.update(|d| d.active_icon_pack = "Default".into());
        e.resolver.reload();
        e.after_packs_changed();
        Ok(Value::Null)
    });

    let e = engine.clone();
    s.register_sync("packs.reload", move |_| {
        e.resolver.reload();
        e.after_packs_changed();
        Ok(Value::Null)
    });
}

/// Watches the pack folders: a pack edited by hand shows up without a restart.
pub fn watch_packs(engine: &Arc<Engine>) {
    use notify::{RecursiveMode, Watcher};
    let e = engine.clone();
    let watcher = notify::recommended_watcher(move |res: notify::Result<notify::Event>| {
        if res.is_ok() {
            e.schedule_pack_reload();
        }
    });
    match watcher {
        Ok(mut w) => match w.watch(engine.packs.root_directory(), RecursiveMode::Recursive) {
            Ok(()) => *engine.pack_watcher.lock().unwrap() = Some(Box::new(w)),
            Err(e) => log::warn(format!("icon pack watcher failed: {e}")),
        },
        Err(e) => log::warn(format!("icon pack watcher failed: {e}")),
    }
}
