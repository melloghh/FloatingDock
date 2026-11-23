//! Per-app icon overrides chosen by the user ("Change icon"). The chosen file is validated, copied into FloatingDock's data
//! folder and registered in `icon-overrides.json`. Order in [`set`](IconOverrideStore::set): validate -> copy to a temp file ->
//! verify -> rename -> save the JSON atomically -> only then delete the previous icon. A failure at any step leaves the
//! previous icon and the JSON untouched.

use std::collections::HashMap;
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use indexmap::IndexMap;
use md5::{Digest, Md5};
use serde::Serialize;
use serde_json::Value;

use super::image_probe::{self, ImageFormat, MAX_FILE_BYTES, MAX_OVERRIDES};
use super::packaged_identity;
use crate::event::Event;
use crate::json_file::{self, LoadStatus, ParseError};
use crate::window_info::{AppMatching, EXPLORER_AUMID};
use crate::{win_path, AppRef};

pub const CURRENT_SCHEMA_VERSION: i64 = 1;

#[derive(Serialize)]
#[serde(rename_all = "PascalCase")]
struct FileModel<'a> {
    schema_version: i64,
    overrides: &'a IndexMap<String, String>,
}

pub struct IconOverrideStore {
    file: PathBuf,
    dir: PathBuf,
    map: Mutex<IndexMap<String, String>>,
    load_status: LoadStatus,
    pub changed: Event<()>,
}

fn parse(text: &str) -> Result<IndexMap<String, String>, ParseError> {
    let node = crate::lenient_json::parse(text).map_err(|e| ParseError::Invalid(e.to_string()))?;
    let Value::Object(o) = node else { return Err(ParseError::invalid("The root is not an object.")) };
    let version = o.get("SchemaVersion").and_then(Value::as_i64).unwrap_or(0);
    if version > CURRENT_SCHEMA_VERSION {
        return Err(ParseError::Unsupported(format!("icon-overrides.json has a newer schema ({version}).")));
    }
    let Some(Value::Object(ov)) = o.get("Overrides") else { return Err(ParseError::invalid("The Overrides object is missing or invalid.")) };
    let mut map = IndexMap::new();
    for (k, v) in ov {
        if let Some(p) = v.as_str().filter(|p| !k.is_empty() && !p.trim().is_empty()) {
            map.insert(k.clone(), p.to_string());
        }
    }
    Ok(map)
}

fn random_hex(n: usize) -> String {
    let mut b = vec![0u8; n];
    let _ = getrandom::fill(&mut b);
    hex::encode(b)
}

impl IconOverrideStore {
    pub fn open(file: &Path, user_icons_dir: &Path) -> IconOverrideStore {
        let result = json_file::load(file, parse);
        let store = IconOverrideStore {
            file: file.to_path_buf(),
            dir: user_icons_dir.to_path_buf(),
            map: Mutex::new(result.value.unwrap_or_default()),
            load_status: result.status,
            changed: Event::new(),
        };
        store.clean_temp();
        store
    }

    pub fn load_status(&self) -> LoadStatus {
        self.load_status
    }

    /// True when `path` is a file directly inside the user icon folder (a hand-edited JSON cannot point anywhere else).
    pub fn is_owned_file(&self, path: &str) -> bool {
        if path.trim().is_empty() {
            return false;
        }
        let p = Path::new(path);
        let prefix = self.dir.to_string_lossy().to_lowercase();
        let parent = p.parent().map(|x| x.to_string_lossy().to_lowercase().trim_end_matches(['\\', '/']).to_string());
        parent.as_deref() == Some(prefix.trim_end_matches(['\\', '/'])) && !path.contains("..")
    }

    /// The override file for the app key, or `None` when there is none, it vanished, or it lies outside the user icon folder.
    pub fn try_get(&self, key: &str) -> Option<PathBuf> {
        let p = self.map.lock().unwrap().get(key).cloned()?;
        (self.is_owned_file(&p) && Path::new(&p).is_file()).then(|| PathBuf::from(p))
    }

