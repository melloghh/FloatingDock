//! A launchable application as known to FloatingDock (pinned dock app, Launchpad tile, identity of a window group).

use std::collections::HashSet;

use serde::{Deserialize, Serialize};

use crate::string_enum;
use crate::win_path;
use crate::window_info::AppMatching;

string_enum! { pub enum AppSource { Unknown, AppsFolder, StartMenu, User, Window, WindowsTaskbar } }

fn new_id() -> String {
    let mut b = [0u8; 16];
    let _ = getrandom::fill(&mut b);
    hex::encode(b)
}

/// Only what is needed to launch an app, name it and find its icon is stored. Serialized with the PascalCase names of `pinned.json`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AppRef {
    pub id: String,
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub exe_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub aumid: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub shortcut_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub arguments: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub working_directory: Option<String>,
    /// Shell parsing name inside the AppsFolder (launch / icon of entries that have neither exe nor AUMID).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub shell_parsing_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub aliases: Option<Vec<String>>,
    pub source: AppSource,
    /// A persisted dock divider; never a launchable app or a Launchpad tile.
    pub is_separator: bool,
}

impl Default for AppRef {
    fn default() -> Self {
        Self {
            id: new_id(),
            name: String::new(),
            exe_path: None,
            aumid: None,
            shortcut_path: None,
            arguments: None,
            working_directory: None,
            shell_parsing_name: None,
            aliases: None,
            source: AppSource::Unknown,
            is_separator: false,
        }
    }
}

fn non_empty(s: &Option<String>) -> Option<&str> {
    s.as_deref().filter(|v| !v.is_empty())
}

impl AppRef {
    pub fn separator() -> AppRef {
        AppRef { name: "Separator".into(), is_separator: true, source: AppSource::User, ..Default::default() }
    }

    /// Stable identity used for de-duplication and for animation continuity (pin / unpin of a running app).
    pub fn key(&self) -> String {
        if self.is_separator {
            return format!("separator:{}", self.id);
        }
        let aumid = non_empty(&self.aumid);
        let exe = non_empty(&self.exe_path);
        if let Some(a) = aumid {
            if a.contains('!') {
                return format!("aumid:{}", a.to_lowercase());
            }
        }
        if let Some(e) = exe {
            if !AppMatching::is_host_exe(Some(e)) {
                return format!("exe:{}", AppMatching::canonical_exe_path(e));
            }
        }
        if let Some(a) = aumid {
            return format!("aumid:{}", a.to_lowercase());
        }
        if let Some(e) = exe {
            return format!("exe:{}", AppMatching::normalize_path(e));
        }
        if let Some(s) = non_empty(&self.shortcut_path) {
            return format!("lnk:{}", AppMatching::normalize_path(s));
        }
        if let Some(s) = non_empty(&self.shell_parsing_name) {
            return format!("shell:{}", s.to_lowercase());
        }
        format!("id:{}", self.id)
    }

    /// Names that may identify this app in an icon pack (exe name, AUMID parts, display name, shortcut name, aliases).
    pub fn icon_names(&self) -> Vec<String> {
        let mut raw: Vec<String> = Vec::new();
        if let Some(e) = self.exe_path.as_deref().filter(|s| !s.trim().is_empty()) {
            raw.push(win_path::file_name_without_extension(e).to_string());
            raw.push(win_path::file_name(e).to_string());
        }
        if let Some(a) = self.aumid.as_deref().filter(|s| !s.trim().is_empty()) {
            raw.push(a.to_string());
            let bang = a.find('!');
            let mut pkg = bang.map_or(a, |b| &a[..b]);
            if let Some(us) = pkg.find('_') {
                if us > 0 {
                    pkg = &pkg[..us];
                }
            }
            raw.push(pkg.to_string());
            if let Some(dot) = pkg.rfind('.') {
                if dot > 0 && dot < pkg.len() - 1 {
                    raw.push(pkg[dot + 1..].to_string());
                }
            }
            if let Some(b) = bang {
                if b < a.len() - 1 {
                    raw.push(a[b + 1..].to_string());
                }
            }
        }
        if !self.name.trim().is_empty() {
            raw.push(self.name.clone());
        }
        if let Some(s) = self.shortcut_path.as_deref().filter(|s| !s.trim().is_empty()) {
            raw.push(win_path::file_name_without_extension(s).to_string());
        }
        if let Some(list) = &self.aliases {
            raw.extend(list.iter().cloned());
        }
        let mut seen = HashSet::new();
        raw.into_iter().filter(|n| !n.trim().is_empty() && seen.insert(n.to_lowercase())).collect()
    }

    /// The object the UI receives: the stored fields plus the computed `Key`.
    pub fn to_node(&self) -> serde_json::Value {
        let mut v = serde_json::to_value(self).expect("app serializes");
        if let Some(o) = v.as_object_mut() {
            o.insert("Key".into(), serde_json::Value::String(self.key()));
        }
        v
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn key_prefers_packaged_aumid_then_exe() {
        let packaged = AppRef { aumid: Some("Pkg_abc!App".into()), exe_path: Some(r"C:\x\a.exe".into()), ..Default::default() };
        assert_eq!(packaged.key(), "aumid:pkg_abc!app");
        let exe = AppRef { exe_path: Some(r"C:\Prog/A.EXE\".into()), ..Default::default() };
        assert_eq!(exe.key(), r"exe:c:\prog\a.exe");
        let host = AppRef { exe_path: Some(r"C:\Windows\System32\ApplicationFrameHost.exe".into()), aumid: Some("Foo".into()), ..Default::default() };
        assert_eq!(host.key(), "aumid:foo");
        let sep = AppRef::separator();
        assert!(sep.key().starts_with("separator:"));
    }

    #[test]
    fn icon_names_are_ordered_and_unique() {
        let app = AppRef {
            name: "Calc".into(),
            exe_path: Some(r"C:\x\calc.exe".into()),
            aumid: Some("Microsoft.Calc_8wekyb!App".into()),
            ..Default::default()
        };
        let names = app.icon_names();
        assert_eq!(names[0], "calc");
        assert_eq!(names[1], "calc.exe");
        assert!(names.contains(&"Microsoft.Calc".to_string()));
        assert!(names.contains(&"Calc".to_string()) == false || names.iter().filter(|n| n.eq_ignore_ascii_case("calc")).count() == 1);
    }

    #[test]
    fn json_uses_stored_names_and_skips_nulls() {
        let app = AppRef { id: "x".into(), name: "A".into(), exe_path: Some("c:\\a.exe".into()), ..Default::default() };
        let v = app.to_node();
        assert_eq!(v["ExePath"], "c:\\a.exe");
        assert!(v.get("Aumid").is_none());
        assert_eq!(v["Source"], "Unknown");
        assert_eq!(v["IsSeparator"], false);
    }
}
