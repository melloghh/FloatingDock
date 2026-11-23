//! User configuration (`config.json`): the same names, defaults and clamping as the C# `DockSettings`.

use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

use crate::json_file::ParseError;
use crate::string_enum;

string_enum! { pub enum DockEdge { Bottom, Left, Right } }
string_enum! { pub enum WidthMode { Auto, Fixed } }
string_enum! { pub enum DockAlignment { Center, Left, Right } }
string_enum! { pub enum MonitorMode { Primary, All } }
string_enum! { pub enum ThemeMode { Dark, Light, System } }
string_enum! { pub enum IndicatorStyle { Dot, Bar } }
string_enum! { pub enum InterfaceLanguage { English, Russian } }
string_enum! { pub enum LaunchpadIconStyle { WhiteSquares, ColorOnDark, ColorOnWhite } }
string_enum! { pub enum TilingModifier { Alt, CtrlAltWin } }
string_enum! {
    /// What to do with the Windows taskbar.
    pub enum TaskbarMode { Leave, AutoHide, HideCompletely }
}

pub const CURRENT_SCHEMA_VERSION: i32 = 2;

/// Every property is wired to real behaviour; values are clamped by [`DockSettings::normalize`].
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct DockSettings {
    pub schema_version: i32,

    // ---- Dock ----
    pub edge: DockEdge,
    pub icon_size: i32,
    pub icon_spacing: i32,
    pub dock_height: i32,
    pub bottom_offset: i32,
    pub width_mode: WidthMode,
    pub fixed_width: i32,
    pub alignment: DockAlignment,
    pub horizontal_offset: i32,
    pub monitor_mode: MonitorMode,

    // ---- Appearance ----
    pub background_color: String,
    pub opacity: i32,
    pub corner_radius: i32,
    pub shadow: bool,
    pub border: bool,
    pub blur: bool,
    pub theme: ThemeMode,
    /// Material 3 seed colour (hex RGB).
    pub accent_seed: String,
    pub language: InterfaceLanguage,
    pub menu_animations: bool,
    pub menu_animation_ms: i32,
    pub show_window_previews: bool,
    pub preview_delay_ms: i32,
    pub indicator_style: IndicatorStyle,
    pub show_labels: bool,

    // ---- Magnification ----
    pub magnification_enabled: bool,
    pub max_scale_percent: i32,
    pub magnification_radius: i32,
    pub responsiveness: i32,
    pub animation_speed: i32,
    pub smoothness: i32,
    pub animation_fps: i32,
    pub icon_animation: bool,
    pub icon_animation_ms: i32,

    // ---- Behaviour ----
    pub show_start_button: bool,
    pub show_launchpad_button: bool,
    pub show_pinned: bool,
    pub show_running: bool,
    pub show_active_indicator: bool,
    pub auto_hide: bool,
    pub reveal_on_edge_hover: bool,
    pub hide_on_fullscreen: bool,
    pub keep_dock_in_fullscreen: bool,
    pub minimize_on_repeat_click: bool,
    pub start_with_windows: bool,
    /// "Flutter" (the engine and the Flutter UI) or "Classic" (the WinUI application). Both read this file.
    pub ui_mode: String,

    // ---- Menu bar ----
    pub top_bar_enabled: bool,
    pub top_bar_auto_hide: bool,
    pub top_bar_reserve_space: bool,
    pub top_bar_show_date: bool,
    pub top_bar_show_seconds: bool,
    pub top_bar_animation_fps: i32,
    pub top_bar_hide_ms: i32,
    pub top_bar_show_ms: i32,
    pub top_bar_hide_delay_ms: i32,
    pub top_bar_media_enabled: bool,
    pub top_bar_tray_enabled: bool,
    pub top_bar_search_enabled: bool,
    /// "Pills", "Island", "Bar" or "Text".
    pub top_bar_style: String,

    // ---- Icon packs ----
    pub use_icon_pack: bool,
    pub active_icon_pack: String,
    pub prefer_dark_variants: bool,

    // ---- Launchpad ----
    pub launchpad_icon_size: i32,
    pub launchpad_spacing: i32,
    pub launchpad_hotkey: String,
    pub launchpad_icon_style: LaunchpadIconStyle,
    pub search_hotkey: String,
    pub search_files: bool,
    pub ui_sounds: bool,
    pub search_folders: String,
    pub search_depth: i32,
    pub search_result_limit: i32,

    // ---- Built-in, opt-in tiling ----
    pub tiling_enabled: bool,
    pub tiling_show_workspaces: bool,
    pub tiling_workspaces: i32,
    pub tiling_gap: i32,
    pub tiling_outer_gap: i32,
    pub tiling_animation_ms: i32,
    pub tiling_animation_fps: i32,
    pub tiling_startup_delay_ms: i32,
    pub tiling_hide_dock: bool,
    pub tiling_bring_activated_windows: bool,
    pub tiling_tile_maximized_apps: bool,
    pub tiling_borders: bool,
    pub tiling_active_border_color: String,
    pub tiling_inactive_border_color: String,
    pub tiling_keybindings: IndexMap<String, String>,
    pub tiling_modifier: TilingModifier,
    pub tiling_excluded_apps: String,

    // ---- Windows taskbar ----
    pub taskbar_mode: TaskbarMode,
    pub reserve_work_area: bool,
    pub restore_taskbar_hotkey: String,
    /// Moves keyboard focus into the dock (arrows, Enter, Esc).
    pub dock_focus_hotkey: String,
}

