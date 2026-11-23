//! Platform-neutral window snapshot and the rules that match windows to apps.

use md5::{Digest, Md5};

use crate::app_ref::{AppRef, AppSource};
use crate::win_path;

/// A taskbar-eligible top-level window.
#[derive(Debug, Clone, PartialEq)]
pub struct WindowInfo {
    pub hwnd: i64,
    pub process_id: i32,
    pub exe_path: Option<String>,
    pub aumid: Option<String>,
    pub title: Option<String>,
    pub class_name: Option<String>,
    pub is_minimized: bool,
}

impl WindowInfo {
    pub fn exe_file_name(&self) -> Option<&str> {
        self.exe_path.as_deref().filter(|p| !p.is_empty()).map(win_path::file_name)
    }
}

pub struct AppMatching;

const HOST_EXES: [&str; 2] = ["applicationframehost.exe", "wwahost.exe"];
pub const EXPLORER_AUMID: &str = "Microsoft.Windows.Explorer";

fn eq_ic(a: &str, b: &str) -> bool {
    a.to_lowercase() == b.to_lowercase()
}

fn some(s: &Option<String>) -> Option<&str> {
    s.as_deref().filter(|v| !v.is_empty())
}

impl AppMatching {
    pub fn is_explorer(app: &AppRef) -> bool {
        !app.is_separator
            && (app.aumid.as_deref().is_some_and(|a| eq_ic(a, EXPLORER_AUMID))
                || app.shell_parsing_name.as_deref().is_some_and(|a| eq_ic(a, EXPLORER_AUMID))
                || some(&app.exe_path).is_some_and(|e| eq_ic(win_path::file_name(e), "explorer.exe")))
    }

    /// Is the identity of a window final? A host process (ApplicationFrameHost / WWAHost) is never an identity by itself.
    pub fn is_identity_complete(exe_path: Option<&str>, aumid: Option<&str>, is_frame_host: bool) -> bool {
        let has_real_exe = exe_path.is_some_and(|e| !e.is_empty()) && !Self::is_host_exe(exe_path);
        if !is_frame_host {
            return has_real_exe || Self::is_packaged_aumid(aumid);
        }
        has_real_exe || aumid.is_some_and(|a| !a.is_empty())
    }

    pub fn is_host_exe(exe_path: Option<&str>) -> bool {
        match exe_path {
            Some(p) if !p.is_empty() => {
                let f = win_path::file_name(p).to_lowercase();
                HOST_EXES.contains(&f.as_str())
            }
            _ => false,
        }
    }

    pub fn normalize_path(path: &str) -> String {
        path.trim().replace('/', "\\").trim_end_matches('\\').to_lowercase()
    }

    /// Steam's client UI runs in a CEF helper. Only helpers inside that client's bin/cef tree belong to it.
    pub fn canonical_exe_path(path: &str) -> String {
        let normalized = Self::normalize_path(path);
        if let Some(cef) = normalized.find("\\bin\\cef\\") {
            if cef > 0 && win_path::file_name(&normalized) == "steamwebhelper.exe" {
                return format!("{}\\steam.exe", &normalized[..cef]);
            }
        }
        normalized
    }

    pub fn is_packaged_aumid(aumid: Option<&str>) -> bool {
        aumid.is_some_and(|a| !a.is_empty() && a.contains('!'))
    }

    /// True if the window belongs to the app (exe path, AUMID, or the Explorer special case).
    pub fn matches(app: &AppRef, w: &WindowInfo) -> bool {
        if app.is_separator {
            return false;
        }
        if let (Some(a), Some(b)) = (some(&app.aumid), some(&w.aumid)) {
            if eq_ic(a, b) {
                return true;
            }
        }
        if let (Some(a), Some(b)) = (some(&app.exe_path), some(&w.exe_path)) {
            if !Self::is_host_exe(Some(b)) && Self::canonical_exe_path(a) == Self::canonical_exe_path(b) {
                // two different packaged apps that share an executable are not the same app
                if Self::is_packaged_aumid(app.aumid.as_deref())
                    && Self::is_packaged_aumid(w.aumid.as_deref())
                    && !eq_ic(app.aumid.as_deref().unwrap_or(""), w.aumid.as_deref().unwrap_or(""))
                {
                    return false;
                }
                return true;
            }
        }
        // File Explorer pinned by AppID only: its windows may report no AUMID.
        app.aumid.as_deref().is_some_and(|a| eq_ic(a, EXPLORER_AUMID)) && w.exe_file_name().is_some_and(|f| eq_ic(f, "explorer.exe"))
    }