    fn keys_for_app(&self, app: &AppRef) -> Vec<String> {
        let mut keys = vec![app.key()];
        // Explorer's AppsFolder pin has only an AppID; running windows often have only an executable.
        if AppMatching::is_explorer(app) {
            keys.push(format!("aumid:{}", EXPLORER_AUMID.to_lowercase()));
            let mut extra: Vec<String> = self
                .map
                .lock()
                .unwrap()
                .keys()
                .filter(|k| k.starts_with("exe:") && win_path::file_name(&k[4..]).eq_ignore_ascii_case("explorer.exe"))
                .cloned()
                .collect();
            extra.sort();
            keys.extend(extra);
        }
        if let Some(exe_path) = app.exe_path.as_deref().filter(|e| !e.trim().is_empty()) {
            let exe = format!("exe:{}", AppMatching::normalize_path(exe_path));
            if exe != app.key() {
                keys.push(exe.clone());
            }
            if let Some(family) = packaged_identity::from_exe_key(&exe) {
                let mut same: Vec<String> = self
                    .map
                    .lock()
                    .unwrap()
                    .keys()
                    .filter(|k| **k != exe && packaged_identity::from_exe_key(k).as_deref() == Some(family.as_str()))
                    .cloned()
                    .collect();
                same.sort_by(|a, b| b.cmp(a));
                keys.extend(same);
            }
        }
        let mut seen = std::collections::HashSet::new();
        keys.retain(|k| seen.insert(k.clone()));
        keys
    }

    pub fn try_get_for_app(&self, app: &AppRef) -> Option<PathBuf> {
        self.keys_for_app(app).iter().find_map(|k| self.try_get(k))
    }

    pub fn has(&self, app: &AppRef) -> bool {
        self.try_get_for_app(app).is_some()
    }

    fn ensure_writable(&self) -> Result<(), String> {
        if self.load_status == LoadStatus::Corrupt {
            return Err("The icon override list could not be read. Restore the JSON file or its backup before saving changes.".into());
        }
        Ok(())
    }

    fn save(&self, next: &IndexMap<String, String>) -> Result<(), String> {
        json_file::save(&self.file, &FileModel { schema_version: CURRENT_SCHEMA_VERSION, overrides: next }).map_err(|e| e.to_string())
    }

    pub fn reset_for_app(&self, app: &AppRef) -> Result<bool, String> {
        self.ensure_writable()?;
        let keys = self.keys_for_app(app);
        {
            let mut map = self.map.lock().unwrap();
            let mut next = map.clone();
            let mut changed = false;
            for k in &keys {
                changed |= next.shift_remove(k).is_some();
            }
            if !changed {
                return Ok(false);
            }
            self.save(&next)?;
            *map = next;
        }
        self.clean_unused_icons();
        self.changed.emit(&());
        Ok(true)
    }

    /// Removes the override. `Ok(false)` when there was none.
    pub fn reset(&self, key: &str) -> Result<bool, String> {
        self.ensure_writable()?;
        {
            let mut map = self.map.lock().unwrap();
            if !map.contains_key(key) {
                return Ok(false);
            }
            let mut next = map.clone();
            next.shift_remove(key);
            self.save(&next)?;
            *map = next;
        }
        self.clean_unused_icons();
        self.changed.emit(&());
        Ok(true)
    }

    /// Validates the file, copies it into the user icon folder and registers it for `key`.
    pub fn set(&self, key: &str, source: &Path, validated_bytes: Option<&[u8]>) -> Result<(), String> {
        self.ensure_writable()?;
        if key.is_empty() {
            return Err("appKey".into());
        }
        let ext = source.extension().map(|e| format!(".{}", e.to_string_lossy().to_lowercase())).unwrap_or_default();
        if ext != ".png" && ext != ".ico" {
            return Err("Only PNG and ICO files can be used as an icon.".into());
        }
        if !source.is_file() {
            return Err("Icon file not found.".into());
        }
        let info = image_probe::probe_file(source).map_err(|e| if e.is_empty() { "The file is not a valid icon.".to_string() } else { e })?;
        if (info.format == ImageFormat::Png) != (ext == ".png") {
            return Err("The file extension does not match its content.".into());
        }
        {
            let map = self.map.lock().unwrap();
            if !map.contains_key(key) && map.len() >= MAX_OVERRIDES {
                return Err("Too many custom icons. Reset some first.".into());
            }
        }
        fs::create_dir_all(&self.dir).map_err(|e| e.to_string())?;
        let hash = hex::encode_upper(Md5::digest(key.as_bytes()))[..16].to_string();
        let dest = self.dir.join(format!("{hash}_{}{ext}", random_hex(16)));
        let tmp = PathBuf::from(format!("{}.{}.tmp", dest.display(), random_hex(3)));
        let mut dest_created = false;
        let result = (|| -> Result<(), String> {
            // 1. copy to a temp file and verify it
            let bytes = match validated_bytes {
                Some(b) => {
                    if b.len() as u64 > MAX_FILE_BYTES {
                        return Err("The icon is too large.".into());
                    }
                    b.to_vec()
                }
                None => {
                    let b = fs::read(source).map_err(|e| e.to_string())?;
                    if b.len() as u64 > MAX_FILE_BYTES {
                        return Err("The icon is too large.".into());
                    }
                    b
                }
            };
            {
                let mut f = fs::OpenOptions::new().write(true).create_new(true).open(&tmp).map_err(|e| e.to_string())?;
                f.write_all(&bytes).map_err(|e| e.to_string())?;
                f.sync_all().map_err(|e| e.to_string())?;
            }
            image_probe::probe_file(&tmp).map_err(|e| if e.is_empty() { "The copied icon is not valid.".to_string() } else { e })?;
            // 2. make it the final name (nothing refers to it yet)
            fs::rename(&tmp, &dest).map_err(|e| e.to_string())?;
            dest_created = true;
            // 3. the new JSON content is prepared from a copy and saved atomically; the live map changes only after the save succeeded
            let mut map = self.map.lock().unwrap();
            let mut next = map.clone();
            if !next.contains_key(key) && next.len() >= MAX_OVERRIDES {
                return Err("Too many custom icons.".into());
            }
            next.insert(key.to_string(), dest.to_string_lossy().into_owned());
            self.save(&next)?;
            *map = next;
            Ok(())
        })();
        if let Err(e) = result {
            let _ = fs::remove_file(&tmp);
            if dest_created {
                let _ = fs::remove_file(&dest);
            }
            return Err(e);
        }
        // 4. only now the previous icon file may go
        self.clean_unused_icons();
        self.changed.emit(&());
        Ok(())
    }

