//! Atomic, verified JSON persistence shared by all stores (port of `JsonFile`).

use std::fs;
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LoadStatus {
    /// The file does not exist (first run).
    Missing,
    /// Loaded and valid.
    Ok,
    /// The main file was damaged or unsupported; the last good copy (`*.bak`) was used.
    RecoveredFromBackup,
    /// The file exists but could not be used and no good backup exists. A copy is kept as `*.corrupt`.
    Corrupt,
}

#[derive(Debug)]
pub struct LoadResult<T> {
    pub value: Option<T>,
    pub status: LoadStatus,
    pub detail: Option<String>,
}

/// Why a parser refused a file's text.
#[derive(Debug)]
pub enum ParseError {
    /// Written by a newer version: never overwritten, never "repaired" from a backup.
    Unsupported(String),
    /// Damaged or not what was expected.
    Invalid(String),
}

impl From<serde_json::Error> for ParseError {
    fn from(e: serde_json::Error) -> Self {
        ParseError::Invalid(e.to_string())
    }
}

impl ParseError {
    pub fn invalid(message: impl Into<String>) -> Self {
        ParseError::Invalid(message.into())
    }
}

pub fn backup_path(path: &Path) -> PathBuf {
    let mut s = path.as_os_str().to_owned();
    s.push(".bak");
    PathBuf::from(s)
}

fn with_suffix(path: &Path, suffix: &str) -> PathBuf {
    let mut s = path.as_os_str().to_owned();
    s.push(suffix);
    PathBuf::from(s)
}

fn random_hex8() -> String {
    let mut b = [0u8; 4];
    let _ = getrandom::fill(&mut b);
    hex::encode(b)
}

/// Writes `bytes` so that a crash or a full disk never leaves a half-written file: a temp file in the same folder, flushed to disk
/// and read back, then swapped in (the previous version becomes `*.bak` unless `preserve_backup`), then verified again.
pub fn write_atomic(path: &Path, bytes: &[u8], preserve_backup: bool) -> io::Result<()> {
    if let Some(dir) = path.parent() {
        if !dir.as_os_str().is_empty() {
            fs::create_dir_all(dir)?;
        }
    }
    let tmp = with_suffix(path, &format!(".{}.tmp", random_hex8()));
    let result = (|| -> io::Result<()> {
        {
            let mut f = fs::OpenOptions::new().write(true).create_new(true).open(&tmp)?;
            f.write_all(bytes)?;
            f.sync_all()?; // to the disk, not just to the OS cache
        }
        if fs::read(&tmp)? != bytes {
            return Err(io::Error::other("The temporary file could not be verified."));
        }
        if path.exists() {
            if !preserve_backup {
                let _ = fs::copy(path, backup_path(path));
            }
            fs::rename(&tmp, path)?;
        } else {
            fs::rename(&tmp, path)?;
        }
        if fs::read(path)? != bytes {
            return Err(io::Error::other("The saved file could not be verified."));
        }
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&tmp);
    }
    result
}

/// Serializes with two-space indentation like the C# engine and saves atomically.
pub fn save<T: serde::Serialize>(path: &Path, value: &T) -> io::Result<()> {
    let text = serde_json::to_string_pretty(value).map_err(io::Error::other)?;
    write_atomic(path, text.as_bytes(), false)
}

/// Removes temp files that a crash left next to `path`.
pub fn clean_stale_temp(path: &Path) {
    let (Some(dir), Some(name)) = (path.parent(), path.file_name().and_then(|n| n.to_str())) else { return };
    let Ok(entries) = fs::read_dir(dir) else { return };
    let prefix = format!("{name}.");
    for e in entries.flatten() {
        let fname = e.file_name();
        let Some(fname) = fname.to_str() else { continue };
        if fname.starts_with(&prefix) && fname.ends_with(".tmp") {
            let old = e
                .metadata()
                .and_then(|m| m.modified())
                .map(|t| SystemTime::now().duration_since(t).unwrap_or_default() > Duration::from_secs(24 * 3600))
                .unwrap_or(false);
            if old {
                let _ = fs::remove_file(e.path());
            }
        }
    }
}

