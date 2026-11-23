//! IPC handlers. Method names, parameters and results mirror the C# engine (`EngineHost.*.cs`); see `PROTOCOL.md`.

use std::sync::Arc;

use fd_core::interface_text;
use fd_ipc::server::{IpcError, IpcRequest, IpcResult};
use serde_json::{json, Map, Value};

use crate::engine::{Engine, VERSION};
use crate::log;

type SyncHandler = fn(&Arc<Engine>, IpcRequest) -> IpcResult;

fn sync(engine: &Arc<Engine>, method: &str, handler: SyncHandler) {
    let e = engine.clone();
    engine.server.register_sync(method, move |r| handler(&e, r));
}

pub fn register(engine: &Arc<Engine>) {
    // ---- state
    sync(engine, "settings.get", |e, _| Ok(e.settings.current().to_value()));
    sync(engine, "settings.set", settings_set);
    sync(engine, "settings.reset", settings_reset);
    sync(engine, "text.table", text_table);

    // ---- app
    sync(engine, "app.info", |e, _| {
        Ok(json!({
            "version": VERSION,
            "dataPath": e.paths.root.to_string_lossy(),
            "logsPath": e.paths.logs_dir().to_string_lossy(),
            "packsPath": e.paths.icon_packs_dir().to_string_lossy(),
            "pid": std::process::id(),
            "diagnostic": e.diagnostic,
        }))
    });
    sync(engine, "engine.quit", |e, r| Ok(e.quit(&r.session.client_name().unwrap_or_else(|| "?".into()))));
    sync(engine, "app.quit", |e, r| Ok(e.quit(&r.session.client_name().unwrap_or_else(|| "?".into()))));
    sync(engine, "app.restart", |e, _| {
        let e2 = e.clone();
        e.ui.post(move || e2.restart());
        Ok(json!({ "restarting": true }))
    });
    // A second launch of the program, or any tool, asks the UI to do something it alone can show.
    sync(engine, "app.activate", |e, _| {
        e.ui_command("settings", None);
        Ok(Value::Null)
    });
    sync(engine, "ui.command", |e, r| {
        let name = r.str("command")?;
        e.ui_command(name, r.object("data"));
        Ok(Value::Null)
    });
    // Switches between the new interface and the WinUI application; both read the same profile.
    sync(engine, "interface.switch", |e, r| {
        let mode = r.str("mode")?;
        if mode != "Flutter" && mode != "Classic" {
            return Err(IpcError::bad_params("mode must be Flutter or Classic."));
        }
        e.settings.update(|s| s.ui_mode = mode.to_string());
        e.settings.flush();
        let e2 = e.clone();
        e.ui.post(move || e2.restart());
        Ok(json!({ "restarting": true }))
    });
    sync(engine, "dock.setHidden", |e, r| {
        let hidden = r.bool_or("hidden", false);
        let e2 = e.clone();
        e.ui.post(move || e2.set_dock_user_hidden(hidden));
        Ok(Value::Null)
    });

    // ---- pinned apps and Launchpad tiles
    sync(engine, "pinned.get", |e, _| Ok(e.pinned_node()));
    sync(engine, "pinned.move", |e, r| {
        e.pinned.move_application(r.str("key")?, r.i64_or("index", 0));
        Ok(Value::Null)
    });
    sync(engine, "pinned.reorder", |e, r| {
        e.pinned.move_to(r.str("key")?, r.i64_or("index", 0));
        Ok(Value::Null)
    });
    sync(engine, "pinned.remove", |e, r| Ok(json!({ "removed": e.pinned.remove(r.str("key")?) })));
    sync(engine, "pinned.addSeparator", |e, r| {
        let pins = e.pinned.snapshot();
        let at = r.opt_str("afterKey").and_then(|after| pins.iter().position(|p| p.key() == after));
        e.pinned.add(&fd_core::AppRef::separator(), Some(at.map_or(pins.len(), |i| i + 1)));
        Ok(Value::Null)
    });
    sync(engine, "tiles.get", |e, _| Ok(Value::Array(e.tiles.snapshot().iter().map(|a| a.to_node()).collect())));
    sync(engine, "tiles.remove", |e, r| Ok(json!({ "removed": e.tiles.remove(r.str("key")?) })));
    sync(engine, "tiles.move", |e, r| {
        e.tiles.move_to(r.str("key")?, r.i64_or("index", 0));
        Ok(Value::Null)
    });
}

fn settings_set(e: &Arc<Engine>, r: IpcRequest) -> IpcResult {
    let patch: &Map<String, Value> = r
        .object("patch")
        .ok_or_else(|| IpcError::bad_params("patch must be an object of setting names and values."))?;
    let next = e.settings.current().apply_patch(patch).map_err(|err| IpcError::bad_params(err.to_string()))?;
    e.settings.update(|target| *target = next);
    Ok(e.settings.current().to_value())
}

fn settings_reset(e: &Arc<Engine>, _r: IpcRequest) -> IpcResult {
    let mut defaults = fd_core::DockSettings::default();
    defaults.ui_mode = e.settings.current().ui_mode.clone(); // resetting the dock must not switch interfaces
    e.settings.update(|target| *target = defaults);
    log::info("settings were reset to the defaults");
    Ok(e.settings.current().to_value())
}

fn text_table(e: &Arc<Engine>, _r: IpcRequest) -> IpcResult {
    let language = e.settings.current().language;
    let mut table = Map::new();
    for (english, translated) in interface_text::table(language) {
        table.insert(english, Value::String(translated));
    }
    Ok(json!({ "language": language.as_str(), "table": table }))
}