impl Default for DockSettings {
    fn default() -> Self {
        Self {
            schema_version: CURRENT_SCHEMA_VERSION,
            edge: DockEdge::Bottom,
            icon_size: 48,
            icon_spacing: 12,
            dock_height: 64,
            bottom_offset: 0,
            width_mode: WidthMode::Auto,
            fixed_width: 900,
            alignment: DockAlignment::Center,
            horizontal_offset: 0,
            monitor_mode: MonitorMode::Primary,
            background_color: "1E1E1E".into(),
            opacity: 55,
            corner_radius: 22,
            shadow: true,
            border: true,
            blur: false,
            theme: ThemeMode::Dark,
            accent_seed: "4A6BC8".into(),
            language: InterfaceLanguage::English,
            menu_animations: true,
            menu_animation_ms: 200,
            show_window_previews: true,
            preview_delay_ms: 750,
            indicator_style: IndicatorStyle::Dot,
            show_labels: true,
            magnification_enabled: true,
            max_scale_percent: 170,
            magnification_radius: 150,
            responsiveness: 10,
            animation_speed: 6,
            smoothness: 5,
            animation_fps: 60,
            icon_animation: true,
            icon_animation_ms: 240,
            show_start_button: true,
            show_launchpad_button: true,
            show_pinned: true,
            show_running: true,
            show_active_indicator: true,
            auto_hide: false,
            reveal_on_edge_hover: true,
            hide_on_fullscreen: true,
            keep_dock_in_fullscreen: false,
            minimize_on_repeat_click: true,
            start_with_windows: false,
            ui_mode: "Flutter".into(),
            top_bar_enabled: true,
            top_bar_auto_hide: false,
            top_bar_reserve_space: true,
            top_bar_show_date: false,
            top_bar_show_seconds: false,
            top_bar_animation_fps: 60,
            top_bar_hide_ms: 220,
            top_bar_show_ms: 170,
            top_bar_hide_delay_ms: 700,
            top_bar_media_enabled: true,
            top_bar_tray_enabled: true,
            top_bar_search_enabled: true,
            top_bar_style: "Pills".into(),
            use_icon_pack: true,
            active_icon_pack: "Default".into(),
            prefer_dark_variants: true,
            launchpad_icon_size: 72,
            launchpad_spacing: 28,
            launchpad_hotkey: "Ctrl+Alt+Space".into(),
            launchpad_icon_style: LaunchpadIconStyle::WhiteSquares,
            search_hotkey: "Ctrl+Alt+K".into(),
            search_files: true,
            ui_sounds: false,
            search_folders: "Desktop;Downloads".into(),
            search_depth: 1,
            search_result_limit: 30,
            tiling_enabled: false,
            tiling_show_workspaces: true,
            tiling_workspaces: 5,
            tiling_gap: 8,
            tiling_outer_gap: 12,
            tiling_animation_ms: 220,
            tiling_animation_fps: 60,
            tiling_startup_delay_ms: 180,
            tiling_hide_dock: true,
            tiling_bring_activated_windows: true,
            tiling_tile_maximized_apps: true,
            tiling_borders: false,
            tiling_active_border_color: "A8C7FA".into(),
            tiling_inactive_border_color: "414956".into(),
            tiling_keybindings: IndexMap::new(),
            tiling_modifier: TilingModifier::Alt,
            tiling_excluded_apps: "FloatingDock.exe;Taskmgr.exe;SnippingTool.exe;ScreenClippingHost.exe".into(),
            taskbar_mode: TaskbarMode::HideCompletely,
            reserve_work_area: false,
            restore_taskbar_hotkey: "Ctrl+Alt+Shift+T".into(),
            dock_focus_hotkey: "Ctrl+Alt+D".into(),
        }
    }
}

