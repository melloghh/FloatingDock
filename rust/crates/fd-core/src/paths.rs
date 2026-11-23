//! All user-data locations. Everything lives under `%LOCALAPPDATA%\FloatingDock` and never inside the install folder.

use std::path::{Path, PathBuf};

#[derive(Debug, Clone)]
pub struct AppPaths {
    pub root: PathBuf,
}

impl AppPaths {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self { root: root.into() }
    }

    pub fn default_location() -> Self {
        let local = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).filter(|p| !p.as_os_str().is_empty());
        let base = local.unwrap_or_else(|| std::env::temp_dir().join("FloatingDockData"));
        Self::new(base.join("FloatingDock"))
    }

    pub fn config_file(&self) -> PathBuf { self.root.join("config.json") }
    pub fn search_usage_file(&self) -> PathBuf { self.root.join("search-usage.json") }
    pub fn pinned_file(&self) -> PathBuf { self.root.join("pinned.json") }
    pub fn icon_overrides_file(&self) -> PathBuf { self.root.join("icon-overrides.json") }
    pub fn taskbar_state_file(&self) -> PathBuf { self.root.join("taskbar-state.json") }
    pub fn app_library_dir(&self) -> PathBuf { self.root.join("AppLibrary") }
    pub fn app_library_file(&self) -> PathBuf { self.app_library_dir().join("library.json") }
    pub fn app_library_shortcuts_dir(&self) -> PathBuf { self.app_library_dir().join("Shortcuts") }
    pub fn icon_packs_dir(&self) -> PathBuf { self.root.join("IconPacks") }
    pub fn custom_icons_dir(&self) -> PathBuf { self.icon_packs_dir().join("Custom") }
    pub fn user_icons_dir(&self) -> PathBuf { self.root.join("UserIcons") }
    pub fn cache_dir(&self) -> PathBuf { self.root.join("Cache") }
    pub fn system_icon_cache_dir(&self) -> PathBuf { self.cache_dir().join("SystemIcons") }
    pub fn logs_dir(&self) -> PathBuf { self.root.join("Logs") }

    pub fn ensure_created(&self) -> std::io::Result<()> {
        for d in [
            self.root.clone(),
            self.app_library_dir(),
            self.app_library_shortcuts_dir(),
            self.icon_packs_dir(),
            self.custom_icons_dir(),
            self.user_icons_dir(),
            self.cache_dir(),
            self.system_icon_cache_dir(),
            self.logs_dir(),
        ] {
            std::fs::create_dir_all(d)?;
        }
        Ok(())
    }

    pub fn root(&self) -> &Path {
        &self.root
    }
}
