//! Turns a dropped file (a `.lnk` from the Desktop / Explorer, or an `.exe`) into a persistent Launchpad tile or a pinned app.
//! Nothing is ever started. A shortcut is copied into FloatingDock's own folder, so deleting or moving the original does not
//! destroy the tile. Runs on the shell worker thread.

use std::fs;
use std::path::Path;

use fd_core::{win_path, AppListStore, AppRef, AppSource};

use crate::catalog;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TileImportStatus {
    Added,
    Duplicate,
    Unsupported,
    SaveFailed,
}

impl TileImportStatus {
    pub fn as_str(self) -> &'static str {
        match self {
            TileImportStatus::Added => "Added",
            TileImportStatus::Duplicate => "Duplicate",
            TileImportStatus::Unsupported => "Unsupported",
            TileImportStatus::SaveFailed => "SaveFailed",
        }
    }
}

pub struct TileImportResult {
    pub status: TileImportStatus,
    pub app: Option<AppRef>,
    pub message: Option<String>,
}

fn result(status: TileImportStatus, app: Option<AppRef>, message: impl Into<String>) -> TileImportResult {
    let m = message.into();
    TileImportResult { status, app, message: (!m.is_empty()).then_some(m) }
}

/// A copied shortcut is kept only after the corresponding app list was saved.
pub fn import_and_save(path: &str, shortcuts_dir: &Path, store: &AppListStore) -> TileImportResult {
    let r = import(path, shortcuts_dir, &|key| store.contains(key));
    let (TileImportStatus::Added, Some(app)) = (r.status, r.app.as_ref()) else { return r };
    if store.add(app, None) {
        return r;
    }
    if let Some(copy) = &app.shortcut_path {
        let _ = fs::remove_file(copy);
    }
    if store.contains(&app.key()) {
        result(TileImportStatus::Duplicate, None, "Already added.")
    } else {
        result(TileImportStatus::SaveFailed, None, store.last_error().unwrap_or_else(|| "Could not add application.".into()))
    }
}

pub fn import(path: &str, shortcuts_dir: &Path, already_exists: &dyn Fn(&str) -> bool) -> TileImportResult {
    if !Path::new(path).is_file() {
        return result(TileImportStatus::Unsupported, None, "The file does not exist.");
    }
    let ext = win_path::extension(path).to_lowercase();
    if ext == ".lnk" {
        if let Err(e) = fs::create_dir_all(shortcuts_dir) {
            return result(TileImportStatus::Unsupported, None, e.to_string());
        }
        let mut rnd = [0u8; 16];
        let _ = getrandom::fill(&mut rnd);
        let copy = shortcuts_dir.join(format!("{}.lnk", hex_of(&rnd)));
        if let Err(e) = fs::copy(path, &copy) {
            return result(TileImportStatus::Unsupported, None, e.to_string());
        }
        let copy_text = copy.to_string_lossy().into_owned();
        let Some(mut app) = catalog::app_from_shortcut(&copy_text, AppSource::User) else {
            let _ = fs::remove_file(&copy);
            return result(TileImportStatus::Unsupported, None, "This shortcut does not point to an application.");
        };
        app.name = win_path::file_name_without_extension(path).to_string();
        if already_exists(&app.key()) {
            let _ = fs::remove_file(&copy);
            return result(TileImportStatus::Duplicate, None, format!("'{}' is already in the Launchpad.", app.name));
        }
        return result(TileImportStatus::Added, Some(app), "");
    }
    if ext == ".exe" {
        let app = AppRef {
            name: catalog::exe_display_name(path).unwrap_or_else(|| win_path::file_name_without_extension(path).to_string()),
            exe_path: Some(path.to_string()),
            working_directory: win_path::directory(path).map(str::to_string),
            source: AppSource::User,
            ..Default::default()
        };
        if already_exists(&app.key()) {
            return result(TileImportStatus::Duplicate, None, format!("'{}' is already in the Launchpad.", app.name));
        }
        return result(TileImportStatus::Added, Some(app), "");
    }
    result(TileImportStatus::Unsupported, None, "Only shortcuts (.lnk) and programs (.exe) can be added.")
}

fn hex_of(b: &[u8]) -> String {
    hex::encode(b)
}