pub struct ColorUtil;

impl ColorUtil {
    /// `RRGGBB` with an optional `#`.
    pub fn try_parse_rgb(text: &str) -> Option<(u8, u8, u8)> {
        let t = text.trim().trim_start_matches('#');
        if t.len() != 6 || !t.chars().all(|c| c.is_ascii_hexdigit()) {
            return None;
        }
        let v = u32::from_str_radix(t, 16).ok()?;
        Some(((v >> 16) as u8, (v >> 8) as u8, v as u8))
    }
}

impl DockSettings {
    /// Clamps everything into sane ranges (hand-edited JSON can contain anything).
    pub fn normalize(mut self) -> Self {
        self.schema_version = CURRENT_SCHEMA_VERSION;
        self.top_bar_animation_fps = self.top_bar_animation_fps.clamp(30, 144);
        self.top_bar_hide_ms = self.top_bar_hide_ms.clamp(80, 800);
        self.top_bar_show_ms = self.top_bar_show_ms.clamp(80, 800);
        self.top_bar_hide_delay_ms = self.top_bar_hide_delay_ms.clamp(100, 3000);
        self.menu_animation_ms = self.menu_animation_ms.clamp(80, 600);
        self.preview_delay_ms = self.preview_delay_ms.clamp(200, 2000);
        self.search_result_limit = self.search_result_limit.clamp(5, 100);
        self.search_depth = self.search_depth.clamp(0, 3);
        self.tiling_workspaces = self.tiling_workspaces.clamp(1, 9);
        self.tiling_gap = self.tiling_gap.clamp(0, 40);
        self.tiling_outer_gap = self.tiling_outer_gap.clamp(0, 80);
        self.tiling_animation_ms = self.tiling_animation_ms.clamp(0, 600);
        self.tiling_animation_fps = self.tiling_animation_fps.clamp(30, 144);
        self.tiling_startup_delay_ms = self.tiling_startup_delay_ms.clamp(0, 500);
        self.tiling_keybindings = std::mem::take(&mut self.tiling_keybindings)
            .into_iter()
            .filter(|(k, _)| k.encode_utf16().count() <= 64)
            .map(|(k, v)| (k, v.trim().to_string()))
            .collect();
        if ColorUtil::try_parse_rgb(&self.tiling_active_border_color).is_none() {
            self.tiling_active_border_color = "A8C7FA".into();
        }
        if ColorUtil::try_parse_rgb(&self.tiling_inactive_border_color).is_none() {
            self.tiling_inactive_border_color = "414956".into();
        }
        self.search_folders = self
            .search_folders
            .split(';')
            .map(str::trim)
            .filter(|t| !t.is_empty() && !t.eq_ignore_ascii_case("Documents"))
            .collect::<Vec<_>>()
            .join(";");
        self.icon_size = self.icon_size.clamp(24, 128);
        self.icon_spacing = self.icon_spacing.clamp(0, 40);
        self.dock_height = self.dock_height.clamp(self.icon_size + 4, 200);
        self.bottom_offset = self.bottom_offset.clamp(0, 200);
        self.fixed_width = self.fixed_width.clamp(200, 7680);
        self.horizontal_offset = self.horizontal_offset.clamp(-4000, 4000);
        self.opacity = self.opacity.clamp(0, 100);
        self.corner_radius = self.corner_radius.clamp(0, 60);
        if ColorUtil::try_parse_rgb(&self.background_color).is_none() {
            self.background_color = "1E1E1E".into();
        }
        if ColorUtil::try_parse_rgb(&self.accent_seed).is_none() {
            self.accent_seed = "4A6BC8".into();
        }
        self.max_scale_percent = self.max_scale_percent.clamp(100, 300);
        self.magnification_radius = self.magnification_radius.clamp(40, 500);
        self.responsiveness = self.responsiveness.clamp(1, 10);
        self.animation_speed = self.animation_speed.clamp(1, 10);
        self.smoothness = self.smoothness.clamp(0, 10);
        self.animation_fps = self.animation_fps.clamp(30, 144);
        self.icon_animation_ms = self.icon_animation_ms.clamp(80, 800);
        self.launchpad_icon_size = self.launchpad_icon_size.clamp(32, 160);
        self.launchpad_spacing = self.launchpad_spacing.clamp(4, 120);
        if self.active_icon_pack.trim().is_empty() {
            self.active_icon_pack = "Default".into();
        }
        if !matches!(self.ui_mode.as_str(), "Flutter" | "Classic") {
            self.ui_mode = "Flutter".into();
        }
        if !matches!(self.top_bar_style.as_str(), "Pills" | "Island" | "Bar" | "Text") {
            self.top_bar_style = "Pills".into();
        }
        self
    }

