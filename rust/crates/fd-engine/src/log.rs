//! Append-only log in the profile's Logs folder; rolled over at 1 MB. Never fails.

use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use windows::Win32::System::SystemInformation::GetLocalTime;

static FILE: Mutex<Option<PathBuf>> = Mutex::new(None);

pub fn open(logs_dir: &Path) {
    let _ = fs::create_dir_all(logs_dir);
    let file = logs_dir.join("engine.log");
    if let Ok(meta) = fs::metadata(&file) {
        if meta.len() > 1_000_000 {
            let mut old = file.as_os_str().to_owned();
            old.push(".1");
            let _ = fs::rename(&file, PathBuf::from(old));
        }
    }
    *FILE.lock().unwrap_or_else(|e| e.into_inner()) = Some(file);
}

pub fn info(message: impl AsRef<str>) {
    write("INFO", message.as_ref());
}

pub fn warn(message: impl AsRef<str>) {
    write("WARN", message.as_ref());
}

pub fn error(message: impl AsRef<str>) {
    write("ERROR", message.as_ref());
}

fn write(level: &str, message: &str) {
    let Some(file) = FILE.lock().unwrap_or_else(|e| e.into_inner()).clone() else { return };
    let t = unsafe { GetLocalTime() };
    let line = format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03} {level} {message}\r\n",
        t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds
    );
    if let Ok(mut f) = OpenOptions::new().create(true).append(true).open(file) {
        let _ = f.write_all(line.as_bytes());
    }
}