    fn clean_temp(&self) {
        let Ok(rd) = fs::read_dir(&self.dir) else { return };
        for e in rd.flatten() {
            let p = e.path();
            if p.extension().is_some_and(|x| x == "tmp") {
                let old = e
                    .metadata()
                    .and_then(|m| m.modified())
                    .map(|t| std::time::SystemTime::now().duration_since(t).unwrap_or_default() > std::time::Duration::from_secs(86_400))
                    .unwrap_or(false);
                if old {
                    let _ = fs::remove_file(p);
                }
            }
        }
    }

    /// Removes icon files of this store that nothing (neither the JSON nor its backup) refers to; never unrelated user files.
    fn clean_unused_icons(&self) {
        let mut referenced: HashMap<String, ()> = self.map.lock().unwrap().values().map(|v| (v.to_lowercase(), ())).collect();
        let backup = json_file::backup_path(&self.file);
        if backup.is_file() {
            if let Ok(text) = fs::read_to_string(&backup) {
                if let Ok(m) = parse(&text) {
                    referenced.extend(m.values().map(|v| (v.to_lowercase(), ())));
                }
            }
        }
        let Ok(rd) = fs::read_dir(&self.dir) else { return };
        for e in rd.flatten() {
            let p = e.path();
            let stem = p.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
            let ext = p.extension().map(|s| s.to_string_lossy().to_lowercase()).unwrap_or_default();
            let ours = stem.chars().count() >= 18
                && stem.chars().nth(16) == Some('_')
                && stem.chars().take(16).all(|c| c.is_ascii_hexdigit())
                && (ext == "png" || ext == "ico");
            if ours && !referenced.contains_key(&p.to_string_lossy().to_lowercase()) {
                let _ = fs::remove_file(&p);
            }
        }
    }

    pub fn count(&self) -> usize {
        self.map.lock().unwrap().len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn png() -> Vec<u8> {
        crate::icons::png_writer::encode_bgra(2, 2, &[255u8; 16], false).unwrap()
    }

    #[test]
    fn set_get_reset_roundtrip() {
        let root = std::env::temp_dir().join(format!("fd-ov-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&root).unwrap();
        let (file, dir) = (root.join("icon-overrides.json"), root.join("UserIcons"));
        let src = root.join("mine.png");
        fs::write(&src, png()).unwrap();
        let store = IconOverrideStore::open(&file, &dir);
        let app = AppRef { exe_path: Some(r"C:\a\app.exe".into()), ..Default::default() };
        assert!(!store.has(&app));
        store.set(&app.key(), &src, None).unwrap();
        assert!(store.has(&app));
        let got = store.try_get_for_app(&app).unwrap();
        assert!(got.starts_with(&dir));
        // survives a reload
        assert!(IconOverrideStore::open(&file, &dir).has(&app));
        assert!(store.set(&app.key(), &root.join("a.txt"), None).is_err());
        assert_eq!(store.reset(&app.key()), Ok(true));
        assert!(got.exists(), "the backup JSON still refers to the file, so it is kept until the next clean-up");
        assert_eq!(store.reset(&app.key()), Ok(false));
        let _ = fs::remove_dir_all(&root);
    }
}