fn keep_corrupt_copy(path: &Path) {
    let _ = fs::copy(path, with_suffix(path, ".corrupt"));
}

fn read_text(path: &Path) -> io::Result<String> {
    let bytes = fs::read(path)?;
    let text = String::from_utf8(bytes).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e.to_string()))?;
    Ok(text.trim_start_matches('\u{feff}').to_string())
}

/// Loads `path` with `parse`. If the file is damaged the last good copy is tried; the damaged file is kept as `*.corrupt`
/// and is never silently replaced by an empty value.
pub fn load<T>(path: &Path, parse: impl Fn(&str) -> Result<T, ParseError>) -> LoadResult<T> {
    clean_stale_temp(path);
    let mut error: Option<String> = None;
    let mut exists = false;
    match read_text(path) {
        Ok(text) => {
            exists = true;
            match parse(&text) {
                Ok(v) => return LoadResult { value: Some(v), status: LoadStatus::Ok, detail: None },
                Err(ParseError::Unsupported(m)) => {
                    keep_corrupt_copy(path);
                    return LoadResult { value: None, status: LoadStatus::Corrupt, detail: Some(m) };
                }
                Err(ParseError::Invalid(m)) => error = Some(m),
            }
        }
        Err(e) if e.kind() == io::ErrorKind::NotFound => {}
        Err(e) => {
            exists = true;
            error = Some(e.to_string());
        }
    }

    let bak = backup_path(path);
    if bak.exists() {
        match read_text(&bak) {
            Ok(text) => match parse(&text) {
                Ok(value) => {
                    if exists {
                        keep_corrupt_copy(path);
                    }
                    // Repair the main file without rotating the damaged file over the good backup.
                    if let Err(e) = write_atomic(path, text.as_bytes(), true) {
                        error = Some(format!("{} Recovery could not be written: {}", error.clone().unwrap_or_default(), e).trim().to_string());
                    }
                    return LoadResult { value: Some(value), status: LoadStatus::RecoveredFromBackup, detail: error };
                }
                Err(ParseError::Unsupported(m) | ParseError::Invalid(m)) => {
                    exists = true;
                    error.get_or_insert(m);
                }
            },
            Err(e) => {
                exists = true;
                error.get_or_insert(e.to_string());
            }
        }
    }

    if !exists {
        return LoadResult { value: None, status: LoadStatus::Missing, detail: None };
    }
    keep_corrupt_copy(path);
    LoadResult { value: None, status: LoadStatus::Corrupt, detail: error }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("fd-core-test-{name}-{}", random_hex8()));
        fs::create_dir_all(&d).unwrap();
        d
    }

    fn parse_num(t: &str) -> Result<i32, ParseError> {
        t.trim().parse::<i32>().map_err(|e| ParseError::invalid(e.to_string()))
    }

    #[test]
    fn missing_then_roundtrip_then_backup_recovery() {
        let dir = temp_dir("roundtrip");
        let file = dir.join("a.json");
        assert_eq!(load(&file, parse_num).status, LoadStatus::Missing);
        write_atomic(&file, b"1", false).unwrap();
        write_atomic(&file, b"2", false).unwrap();
        assert_eq!(load(&file, parse_num).value, Some(2));
        fs::write(&file, b"garbage").unwrap();
        let r = load(&file, parse_num);
        assert_eq!(r.status, LoadStatus::RecoveredFromBackup);
        assert_eq!(r.value, Some(1));
        assert!(with_suffix(&file, ".corrupt").exists());
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn corrupt_without_backup_is_reported() {
        let dir = temp_dir("corrupt");
        let file = dir.join("a.json");
        fs::write(&file, b"nope").unwrap();
        let r = load(&file, parse_num);
        assert_eq!(r.status, LoadStatus::Corrupt);
        assert!(r.value.is_none());
        let _ = fs::remove_dir_all(dir);
    }
}
