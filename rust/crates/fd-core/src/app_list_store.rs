//! Ordered, persisted list of apps: the dock's pinned apps and the Launchpad's user tiles share this implementation.
//! Every change is saved atomically; if saving fails the change is rolled back (memory always equals disk).

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use serde::Serialize;
use serde_json::Value;

use crate::app_ref::AppRef;
use crate::event::Event;
use crate::json_file::{self, LoadStatus, ParseError};

pub const CURRENT_SCHEMA_VERSION: i64 = 2;

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct FileModel<'a> {
    schema_version: i64,
    apps: &'a [AppRef],
}

pub struct AppListStore {
    path: PathBuf,
    apps: Mutex<Vec<AppRef>>,
    load_status: LoadStatus,
    load_detail: Option<String>,
    last_error: Mutex<Option<String>>,
    changed: Event<()>,
    save_failed: Event<String>,
}

/// Reads the file: the current schema, the legacy bare array and a missing SchemaVersion. A newer schema is refused.
pub fn parse(text: &str) -> Result<Vec<AppRef>, ParseError> {
    let node = crate::lenient_json::parse(text).map_err(|e| ParseError::Invalid(e.to_string()))?;
    let apps = match node {
        Value::Array(_) => node,
        Value::Object(mut o) => {
            let version = o.get("SchemaVersion").and_then(Value::as_i64).unwrap_or(0);
            if version > CURRENT_SCHEMA_VERSION {
                return Err(ParseError::Unsupported(format!("The list was written by a newer version (schema {version}).")));
            }
            o.remove("Apps").unwrap_or(Value::Null)
        }
        _ => return Err(ParseError::invalid("Unexpected file content.")),
    };
    if !apps.is_array() {
        return Err(ParseError::invalid("The Apps array is missing or invalid."));
    }
    let list: Vec<AppRef> = serde_json::from_value(apps).map_err(|e| ParseError::Invalid(format!("The Apps array is invalid: {e}")))?;
    Ok(list.into_iter().filter(|a| !a.key().is_empty()).collect())
}

fn dedupe(list: Vec<AppRef>) -> Vec<AppRef> {
    let mut seen = HashSet::new();
    list.into_iter().filter(|a| seen.insert(a.key())).collect()
}

impl AppListStore {
    pub fn open(path: &Path) -> Self {
        let result = json_file::load(path, parse);
        Self {
            path: path.to_path_buf(),
            apps: Mutex::new(dedupe(result.value.unwrap_or_default())),
            load_status: result.status,
            load_detail: result.detail,
            last_error: Mutex::new(None),
            changed: Event::new(),
            save_failed: Event::new(),
        }
    }

    pub fn load_status(&self) -> LoadStatus {
        self.load_status
    }

    pub fn load_detail(&self) -> Option<&str> {
        self.load_detail.as_deref()
    }

    pub fn last_error(&self) -> Option<String> {
        self.last_error.lock().unwrap().clone()
    }

    pub fn changed(&self) -> &Event<()> {
        &self.changed
    }

    pub fn save_failed(&self) -> &Event<String> {
        &self.save_failed
    }

    pub fn snapshot(&self) -> Vec<AppRef> {
        self.apps.lock().unwrap().clone()
    }

    pub fn contains(&self, key: &str) -> bool {
        self.apps.lock().unwrap().iter().any(|a| a.key() == key)
    }

    /// True when the app was added and saved. False when it was already there or saving failed.
    pub fn add(&self, app: &AppRef, index: Option<usize>) -> bool {
        let result = {
            let mut apps = self.apps.lock().unwrap();
            let key = app.key();
            if apps.iter().any(|a| a.key() == key) {
                return false;
            }
            let mut next = apps.clone();
            match index {
                Some(i) if i <= next.len() => next.insert(i, app.clone()),
                _ => next.push(app.clone()),
            }
            self.commit(&mut apps, next)
        };
        self.finish(result)
    }

    pub fn remove(&self, key: &str) -> bool {
        let result = {
            let mut apps = self.apps.lock().unwrap();
            let next: Vec<AppRef> = apps.iter().filter(|a| a.key() != key).cloned().collect();
            if next.len() == apps.len() {
                return false;
            }
            self.commit(&mut apps, next)
        };
        self.finish(result)
    }

