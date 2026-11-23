//! The safety logic around the original Windows taskbar, independent of Win32 so it can be unit-tested.
//!
//! * The taskbar is never changed unless its original state was captured AND the recovery record was saved and read back.
//! * Restore performs the inverse operations, verifies them, and deletes the record only after the verification succeeded.
//! * Restore only touches what FloatingDock changed: an auto-hide taskbar stays auto-hide, nothing is done when nothing was recorded.

use std::path::{Path, PathBuf};
use std::sync::Mutex;

use base64::Engine as _;
use serde::{Deserialize, Deserializer, Serialize, Serializer};

use crate::json_file::{self, LoadStatus, ParseError};
use crate::settings::TaskbarMode;
use crate::string_enum;

pub const MAIN_CLASS: &str = "Shell_TrayWnd";
pub const SECONDARY_CLASS: &str = "Shell_SecondaryTrayWnd";
pub const AUTO_HIDE: i32 = 1;
pub const ALWAYS_ON_TOP: i32 = 2;
pub const SCHEMA_VERSION: i32 = 2;
pub const MAX_WINDOWS: usize = 64;
pub const MAX_REGION_BYTES: usize = 65536;

string_enum! {
    pub enum TaskbarPhase {
        // Pending: intent recorded; the change may or may not have been (fully) applied. Restoring is idempotent.
        // Applied: all requested changes were applied and verified.
        // Restored: everything was restored but the file could not be deleted; it must be ignored.
        Pending,
        Applied,
        Restored
    }
}

mod base64_opt {
    use super::*;

    pub fn serialize<S: Serializer>(v: &Option<Vec<u8>>, s: S) -> Result<S::Ok, S::Error> {
        match v {
            Some(b) => s.serialize_str(&base64::engine::general_purpose::STANDARD.encode(b)),
            None => s.serialize_none(),
        }
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Option<Vec<u8>>, D::Error> {
        let v: Option<String> = Option::deserialize(d)?;
        v.map(|t| base64::engine::general_purpose::STANDARD.decode(t).map_err(serde::de::Error::custom)).transpose()
    }
}

/// One taskbar window as it was before FloatingDock touched it.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct TaskbarWindowRecord {
    pub handle: i64,
    pub class_name: String,
    /// Monitor device name the window was on; used to find the window again after Explorer restarted.
    pub monitor_id: String,
    pub original_visible: bool,
    pub hidden_by_us: bool,
    pub region_captured: bool,
    #[serde(with = "base64_opt", skip_serializing_if = "Option::is_none")]
    pub original_region: Option<Vec<u8>>,
    pub region_changed_by_us: bool,
}

impl Default for TaskbarWindowRecord {
    fn default() -> Self {
        Self {
            handle: 0,
            class_name: String::new(),
            monitor_id: String::new(),
            original_visible: false,
            hidden_by_us: false,
            region_captured: false,
            original_region: None,
            region_changed_by_us: false,
        }
    }
}

/// The recovery record `taskbar-state.json`: what the taskbar looked like BEFORE FloatingDock changed it. It is written (and
/// verified) before the first change.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct TaskbarStateRecord {
    pub schema_version: i32,
    pub phase: TaskbarPhase,
    pub applied_mode: TaskbarMode,
    pub owner_pid: i32,
    pub created_utc: String,
    /// ABM_GETSTATE flags before the change (ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2).
    pub original_app_bar_flags: i32,
    /// True when FloatingDock switched auto-hide on (so it has to switch it off again).
    pub auto_hide_changed_by_us: bool,
    pub windows: Vec<TaskbarWindowRecord>,
}

impl Default for TaskbarStateRecord {
    fn default() -> Self {
        Self {
            schema_version: SCHEMA_VERSION,
            phase: TaskbarPhase::Pending,
            applied_mode: TaskbarMode::Leave,
            owner_pid: 0,
            created_utc: crate::search::iso_now(),
            original_app_bar_flags: 0,
            auto_hide_changed_by_us: false,
            windows: Vec::new(),
        }
    }
}

impl TaskbarStateRecord {
    pub fn original_auto_hide(&self) -> bool {
        self.original_app_bar_flags & AUTO_HIDE != 0
    }

