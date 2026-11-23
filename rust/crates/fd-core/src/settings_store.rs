//! Loads, mutates and saves [`DockSettings`]. Changes are applied immediately (event) and persisted with a short debounce.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Condvar, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use crate::event::Event;
use crate::json_file::{self, LoadStatus};
use crate::settings::DockSettings;

const SAVE_DEBOUNCE: Duration = Duration::from_millis(400);
const SAVE_RETRY: Duration = Duration::from_secs(5);

struct State {
    current: Arc<DockSettings>,
    dirty: bool,
    deadline: Option<Instant>,
    closed: bool,
}

struct Inner {
    path: PathBuf,
    state: Mutex<State>,
    wake: Condvar,
    changed: Event<DockSettings>,
    save_failed: Event<String>,
    load_status: LoadStatus,
    load_detail: Option<String>,
}

pub struct SettingsStore {
    inner: Arc<Inner>,
}

impl SettingsStore {
    pub fn open(path: &Path) -> SettingsStore {
        let loaded = json_file::load(path, DockSettings::parse);
        let inner = Arc::new(Inner {
            path: path.to_path_buf(),
            state: Mutex::new(State {
                current: Arc::new(loaded.value.unwrap_or_else(|| DockSettings::default().normalize())),
                dirty: false,
                deadline: None,
                closed: false,
            }),
            wake: Condvar::new(),
            changed: Event::new(),
            save_failed: Event::new(),
            load_status: loaded.status,
            load_detail: loaded.detail,
        });
        let worker = inner.clone();
        thread::Builder::new()
            .name("settings-saver".into())
            .spawn(move || worker.run_saver())
            .expect("settings saver thread");
        SettingsStore { inner }
    }

    /// The settings from `path` without keeping the file open (a damaged file gives the defaults).
    pub fn load(path: &Path) -> DockSettings {
        if !path.exists() {
            return DockSettings::default().normalize();
        }
        json_file::load(path, DockSettings::parse).value.unwrap_or_else(|| DockSettings::default().normalize())
    }

    pub fn load_status(&self) -> LoadStatus {
        self.inner.load_status
    }

    pub fn load_detail(&self) -> Option<&str> {
        self.inner.load_detail.as_deref()
    }

    pub fn current(&self) -> Arc<DockSettings> {
        self.inner.state.lock().unwrap().current.clone()
    }

    /// Raised after every change with a fresh snapshot.
    pub fn changed(&self) -> &Event<DockSettings> {
        &self.inner.changed
    }

    /// Raised when the settings could not be written (the change stays in memory and the save is retried every 5 s).
    pub fn save_failed(&self) -> &Event<String> {
        &self.inner.save_failed
    }

    pub fn update(&self, mutate: impl FnOnce(&mut DockSettings)) {
        let snapshot = {
            let mut st = self.inner.state.lock().unwrap();
            let mut next = (*st.current).clone();
            mutate(&mut next);
            let next = Arc::new(next.normalize());
            st.current = next.clone();
            st.dirty = true;
            st.deadline = Some(Instant::now() + SAVE_DEBOUNCE);
            self.inner.wake.notify_all();
            next
        };
        self.inner.changed.emit(&snapshot);
    }

    pub fn flush(&self) {
        self.inner.flush();
    }
}

impl Inner {
    fn run_saver(self: Arc<Self>) {
        loop {
            {
                let mut st = self.state.lock().unwrap();
                loop {
                    if st.closed {
                        return;
                    }
                    match st.deadline {
                        None => st = self.wake.wait(st).unwrap(),
                        Some(d) => {
                            let now = Instant::now();
                            if now >= d {
                                break;
                            }
                            st = self.wake.wait_timeout(st, d - now).unwrap().0;
                        }
                    }
                }
                st.deadline = None;
            }
            self.flush();
        }
    }

    fn flush(&self) {
        let failure = {
            let mut st = self.state.lock().unwrap();
            if !st.dirty {
                return;
            }
            let result = if self.load_status == LoadStatus::Corrupt {
                Err("The settings file could not be read. Restore the file or its backup before saving changes.".to_string())
            } else {
                json_file::save(&self.path, &*st.current).map_err(|e| e.to_string())
            };
            match result {
                Ok(()) => {
                    st.dirty = false;
                    None
                }
                Err(e) => {
                    if !st.closed {
                        st.deadline = Some(Instant::now() + SAVE_RETRY);
                        self.wake.notify_all();
                    }
                    Some(e)
                }
            }
        };
        if let Some(e) = failure {
            self.save_failed.emit(&e);
        }
    }
}

impl Drop for SettingsStore {
    fn drop(&mut self) {
        {
            let mut st = self.inner.state.lock().unwrap();
            st.closed = true;
            self.inner.wake.notify_all();
        }
        self.inner.flush();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn update_persists_after_flush_and_reloads() {
        let dir = std::env::temp_dir().join(format!("fd-settings-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let file = dir.join("config.json");
        {
            let store = SettingsStore::open(&file);
            assert_eq!(store.load_status(), LoadStatus::Missing);
            store.update(|s| s.icon_size = 64);
            store.flush();
        }
        let again = SettingsStore::open(&file);
        assert_eq!(again.load_status(), LoadStatus::Ok);
        assert_eq!(again.current().icon_size, 64);
        let _ = std::fs::remove_dir_all(dir);
    }
}