    pub fn to_value(&self) -> Value {
        serde_json::to_value(self).expect("settings serialize")
    }

    /// Reads a settings object whose names may be written in any case (hand-edited files); unknown names are ignored.
    pub fn from_value(value: Value) -> Result<Self, serde_json::Error> {
        let Value::Object(map) = value else {
            return Err(serde::de::Error::custom("root is not an object"));
        };
        let canonical = canonical_keys();
        let mut fixed = Map::new();
        for (k, v) in map {
            let name = canonical.iter().find(|c| c.eq_ignore_ascii_case(&k)).cloned().unwrap_or(k);
            // The last spelling wins, like the case-insensitive property binding of the C# engine.
            fixed.insert(name, v);
        }
        serde_json::from_value::<DockSettings>(Value::Object(fixed))
    }

    /// Parses the text of `config.json`: migrates old schemas, refuses newer ones, clamps the values.
    pub fn parse(text: &str) -> Result<Self, ParseError> {
        let value = crate::lenient_json::parse(text).map_err(|e| ParseError::Invalid(e.to_string()))?;
        let Value::Object(mut node) = value else {
            return Err(ParseError::invalid("root is not an object"));
        };
        let version = node
            .get("SchemaVersion")
            .or_else(|| node.get("schemaVersion"))
            .and_then(Value::as_i64)
            .unwrap_or(0) as i32;
        if version > CURRENT_SCHEMA_VERSION {
            return Err(ParseError::Unsupported("The settings were written by a newer version.".into()));
        }
        if version < CURRENT_SCHEMA_VERSION {
            migrate(&mut node, version);
        }
        Ok(Self::from_value(Value::Object(node))?.normalize())
    }
}

fn canonical_keys() -> Vec<String> {
    match DockSettings::default().to_value() {
        Value::Object(m) => m.keys().cloned().collect(),
        _ => Vec::new(),
    }
}

/// Step-wise schema migration; every step only touches the keys it knows about.
fn migrate(node: &mut Map<String, Value>, from_version: i32) {
    if from_version < 1 {
        // v0 (pre-release) stored the Launchpad button flag as ShowLaunchpad and the opacity as 0..1.
        if node.contains_key("ShowLaunchpad") && !node.contains_key("ShowLaunchpadButton") {
            if let Some(v) = node.shift_remove("ShowLaunchpad") {
                node.insert("ShowLaunchpadButton".into(), v);
            }
        }
        if let Some(d) = node.get("Opacity").and_then(Value::as_f64) {
            if d > 0.0 && d <= 1.0 {
                node.insert("Opacity".into(), Value::from((d * 100.0).round() as i64));
            }
        }
    }
    if from_version < 2 {
        // v2 replaced the boolean HideTaskbar with the TaskbarMode enum.
        if let Some(Value::Bool(hide)) = node.get("HideTaskbar").cloned() {
            node.insert("TaskbarMode".into(), Value::from(if hide { "AutoHide" } else { "Leave" }));
            node.shift_remove("HideTaskbar");
        }
    }
    node.insert("SchemaVersion".into(), Value::from(CURRENT_SCHEMA_VERSION));
}

/// A patch named a setting that does not exist, is read-only, or carried a value of the wrong type.
#[derive(Debug, Clone)]
pub struct SettingsPatchError {
    pub key: String,
    pub message: String,
}

impl std::fmt::Display for SettingsPatchError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        if self.key.is_empty() {
            write!(f, "{}", self.message)
        } else {
            write!(f, "{}: {}", self.key, self.message)
        }
    }
}