    /// An error text when the record cannot be trusted, otherwise `None`.
    pub fn validate(&self) -> Option<String> {
        if self.schema_version < 1 {
            return Some("missing schema version".into());
        }
        if self.schema_version > SCHEMA_VERSION {
            return Some(format!("written by a newer version (schema {})", self.schema_version));
        }
        if self.windows.len() > MAX_WINDOWS {
            return Some("too many windows".into());
        }
        if !(0..=0xF).contains(&self.original_app_bar_flags) {
            return Some("invalid flags".into());
        }
        if self.windows.iter().any(|w| w.handle == 0 || (w.class_name != MAIN_CLASS && w.class_name != SECONDARY_CLASS)) {
            return Some("invalid taskbar window identity".into());
        }
        let mut handles: Vec<i64> = self.windows.iter().map(|w| w.handle).collect();
        handles.sort_unstable();
        handles.dedup();
        if handles.len() != self.windows.len() {
            return Some("duplicate taskbar handles".into());
        }
        if self.windows.iter().any(|w| w.original_region.as_ref().is_some_and(|b| b.len() < 32 || b.len() > MAX_REGION_BYTES)) {
            return Some("invalid taskbar region data".into());
        }
        if self.windows.iter().any(|w| w.region_changed_by_us && !w.region_captured) {
            return Some("missing original taskbar region".into());
        }
        None
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TaskbarStateStatus {
    Missing,
    Ok,
    Corrupt,
}

pub struct TaskbarStateLoad {
    pub status: TaskbarStateStatus,
    pub record: Option<TaskbarStateRecord>,
    pub detail: Option<String>,
}

/// Reads / writes the recovery record: atomic, verified, schema-checked. A damaged file is kept as `*.corrupt`.
pub struct TaskbarStateStore {
    path: PathBuf,
}

impl TaskbarStateStore {
    pub fn new(path: &Path) -> Self {
        Self { path: path.to_path_buf() }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn load(&self) -> TaskbarStateLoad {
        let r = json_file::load(&self.path, |text| {
            let v = crate::lenient_json::parse(text).map_err(|e| ParseError::Invalid(e.to_string()))?;
            let rec: TaskbarStateRecord = serde_json::from_value(v).map_err(|e| ParseError::Invalid(e.to_string()))?;
            if rec.schema_version > SCHEMA_VERSION {
                return Err(ParseError::Unsupported("The recovery record was written by a newer version.".into()));
            }
            match rec.validate() {
                Some(problem) => Err(ParseError::Invalid(problem)),
                None => Ok(rec),
            }
        });
        match r.status {
            LoadStatus::Missing => TaskbarStateLoad { status: TaskbarStateStatus::Missing, record: None, detail: None },
            LoadStatus::Corrupt => TaskbarStateLoad { status: TaskbarStateStatus::Corrupt, record: None, detail: r.detail },
            _ => TaskbarStateLoad { status: TaskbarStateStatus::Ok, record: r.value, detail: r.detail },
        }
    }

    /// True only when the record is on disk and was read back successfully.
    pub fn try_save(&self, record: &TaskbarStateRecord) -> Result<(), String> {
        if let Some(problem) = record.validate() {
            return Err(format!("Invalid recovery record: {problem}"));
        }
        json_file::save(&self.path, record).map_err(|e| e.to_string())
    }

    /// Deletes the record (and its `*.bak`). True when no record remains.
    pub fn try_delete(&self) -> bool {
        let bak = json_file::backup_path(&self.path);
        let _ = std::fs::remove_file(&bak);
        let _ = std::fs::remove_file(&self.path);
        !self.path.exists() && !bak.exists()
    }
}

/// A taskbar window as reported by the operating system.
#[derive(Debug, Clone, PartialEq)]
pub struct TaskbarWindow {
    pub handle: i64,
    pub class_name: String,
    pub monitor_id: String,
    pub visible: bool,
}

/// The few operations on the Windows taskbar FloatingDock needs. Every method reports success; implementations must verify
/// the effect.
pub trait TaskbarSystem: Send + Sync {
    /// ABM_GETSTATE flags. `None` when there is no taskbar (Explorer is not running).
    fn try_get_app_bar_state(&self) -> Option<i32>;
    /// ABM_SETSTATE; true only when the new state was read back.
    fn try_set_app_bar_state(&self, flags: i32) -> bool;
    fn taskbar_windows(&self) -> Vec<TaskbarWindow>;
    fn is_window(&self, handle: i64) -> bool;
    /// Shows / hides the window; true only when the new visibility was read back.
    fn try_set_visible(&self, handle: i64, visible: bool) -> bool;
    /// `Some(None)` = no window region; `None` = could not be read.
    fn try_get_region(&self, handle: i64) -> Option<Option<Vec<u8>>>;
    fn try_set_region(&self, handle: i64, data: Option<&[u8]>) -> bool;
    fn try_clip_empty(&self, handle: i64) -> bool;
    fn is_clipped_empty(&self, handle: i64) -> bool;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TaskbarOutcome {
    /// Done and verified.
    Ok,
    /// Nothing had to be done.
    NothingToDo,
    /// The requested mode could not be applied completely; a safer mode is active (see message).
    Degraded,
    /// Nothing was changed (or the change was rolled back) because it could not be done safely.
    Refused,
    /// Restoring did not finish; the recovery record was kept so it can be tried again.
    RetryNeeded,
    /// The recovery record is missing / unreadable but the taskbar looks modified: the user has to confirm a forced restore.
    NeedsConfirmation,
}

#[derive(Debug, Clone, PartialEq)]
pub struct TaskbarResult {
    pub outcome: TaskbarOutcome,
    pub message: String,
}

impl TaskbarResult {
    fn new(outcome: TaskbarOutcome, message: impl Into<String>) -> Self {
        Self { outcome, message: message.into() }
    }

    pub fn success(&self) -> bool {
        matches!(self.outcome, TaskbarOutcome::Ok | TaskbarOutcome::NothingToDo | TaskbarOutcome::Degraded)
    }

    /// True when the user should be told about the result (anything but a plain success).
    pub fn needs_attention(&self) -> bool {
        !matches!(self.outcome, TaskbarOutcome::Ok | TaskbarOutcome::NothingToDo)
    }
}

pub struct TaskbarCoordinator {
    sys: Box<dyn TaskbarSystem>,
    store: TaskbarStateStore,
    log: Box<dyn Fn(&str) + Send + Sync>,
    pid: Box<dyn Fn() -> i32 + Send + Sync>,
    gate: Mutex<()>,
}

fn find_current<'a>(rec: &TaskbarWindowRecord, current: &'a [TaskbarWindow], used: &std::collections::HashSet<i64>) -> Option<&'a TaskbarWindow> {
    if let Some(w) = current.iter().find(|w| w.handle == rec.handle && w.class_name == rec.class_name && !used.contains(&w.handle)) {
        return Some(w);
    }
    if let Some(w) = current
        .iter()
        .find(|w| !used.contains(&w.handle) && w.class_name == rec.class_name && !rec.monitor_id.is_empty() && w.monitor_id == rec.monitor_id)
    {
        return Some(w);
    }
    if rec.class_name == MAIN_CLASS {
        return current.iter().find(|w| !used.contains(&w.handle) && w.class_name == rec.class_name);
    }
    None
}

fn same_region(a: &Option<Vec<u8>>, b: &Option<Vec<u8>>) -> bool {
    a == b
}

impl TaskbarCoordinator {
    pub fn new(sys: Box<dyn TaskbarSystem>, store: TaskbarStateStore, log: impl Fn(&str) + Send + Sync + 'static) -> Self {
        Self::with_pid(sys, store, log, || std::process::id() as i32)
    }

    pub fn with_pid(
        sys: Box<dyn TaskbarSystem>,
        store: TaskbarStateStore,
        log: impl Fn(&str) + Send + Sync + 'static,
        pid: impl Fn() -> i32 + Send + Sync + 'static,
    ) -> Self {
        Self { sys, store, log: Box::new(log), pid: Box::new(pid), gate: Mutex::new(()) }
    }

    fn log(&self, m: &str) {
        (self.log)(m);
    }

    /// True when a record of a possibly modified taskbar exists on disk (or exists but is damaged).
    pub fn has_pending_restore(&self) -> bool {
        let _g = self.gate.lock().unwrap();
        let l = self.store.load();
        l.status == TaskbarStateStatus::Corrupt
            || (l.status == TaskbarStateStatus::Ok && l.record.as_ref().is_some_and(|r| r.phase != TaskbarPhase::Restored))
    }

    // ------------------------------------------------------------------ apply

    /// Applies `mode`. Calling it again (settings change, Explorer restart, monitor hot-plug) is safe and re-uses the originally
    /// captured state.
    pub fn apply(&self, mode: TaskbarMode) -> TaskbarResult {
        let _g = self.gate.lock().unwrap();
        self.apply_locked(mode)
    }

    fn apply_locked(&self, mode: TaskbarMode) -> TaskbarResult {
        use TaskbarOutcome::*;
        if mode == TaskbarMode::Leave {
            return self.restore_locked("mode set to Leave");
        }
        let load = self.store.load();
        let mut record: Option<TaskbarStateRecord> = load.record.clone().filter(|r| r.phase != TaskbarPhase::Restored);

        let Some(flags) = self.sys.try_get_app_bar_state() else {
            return TaskbarResult::new(Refused, "The Windows taskbar was not found (is Explorer running?). Nothing was changed.");
        };
        let mut current = self.sys.taskbar_windows();
        if current.is_empty() {
            return TaskbarResult::new(Refused, "No taskbar window was found. Nothing was changed.");
        }
        if load.status == TaskbarStateStatus::Corrupt {
            return TaskbarResult::new(NeedsConfirmation, "The taskbar recovery file cannot be read. Nothing was changed; restore the taskbar first.");
        }

        let fresh = record.is_none();
        let mut record = match record.take() {
            None => TaskbarStateRecord {
                phase: TaskbarPhase::Pending,
                original_app_bar_flags: flags,
                auto_hide_changed_by_us: flags & AUTO_HIDE == 0,
                windows: current
                    .iter()
                    .map(|w| TaskbarWindowRecord {
                        handle: w.handle,
                        class_name: w.class_name.clone(),
                        monitor_id: w.monitor_id.clone(),
                        original_visible: w.visible,
                        ..Default::default()
                    })
                    .collect(),
                ..Default::default()
            },
            Some(mut r) => {
                reconcile(&mut r, &current);
                r
            }
        };
        record.schema_version = SCHEMA_VERSION;
        if mode == TaskbarMode::HideCompletely {
            for rec in record.windows.iter_mut() {
                if !current.iter().any(|w| w.handle == rec.handle) {
                    continue;
                }
                if !rec.region_captured {
                    let Some(region) = self.sys.try_get_region(rec.handle) else {
                        return TaskbarResult::new(Refused, "The original taskbar window region could not be captured. Nothing was changed.");
                    };
                    rec.original_region = region;
                    rec.region_captured = true;
                }
                // Persist the intent before changing the region, including crash recovery.
                rec.region_changed_by_us = true;
            }
        }
        record.owner_pid = (self.pid)();
        record.applied_mode = mode;
        record.phase = TaskbarPhase::Pending;

        // 1. the record must be safely on disk BEFORE the first change
        if let Err(save_error) = self.store.try_save(&record) {
            self.log(&format!("taskbar: cannot save the recovery record: {save_error}"));
            return TaskbarResult::new(
                Refused,
                format!("FloatingDock could not save the taskbar recovery file, so the Windows taskbar was NOT changed. ({save_error})"),
            );
        }

        // 2. auto-hide
        if flags & AUTO_HIDE == 0 && !self.sys.try_set_app_bar_state(flags | AUTO_HIDE) {
            self.log("taskbar: could not switch auto-hide on");
            let undo = self.restore_locked("auto-hide could not be applied");
            return TaskbarResult::new(
                Refused,
                format!("The taskbar auto-hide setting could not be changed; the taskbar was left as it was.{}", if undo.success() { String::new() } else { format!(" {}", undo.message) }),
            );
        }

        // 3. visibility of the taskbar windows
        let mut failures: Vec<String> = Vec::new();
        current = self.sys.taskbar_windows();
        for rec in record.windows.iter_mut() {
            let Some(w) = find_current(rec, &current, &Default::default()).cloned() else { continue };
            rec.handle = w.handle;
            if mode == TaskbarMode::HideCompletely {
                if !self.sys.try_clip_empty(w.handle) {
                    failures.push(format!("{} region on {}", w.class_name, w.monitor_id));
                    continue;
                }
                if !rec.original_visible || !w.visible {
                    if rec.original_visible {
                        rec.hidden_by_us = true;
                    }
                    continue;
                }
                rec.hidden_by_us = true; // set first: if the hide succeeds half-way a restore still knows
                // Explorer can immediately show the HWND again; its empty region still prevents drawing/hit testing.
                self.sys.try_set_visible(w.handle, false);
            } else {
                if rec.region_changed_by_us {
                    if self.sys.try_set_region(w.handle, rec.original_region.as_deref()) {
                        rec.region_changed_by_us = false;
                    } else {
                        failures.push(format!("{} region on {}", w.class_name, w.monitor_id));
                    }
                }
                if rec.hidden_by_us || (rec.original_visible && !w.visible) {
                    if self.sys.try_set_visible(w.handle, true) {
                        rec.hidden_by_us = false;
                    } else {
                        failures.push(format!("{} on {}", w.class_name, w.monitor_id));
                    }
                }
            }
        }

        let mut effective = mode;
        let mut degraded: Option<String> = None;
        if !failures.is_empty() {
            self.log(&format!("taskbar: could not change window visibility: {}", failures.join(", ")));
            if mode == TaskbarMode::HideCompletely {
                // safe fallback: put the windows back, keep the (already verified) auto-hide
                let back = self.show_hidden_windows(&mut record);
                effective = TaskbarMode::AutoHide;
                degraded = Some(if back {
                    "The taskbar could not be hidden completely; it is set to auto-hide instead.".into()
                } else {
                    "The taskbar could not be hidden completely and some taskbar windows could not be shown again. Use 'Restore Windows taskbar'.".into()
                });
            } else {
                degraded = Some("Some taskbar windows could not be shown again. Use 'Restore Windows taskbar'.".into());
            }
        }
        record.applied_mode = effective;
        record.phase = TaskbarPhase::Applied;
        if let Err(e) = self.store.try_save(&record) {
            self.log(&format!("taskbar: the final recovery record could not be saved (the intent record is on disk): {e}"));
        }
        self.log(&format!("taskbar: applied {} (requested {}, {})", effective.as_str(), mode.as_str(), if fresh { "new" } else { "re-applied" }));
        match degraded {
            None => TaskbarResult::new(Ok, format!("Taskbar mode {} applied.", effective.as_str())),
            Some(m) => TaskbarResult::new(Degraded, m),
        }
    }

    /// Cheap steady-state check; only saves/re-applies if Explorer undid a change.
    pub fn maintain(&self, mode: TaskbarMode) -> TaskbarResult {
        use TaskbarOutcome::*;
        let _g = self.gate.lock().unwrap();
        if mode == TaskbarMode::Leave {
            return TaskbarResult::new(NothingToDo, "No taskbar maintenance requested.");
        }
        let windows = self.sys.taskbar_windows();
        let Some(flags) = self.sys.try_get_app_bar_state() else {
            return TaskbarResult::new(NothingToDo, "Explorer is not running.");
        };
        let needs_apply = flags & AUTO_HIDE == 0 || (mode == TaskbarMode::HideCompletely && windows.iter().any(|w| !self.sys.is_clipped_empty(w.handle)));
        if !needs_apply {
            return TaskbarResult::new(NothingToDo, "Taskbar mode is still active.");
        }
        let saved = self.store.load();
        match saved.record {
            Some(r) if r.phase == TaskbarPhase::Applied && r.owner_pid == (self.pid)() => self.apply_locked(mode),
            _ => TaskbarResult::new(Refused, "Taskbar maintenance needs this instance's recovery record."),
        }
    }

    fn show_hidden_windows(&self, record: &mut TaskbarStateRecord) -> bool {
        let mut all_ok = true;
        let current = self.sys.taskbar_windows();
        let mut used = std::collections::HashSet::new();
        for rec in record.windows.iter_mut() {
            let Some(w) = find_current(rec, &current, &used).cloned() else { continue };
            used.insert(w.handle);
            if rec.region_changed_by_us {
                if self.sys.try_set_region(w.handle, rec.original_region.as_deref()) {
                    rec.region_changed_by_us = false;
                } else {
                    all_ok = false;
                }
            }
            if !rec.original_visible || w.visible {
                rec.hidden_by_us = false;
                continue;
            }
            if self.sys.try_set_visible(w.handle, true) {
                rec.hidden_by_us = false;
            } else {
                all_ok = false;
            }
        }
        all_ok
    }

    // ------------------------------------------------------------------ restore

    /// Restores what FloatingDock changed (normal exit, mode Leave, crash recovery, tray command). Does nothing when nothing was
    /// recorded.
    pub fn restore(&self) -> TaskbarResult {
        let _g = self.gate.lock().unwrap();
        self.restore_locked("restore requested")
    }

    fn restore_locked(&self, reason: &str) -> TaskbarResult {
        use TaskbarOutcome::*;
        let load = self.store.load();
        if load.status == TaskbarStateStatus::Missing {
            return TaskbarResult::new(NothingToDo, "The taskbar was not changed by FloatingDock.");
        }
        if load.status == TaskbarStateStatus::Corrupt {
            return TaskbarResult::new(NeedsConfirmation, "The taskbar recovery file is damaged, so FloatingDock cannot know the original taskbar state.");
        }
        let mut rec = load.record.expect("record present when status is Ok");
        if rec.phase == TaskbarPhase::Restored || rec.applied_mode == TaskbarMode::Leave {
            self.store.try_delete();
            return TaskbarResult::new(NothingToDo, "The taskbar was not changed by FloatingDock.");
        }

        let Some(flags) = self.sys.try_get_app_bar_state() else {
            return TaskbarResult::new(
                RetryNeeded,
                "The Windows taskbar was not found (is Explorer running?). The recovery record was kept; try again when it is back.",
            );
        };

        let mut problems: Vec<String> = Vec::new();
        let mut current = self.sys.taskbar_windows();
        let mut used = std::collections::HashSet::new();

        // windows first: a visible taskbar is what the user needs most
        for w in &rec.windows {
            let Some(cur) = find_current(w, &current, &used) else { continue }; // gone (Explorer restarted / monitor removed)
            used.insert(cur.handle);
            if w.region_changed_by_us && cur.handle == w.handle && !self.sys.try_set_region(cur.handle, w.original_region.as_deref()) {
                problems.push(format!("{} original region could not be restored", cur.class_name));
            }
            if w.original_visible && !cur.visible && !self.sys.try_set_visible(cur.handle, true) {
                problems.push(format!("{} on {} could not be shown", cur.class_name, cur.monitor_id));
            }
        }
        // auto-hide: only when FloatingDock switched it on
        if rec.auto_hide_changed_by_us && ((flags & AUTO_HIDE) != 0) != rec.original_auto_hide() {
            let target = if rec.original_auto_hide() { flags | AUTO_HIDE } else { flags & !AUTO_HIDE };
            if !self.sys.try_set_app_bar_state(target) {
                problems.push("the taskbar auto-hide setting could not be restored".into());
            }
        }

        // verify the result with fresh reads
        match self.sys.try_get_app_bar_state() {
            None => problems.push("the taskbar state could not be read back".into()),
            Some(after) if rec.auto_hide_changed_by_us && ((after & AUTO_HIDE) != 0) != rec.original_auto_hide() => {
                problems.push("auto-hide is still different from the original".into())
            }
            _ => {}
        }
        current = self.sys.taskbar_windows();
        used.clear();
        for w in &rec.windows {
            let Some(cur) = find_current(w, &current, &used) else { continue };
            used.insert(cur.handle);
            if w.original_visible && !cur.visible {
                problems.push(format!("{} on {} is still hidden", cur.class_name, cur.monitor_id));
            }
            if w.region_changed_by_us && cur.handle == w.handle {
                match self.sys.try_get_region(cur.handle) {
                    Some(r) if same_region(&w.original_region, &r) => {}
                    _ => problems.push(format!("{} original region is still different", cur.class_name)),
                }
            }
        }

        if !problems.is_empty() {
            self.log(&format!("taskbar: restore incomplete: {}", problems.join("; ")));
            problems.dedup();
            return TaskbarResult::new(
                RetryNeeded,
                format!("The Windows taskbar could not be fully restored ({}). The recovery record was kept so it can be tried again.", problems.join("; ")),
            );
        }

        if !self.store.try_delete() {
            rec.phase = TaskbarPhase::Restored;
            if self.store.try_save(&rec).is_err() {
                self.log("taskbar: restored, but the recovery record could not be deleted or marked");
            }
        }
        self.log(&format!("taskbar: restored ({reason})"));
        TaskbarResult::new(Ok, "The Windows taskbar was restored.")
    }

    // ------------------------------------------------------------------ crash recovery / forced restore

    /// Call once at start, BEFORE the configured mode is applied: undoes what a crashed / killed previous run left behind.
    pub fn recover_from_previous_run(&self) -> TaskbarResult {
        let _g = self.gate.lock().unwrap();
        let load = self.store.load();
        match load.status {
            TaskbarStateStatus::Missing => TaskbarResult::new(TaskbarOutcome::NothingToDo, "No previous taskbar changes."),
            TaskbarStateStatus::Corrupt => TaskbarResult::new(
                TaskbarOutcome::NeedsConfirmation,
                "The taskbar recovery file cannot be read; the original state is unknown. No settings were changed.",
            ),
            TaskbarStateStatus::Ok => {
                self.log("taskbar: restoring changes left over from a previous run");
                self.restore_locked("previous run did not exit cleanly")
            }
        }
    }

    /// True when any taskbar window is hidden right now (decides whether a forced restore is worth offering).
    pub fn any_taskbar_window_hidden(&self) -> bool {
        let _g = self.gate.lock().unwrap();
        self.sys.taskbar_windows().iter().any(|w| !w.visible || self.sys.is_clipped_empty(w.handle))
    }

    /// Explicit, user-confirmed restore for the case where the original state is unknown (record missing or damaged): shows every
    /// taskbar window and, when `disable_auto_hide`, switches auto-hide off (the Windows default). Never called without consent.
    pub fn force_restore(&self, disable_auto_hide: bool) -> TaskbarResult {
        let _g = self.gate.lock().unwrap();
        let Some(flags) = self.sys.try_get_app_bar_state() else {
            return TaskbarResult::new(TaskbarOutcome::RetryNeeded, "The Windows taskbar was not found (is Explorer running?).");
        };
        let mut problems: Vec<String> = Vec::new();
        for w in self.sys.taskbar_windows() {
            if self.sys.is_clipped_empty(w.handle) && !self.sys.try_set_region(w.handle, None) {
                problems.push(format!("{} region could not be cleared", w.class_name));
            }
            if !w.visible && !self.sys.try_set_visible(w.handle, true) {
                problems.push(format!("{} on {} could not be shown", w.class_name, w.monitor_id));
            }
        }
        if disable_auto_hide && flags & AUTO_HIDE != 0 && !self.sys.try_set_app_bar_state(flags & !AUTO_HIDE) {
            problems.push("auto-hide could not be switched off".into());
        }
        if self.sys.taskbar_windows().iter().any(|w| !w.visible || self.sys.is_clipped_empty(w.handle)) {
            problems.push("a taskbar window is still hidden".into());
        }
        if !problems.is_empty() {
            problems.dedup();
            return TaskbarResult::new(TaskbarOutcome::RetryNeeded, format!("The taskbar could not be restored ({}).", problems.join("; ")));
        }
        if !self.store.try_delete() {
            self.log("taskbar: forced restore done but the recovery file could not be deleted");
        }
        self.log("taskbar: forced restore done");
        TaskbarResult::new(TaskbarOutcome::Ok, "The Windows taskbar was restored.")
    }
}

/// Brings the recorded windows in line with what exists now (Explorer restart gives new handles; a new monitor adds a taskbar).
fn reconcile(record: &mut TaskbarStateRecord, current: &[TaskbarWindow]) {
    let mut used = std::collections::HashSet::new();
    for rec in record.windows.iter_mut() {
        if let Some(w) = find_current(rec, current, &used) {
            if rec.handle != w.handle {
                rec.region_captured = false;
                rec.region_changed_by_us = false;
                rec.original_region = None;
            }
            rec.handle = w.handle;
            used.insert(w.handle);
        }
    }
    for w in current {
        if !used.contains(&w.handle) && record.windows.len() < MAX_WINDOWS {
            record.windows.push(TaskbarWindowRecord {
                handle: w.handle,
                class_name: w.class_name.clone(),
                monitor_id: w.monitor_id.clone(),
                original_visible: w.visible,
                ..Default::default()
            });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use std::sync::{Arc, Mutex};

    #[derive(Default)]
    struct World {
        flags: i32,
        windows: Vec<(TaskbarWindow, Option<Vec<u8>>)>, // (window, region)
        fail_set_visible: bool,
        explorer_running: bool,
    }

    #[derive(Clone)]
    struct Fake(Arc<Mutex<World>>);

    fn region(n: u8) -> Vec<u8> {
        vec![n; 40]
    }

    impl Fake {
        fn new() -> Self {
            let world = World {
                flags: 0,
                windows: vec![
                    (TaskbarWindow { handle: 100, class_name: MAIN_CLASS.into(), monitor_id: "D1".into(), visible: true }, Some(region(7))),
                    (TaskbarWindow { handle: 200, class_name: SECONDARY_CLASS.into(), monitor_id: "D2".into(), visible: true }, None),
                ],
                fail_set_visible: false,
                explorer_running: true,
            };
            Fake(Arc::new(Mutex::new(world)))
        }
    }

    impl TaskbarSystem for Fake {
        fn try_get_app_bar_state(&self) -> Option<i32> {
            let w = self.0.lock().unwrap();
            w.explorer_running.then_some(w.flags)
        }
        fn try_set_app_bar_state(&self, flags: i32) -> bool {
            self.0.lock().unwrap().flags = flags;
            true
        }
        fn taskbar_windows(&self) -> Vec<TaskbarWindow> {
            let w = self.0.lock().unwrap();
            if w.explorer_running { w.windows.iter().map(|(x, _)| x.clone()).collect() } else { Vec::new() }
        }
        fn is_window(&self, handle: i64) -> bool {
            self.0.lock().unwrap().windows.iter().any(|(w, _)| w.handle == handle)
        }
        fn try_set_visible(&self, handle: i64, visible: bool) -> bool {
            let mut w = self.0.lock().unwrap();
            if w.fail_set_visible && visible {
                return false;
            }
            w.windows.iter_mut().find(|(x, _)| x.handle == handle).map(|(x, _)| x.visible = visible).is_some()
        }
        fn try_get_region(&self, handle: i64) -> Option<Option<Vec<u8>>> {
            self.0.lock().unwrap().windows.iter().find(|(x, _)| x.handle == handle).map(|(_, r)| r.clone())
        }
        fn try_set_region(&self, handle: i64, data: Option<&[u8]>) -> bool {
            let mut w = self.0.lock().unwrap();
            w.windows.iter_mut().find(|(x, _)| x.handle == handle).map(|(_, r)| *r = data.map(<[u8]>::to_vec)).is_some()
        }
        fn try_clip_empty(&self, handle: i64) -> bool {
            self.try_set_region(handle, Some(&region(0)))
        }
        fn is_clipped_empty(&self, handle: i64) -> bool {
            self.0.lock().unwrap().windows.iter().find(|(x, _)| x.handle == handle).is_some_and(|(_, r)| r.as_deref() == Some(&region(0)[..]))
        }
    }

    fn coordinator(fake: &Fake, tag: &str) -> (TaskbarCoordinator, PathBuf) {
        let dir = std::env::temp_dir().join(format!("fd-taskbar-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let file = dir.join("taskbar-state.json");
        (TaskbarCoordinator::with_pid(Box::new(fake.clone()), TaskbarStateStore::new(&file), |_| {}, || 4242), dir)
    }

    fn all_original(fake: &Fake) -> bool {
        let w = fake.0.lock().unwrap();
        w.flags == 0 && w.windows[0].0.visible && w.windows[1].0.visible && w.windows[0].1 == Some(region(7)) && w.windows[1].1.is_none()
    }

    #[test]
    fn hide_completely_then_restore_returns_everything() {
        let fake = Fake::new();
        let (c, dir) = coordinator(&fake, "hide");
        assert_eq!(c.apply(TaskbarMode::HideCompletely).outcome, TaskbarOutcome::Ok);
        {
            let w = fake.0.lock().unwrap();
            assert_eq!(w.flags & AUTO_HIDE, AUTO_HIDE);
            assert!(w.windows.iter().all(|(x, _)| !x.visible));
        }
        assert!(fake.is_clipped_empty(100) && fake.is_clipped_empty(200));
        assert!(c.has_pending_restore());
        assert_eq!(c.maintain(TaskbarMode::HideCompletely).outcome, TaskbarOutcome::NothingToDo);
        assert_eq!(c.restore().outcome, TaskbarOutcome::Ok);
        assert!(all_original(&fake));
        assert!(!c.has_pending_restore());
        assert_eq!(c.restore().outcome, TaskbarOutcome::NothingToDo);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn auto_hide_that_was_already_on_is_left_alone() {
        let fake = Fake::new();
        fake.0.lock().unwrap().flags = AUTO_HIDE;
        let (c, dir) = coordinator(&fake, "autohide");
        assert_eq!(c.apply(TaskbarMode::AutoHide).outcome, TaskbarOutcome::Ok);
        assert_eq!(c.restore().outcome, TaskbarOutcome::Ok);
        assert_eq!(fake.0.lock().unwrap().flags, AUTO_HIDE, "the user's own auto-hide must survive");
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn a_new_run_recovers_what_a_killed_run_left_behind() {
        let fake = Fake::new();
        let (c1, dir) = coordinator(&fake, "crash");
        assert_eq!(c1.apply(TaskbarMode::HideCompletely).outcome, TaskbarOutcome::Ok);
        drop(c1); // "killed": no restore ran
        let c2 = TaskbarCoordinator::with_pid(Box::new(fake.clone()), TaskbarStateStore::new(&dir.join("taskbar-state.json")), |_| {}, || 7);
        assert!(c2.has_pending_restore());
        assert_eq!(c2.recover_from_previous_run().outcome, TaskbarOutcome::Ok);
        assert!(all_original(&fake));
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn explorer_restart_gives_new_handles_and_apply_adapts() {
        let fake = Fake::new();
        let (c, dir) = coordinator(&fake, "restart");
        assert_eq!(c.apply(TaskbarMode::HideCompletely).outcome, TaskbarOutcome::Ok);
        {
            // Explorer restarts: fresh windows with new handles, nothing hidden
            let mut w = fake.0.lock().unwrap();
            w.windows = vec![
                (TaskbarWindow { handle: 101, class_name: MAIN_CLASS.into(), monitor_id: "D1".into(), visible: true }, Some(region(7))),
                (TaskbarWindow { handle: 201, class_name: SECONDARY_CLASS.into(), monitor_id: "D2".into(), visible: true }, None),
            ];
            w.flags = 0;
        }
        assert_eq!(c.maintain(TaskbarMode::HideCompletely).outcome, TaskbarOutcome::Ok);
        assert!(fake.is_clipped_empty(101));
        assert_eq!(c.restore().outcome, TaskbarOutcome::Ok);
        let w = fake.0.lock().unwrap();
        assert!(w.windows[0].0.visible && w.windows[1].0.visible && w.flags == 0);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn failing_restore_keeps_the_record_and_forced_restore_cleans_up() {
        let fake = Fake::new();
        let (c, dir) = coordinator(&fake, "force");
        assert_eq!(c.apply(TaskbarMode::HideCompletely).outcome, TaskbarOutcome::Ok);
        fake.0.lock().unwrap().fail_set_visible = true;
        assert_eq!(c.restore().outcome, TaskbarOutcome::RetryNeeded);
        assert!(c.has_pending_restore(), "the record is kept for another try");
        fake.0.lock().unwrap().fail_set_visible = false;
        assert_eq!(c.restore().outcome, TaskbarOutcome::Ok);

        // a lost record: the taskbar looks modified, only the forced path can fix it
        assert_eq!(c.apply(TaskbarMode::HideCompletely).outcome, TaskbarOutcome::Ok);
        std::fs::remove_file(dir.join("taskbar-state.json")).unwrap();
        let _ = std::fs::remove_file(dir.join("taskbar-state.json.bak"));
        assert_eq!(c.restore().outcome, TaskbarOutcome::NothingToDo);
        assert!(c.any_taskbar_window_hidden());
        assert_eq!(c.force_restore(true).outcome, TaskbarOutcome::Ok);
        // the original region is unknown without the record: the forced path clears the clipping and shows the windows
        assert!(!c.any_taskbar_window_hidden());
        assert_eq!(fake.0.lock().unwrap().flags & AUTO_HIDE, 0);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn no_taskbar_means_nothing_is_changed_and_no_record_is_written() {
        let fake = Fake::new();
        fake.0.lock().unwrap().explorer_running = false;
        let (c, dir) = coordinator(&fake, "none");
        assert_eq!(c.apply(TaskbarMode::HideCompletely).outcome, TaskbarOutcome::Refused);
        assert!(!c.has_pending_restore());
        let mut m: HashMap<&str, i32> = HashMap::new();
        m.insert("x", 1);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn record_survives_a_json_roundtrip_with_regions() {
        let rec = TaskbarStateRecord {
            windows: vec![TaskbarWindowRecord {
                handle: 5,
                class_name: MAIN_CLASS.into(),
                original_region: Some(region(3)),
                region_captured: true,
                ..Default::default()
            }],
            ..Default::default()
        };
        let text = serde_json::to_string(&rec).unwrap();
        assert!(text.contains("\"OriginalRegion\""));
        let back: TaskbarStateRecord = serde_json::from_str(&text).unwrap();
        assert_eq!(back, rec);
        assert!(back.validate().is_none());
        let bad = TaskbarStateRecord { windows: vec![TaskbarWindowRecord { handle: 0, ..Default::default() }], ..Default::default() };
        assert!(bad.validate().is_some());
    }
}