    /// The same Key, or the same (packaged) AUMID.
    pub fn are_equivalent(a: &AppRef, b: &AppRef) -> bool {
        a.key() == b.key()
            || (Self::is_packaged_aumid(a.aumid.as_deref()) && a.aumid.as_deref().zip(b.aumid.as_deref()).is_some_and(|(x, y)| eq_ic(x, y)))
    }

    /// Group key of a window that matches no pinned app; windows of the same program share it.
    pub fn group_key(w: &WindowInfo) -> String {
        if let Some(a) = some(&w.aumid) {
            if a.contains('!') {
                return format!("aumid:{}", a.to_lowercase());
            }
        }
        if let Some(e) = some(&w.exe_path) {
            if !Self::is_host_exe(Some(e)) {
                return format!("exe:{}", Self::canonical_exe_path(e));
            }
        }
        if let Some(a) = some(&w.aumid) {
            return format!("aumid:{}", a.to_lowercase());
        }
        if some(&w.exe_path).is_some() {
            return format!("win:{}", w.hwnd); // a host process whose real app is unknown (yet): keep it, never drop it
        }
        if w.process_id > 0 { format!("pid:{}", w.process_id) } else { format!("win:{}", w.hwnd) }
    }

    /// The app identity for an unpinned window group (used when pinning a running app).
    pub fn from_window(w: &WindowInfo, display_name: Option<&str>) -> AppRef {
        let exe_path = match some(&w.exe_path) {
            Some(e) if !Self::is_host_exe(Some(e)) => Some(Self::canonical_exe_path(e)),
            _ => None,
        };
        let mut app = AppRef {
            name: display_name.map(str::to_string).unwrap_or_else(|| Self::default_name(w)),
            exe_path,
            aumid: w.aumid.clone(),
            source: AppSource::Window,
            ..Default::default()
        };
        // A normal Win32 app is identified by its exe; keep the AUMID only when it is the only identity (packaged apps).
        if some(&app.exe_path).is_some() && some(&app.aumid).is_some_and(|a| !a.contains('!')) {
            app.aumid = None;
        }
        // The same window group must give the same identity every time.
        let text = format!(
            "{}|{}|{}",
            app.exe_path.as_deref().unwrap_or("").to_lowercase(),
            app.aumid.as_deref().unwrap_or(""),
            if app.exe_path.is_none() && app.aumid.is_none() { Self::group_key(w) } else { String::new() }
        );
        app.id = hex::encode(Md5::digest(text.as_bytes()));
        app
    }

    pub fn default_name(w: &WindowInfo) -> String {
        if let Some(e) = some(&w.exe_path) {
            if !Self::is_host_exe(Some(e)) {
                return win_path::file_name_without_extension(&Self::canonical_exe_path(e)).to_string();
            }
        }
        if let Some(a) = some(&w.aumid) {
            let pkg = a.split('!').next().unwrap_or("").split('_').next().unwrap_or("");
            return match pkg.rfind('.') {
                Some(d) => pkg[d + 1..].to_string(),
                None => pkg.to_string(),
            };
        }
        match w.title.as_deref() {
            Some(t) if !t.trim().is_empty() => t.to_string(),
            _ => "App".to_string(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn win(exe: Option<&str>, aumid: Option<&str>) -> WindowInfo {
        WindowInfo {
            hwnd: 7,
            process_id: 1,
            exe_path: exe.map(Into::into),
            aumid: aumid.map(Into::into),
            title: None,
            class_name: None,
            is_minimized: false,
        }
    }

    #[test]
    fn matches_by_exe_aumid_and_steam_helper() {
        let app = AppRef { exe_path: Some(r"C:\A\app.exe".into()), ..Default::default() };
        assert!(AppMatching::matches(&app, &win(Some(r"c:\a\APP.exe"), None)));
        assert!(!AppMatching::matches(&app, &win(Some(r"C:\Windows\ApplicationFrameHost.exe"), None)));
        let steam = AppRef { exe_path: Some(r"C:\Steam\steam.exe".into()), ..Default::default() };
        assert!(AppMatching::matches(&steam, &win(Some(r"C:\Steam\bin\cef\cef.win7x64\steamwebhelper.exe"), None)));
        let a = AppRef { aumid: Some("Pkg!A".into()), exe_path: Some(r"C:\x.exe".into()), ..Default::default() };
        assert!(!AppMatching::matches(&a, &win(Some(r"C:\x.exe"), Some("Pkg!B"))));
    }

    #[test]
    fn from_window_has_stable_id_and_name() {
        let w = win(Some(r"C:\Tools\Edit.exe"), None);
        let a = AppMatching::from_window(&w, None);
        let b = AppMatching::from_window(&w, None);
        assert_eq!(a.id, b.id);
        assert_eq!(a.id.len(), 32);
        assert_eq!(a.name, "edit");
        assert_eq!(AppMatching::group_key(&win(None, None)), "pid:1");
    }
}
