//! The media player of the bar (`media.get` / `media.control`) and the last pictures of windows (`window.snapshot`).

use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Arc;

use base64::Engine as _;
use fd_ipc::server::IpcError;
use fd_windows::media::{MediaController, MediaState};
use fd_windows::snapshot::WindowSnapshotCache;
use fd_windows::WindowEventKind;
use serde_json::{json, Value};

use crate::engine::Engine;

#[derive(Default)]
pub struct MediaState2 {
    pub controller: MediaController,
    pub snapshots: WindowSnapshotCache,
    last_foreground: AtomicI64,
}

fn media_node(m: &MediaState) -> Value {
    json!({
        "title": m.title, "artist": m.artist, "source": m.source, "canPrevious": m.previous, "canToggle": m.toggle, "canNext": m.next,
        "playing": m.playing, "position": m.position, "duration": m.duration, "canShuffle": m.can_shuffle, "shuffle": m.shuffle,
        "canRepeat": m.can_repeat, "repeat": m.repeat,
    })
}

impl Engine {
    fn is_dock_window(&self, id: i64) -> bool {
        self.running.snapshot().iter().any(|w| w.hwnd == id)
    }
}

pub fn register(engine: &Arc<Engine>) {
    let s = &engine.server;

    let e = engine.clone();
    s.register("media.get", move |_| {
        let e = e.clone();
        async move {
            let read = tokio::task::spawn_blocking(move || e.media.controller.read()).await.map_err(|_| IpcError::failed("stopped"))?;
            match read {
                Ok(Some(m)) => Ok(media_node(&m)),
                Ok(None) => Ok(Value::Null),
                Err(msg) => Err(IpcError::failed(msg)),
            }
        }
    });

    let e = engine.clone();
    s.register("media.control", move |r| {
        let e = e.clone();
        async move {
            let command = r.str("command")?.to_string();
            let ok = tokio::task::spawn_blocking(move || e.media.controller.control(&command))
                .await
                .map_err(|_| IpcError::failed("stopped"))?
                .map_err(IpcError::failed)?;
            Ok(json!({ "ok": ok }))
        }
    });

    let e = engine.clone();
    s.register_sync("window.snapshot", move |r| {
        let id = r.params.get("hwnd").and_then(Value::as_i64).ok_or_else(|| IpcError::bad_params("hwnd is required."))?;
        let png = e.media.snapshots.get(id);
        Ok(json!({ "image": png.map(|b| base64::engine::general_purpose::STANDARD.encode(b)) }))
    });
}

/// Takes the pictures a minimized window will be shown by. Runs once at start.
pub fn wire(engine: &Arc<Engine>) {
    let e = engine.clone();
    engine.running.native_window_changed.subscribe(move |(kind, id)| {
        if e.shutting_down.load(Ordering::SeqCst) || *id == 0 {
            return;
        }
        match kind {
            WindowEventKind::MinimizeStart => {
                // Still on screen for a moment: this is the last chance for a picture of it.
                if e.is_dock_window(*id) {
                    e.media.snapshots.remember(*id, true);
                }
            }
            WindowEventKind::Foreground => {
                let left = e.media.last_foreground.swap(*id, Ordering::SeqCst);
                // The window that has just lost the focus may be minimized next (by Win+D, by another program): keep a fresh picture.
                if left != 0 && left != *id && e.is_dock_window(left) {
                    let e2 = e.clone();
                    std::thread::spawn(move || {
                        e2.media.snapshots.remember(left, false);
                    });
                }
            }
            WindowEventKind::Destroyed => e.media.snapshots.forget(*id),
            _ => {}
        }
    });
}