impl DockSettings {
    /// Applies a partial update (`{"IconSize": 40}`) and returns a normalized copy; `self` is not modified. Keys are compared
    /// case-insensitively. Unknown, read-only or wrongly typed values fail the whole patch.
    pub fn apply_patch(&self, patch: &Map<String, Value>) -> Result<DockSettings, SettingsPatchError> {
        let defaults = DockSettings::default().to_value();
        let Value::Object(mut node) = self.to_value() else { unreachable!() };
        let canonical = canonical_keys();
        for (key, value) in patch {
            let Some(name) = canonical.iter().find(|c| c.eq_ignore_ascii_case(key)) else {
                return Err(SettingsPatchError { key: key.clone(), message: "Unknown setting.".into() });
            };
            if name == "SchemaVersion" {
                return Err(SettingsPatchError { key: key.clone(), message: "This setting is read-only.".into() });
            }
            // A JSON null stands for an empty string / empty map (what the C# properties made of it).
            let value = match (value, defaults.get(name)) {
                (Value::Null, Some(Value::String(_))) => Value::String(String::new()),
                (Value::Null, Some(Value::Object(_))) => Value::Object(Map::new()),
                (v, _) => v.clone(),
            };
            let mut probe = Map::new();
            probe.insert(name.clone(), value.clone());
            if let Err(e) = serde_json::from_value::<DockSettings>(Value::Object(probe)) {
                return Err(SettingsPatchError { key: key.clone(), message: format!("Wrong value type: {e}") });
            }
            node.insert(name.clone(), value);
        }
        serde_json::from_value::<DockSettings>(Value::Object(node))
            .map(DockSettings::normalize)
            .map_err(|e| SettingsPatchError { key: String::new(), message: e.to_string() })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn defaults_roundtrip_with_csharp_names() {
        let v = DockSettings::default().to_value();
        assert_eq!(v["IconSize"], 48);
        assert_eq!(v["Edge"], "Bottom");
        assert_eq!(v["TaskbarMode"], "HideCompletely");
        assert_eq!(v["TilingKeybindings"], json!({}));
        let back = DockSettings::from_value(v).unwrap();
        assert_eq!(back, DockSettings::default());
    }

    #[test]
    fn names_and_enums_are_case_insensitive() {
        let s = DockSettings::parse(r#"{"SchemaVersion":2,"iconsize":60,"edge":"left","Theme":1}"#).unwrap();
        assert_eq!(s.icon_size, 60);
        assert_eq!(s.edge, DockEdge::Left);
        assert_eq!(s.theme, ThemeMode::Light);
    }

    #[test]
    fn normalize_clamps() {
        let s = DockSettings { icon_size: 500, dock_height: 1, opacity: -5, ui_mode: "x".into(), ..Default::default() }.normalize();
        assert_eq!(s.icon_size, 128);
        assert_eq!(s.dock_height, 132);
        assert_eq!(s.opacity, 0);
        assert_eq!(s.ui_mode, "Flutter");
    }

    #[test]
    fn patch_applies_validates_and_is_atomic() {
        let base = DockSettings::default();
        let ok = base.apply_patch(json!({"iconsize": 40, "Edge": "Right"}).as_object().unwrap()).unwrap();
        assert_eq!((ok.icon_size, ok.edge), (40, DockEdge::Right));
        let e = base.apply_patch(json!({"IconSize": "big"}).as_object().unwrap()).unwrap_err();
        assert_eq!(e.key, "IconSize");
        let e = base.apply_patch(json!({"Nope": 1}).as_object().unwrap()).unwrap_err();
        assert_eq!(e.message, "Unknown setting.");
        let e = base.apply_patch(json!({"SchemaVersion": 9}).as_object().unwrap()).unwrap_err();
        assert!(e.message.contains("read-only"));
        assert_eq!(base, DockSettings::default());
        let n = base.apply_patch(json!({"SearchHotkey": null}).as_object().unwrap()).unwrap();
        assert_eq!(n.search_hotkey, "");
    }

    #[test]
    fn migrates_old_schema() {
        let s = DockSettings::parse(r#"{"HideTaskbar": true, "ShowLaunchpad": false, "Opacity": 0.5}"#).unwrap();
        assert_eq!(s.taskbar_mode, TaskbarMode::AutoHide);
        assert!(!s.show_launchpad_button);
        assert_eq!(s.opacity, 50);
    }

    #[test]
    fn newer_schema_is_refused() {
        assert!(matches!(DockSettings::parse(r#"{"SchemaVersion": 99}"#), Err(ParseError::Unsupported(_))));
    }
}