    /// Moves the app so that it ends up at `new_index`.
    pub fn move_to(&self, key: &str, new_index: i64) -> bool {
        let result = {
            let mut apps = self.apps.lock().unwrap();
            let Some(from) = apps.iter().position(|a| a.key() == key) else { return false };
            let new_index = new_index.clamp(0, apps.len() as i64 - 1) as usize;
            if from == new_index {
                return false;
            }
            let mut next = apps.clone();
            let item = next.remove(from);
            next.insert(new_index, item);
            self.commit(&mut apps, next)
        };
        self.finish(result)
    }

    /// Moves within the visible app list, keeping persisted divider slots in place.
    pub fn move_application(&self, key: &str, new_index: i64) -> bool {
        let result = {
            let mut apps = self.apps.lock().unwrap();
            let mut visible: Vec<AppRef> = apps.iter().filter(|a| !a.is_separator).cloned().collect();
            let Some(from) = visible.iter().position(|a| a.key() == key) else { return false };
            let new_index = new_index.clamp(0, visible.len() as i64 - 1) as usize;
            if from == new_index {
                return false;
            }
            let item = visible.remove(from);
            visible.insert(new_index, item);
            let mut next = apps.clone();
            let mut it = visible.into_iter();
            for slot in next.iter_mut().filter(|a| !a.is_separator) {
                *slot = it.next().expect("same count");
            }
            self.commit(&mut apps, next)
        };
        self.finish(result)
    }

    pub fn replace_all(&self, new_apps: impl IntoIterator<Item = AppRef>) -> bool {
        let result = {
            let mut apps = self.apps.lock().unwrap();
            let next = dedupe(new_apps.into_iter().collect());
            self.commit(&mut apps, next)
        };
        self.finish(result)
    }

    /// Saves `next`; it becomes the live list only when the save succeeded. Must be called with the lock held.
    fn commit(&self, live: &mut Vec<AppRef>, next: Vec<AppRef>) -> Result<(), String> {
        let result = if self.load_status == LoadStatus::Corrupt {
            Err("The app list could not be read. Restore the JSON file or its backup before saving changes.".to_string())
        } else {
            json_file::save(&self.path, &FileModel { schema_version: CURRENT_SCHEMA_VERSION, apps: &next }).map_err(|e| e.to_string())
        };
        match &result {
            Ok(()) => {
                *live = next;
                *self.last_error.lock().unwrap() = None;
            }
            Err(e) => *self.last_error.lock().unwrap() = Some(e.clone()),
        }
        result
    }

    fn finish(&self, result: Result<(), String>) -> bool {
        match result {
            Ok(()) => {
                self.changed.emit(&());
                true
            }
            Err(e) => {
                self.save_failed.emit(&e);
                false
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn app(exe: &str) -> AppRef {
        AppRef { name: exe.into(), exe_path: Some(format!("C:\\{exe}.exe")), ..Default::default() }
    }

    fn temp_file(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("fd-list-test-{name}-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        dir.join("pinned.json")
    }

    #[test]
    fn add_move_remove_persist_and_reload() {
        let file = temp_file("persist");
        let store = AppListStore::open(&file);
        assert!(store.add(&app("a"), None));
        assert!(store.add(&app("b"), None));
        assert!(store.add(&AppRef::separator(), None));
        assert!(store.add(&app("c"), None));
        assert!(!store.add(&app("a"), None), "duplicates are refused");
        assert!(store.move_application(&app("c").key(), 0));
        let names: Vec<_> = store.snapshot().iter().map(|a| a.name.clone()).collect();
        assert_eq!(names, ["c", "a", "Separator", "b"], "the divider keeps its slot");
        assert!(store.remove(&app("a").key()));

        let again = AppListStore::open(&file);
        assert_eq!(again.load_status(), LoadStatus::Ok);
        assert_eq!(again.snapshot().len(), 3);
        let _ = std::fs::remove_dir_all(file.parent().unwrap());
    }

    #[test]
    fn reads_legacy_array_and_refuses_newer_schema() {
        let list = parse(r#"[{"Name":"X","ExePath":"C:\\x.exe"}]"#).unwrap();
        assert_eq!(list[0].name, "X");
        assert!(matches!(parse(r#"{"SchemaVersion":9,"Apps":[]}"#), Err(ParseError::Unsupported(_))));
    }
}
