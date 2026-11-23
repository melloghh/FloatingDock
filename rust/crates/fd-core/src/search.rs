//! App search: the palette policy (what is searchable), the name index and the usage-based suggestions.

use std::collections::HashMap;
use std::sync::Mutex;

use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use unicode_normalization::{char::is_combining_mark, UnicodeNormalization};

use crate::app_ref::{AppRef, AppSource};
use crate::json_file::{self, ParseError};

// ------------------------------------------------------------------------------------------------ palette policy

const NOISE_NAMES: [&str; 8] = ["uninstall", "деинсталл", "удалить", "видалити", "readme", "documentation", "release notes", "справка"];
const SYSTEM_IDS: [&str; 9] = [
    "Microsoft.Windows.", "Microsoft.WindowsBackup", "MicrosoftWindows.", "Windows.", "Microsoft.Backup", "Microsoft.Copilot",
    "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.PowerAutomateDesktop",
];

/// Palette-only filtering: explicit user apps remain searchable; the Launchpad keeps its catalog.
pub fn palette_includes(app: &AppRef) -> bool {
    if app.is_separator {
        return false;
    }
    if app.source == AppSource::User {
        return true;
    }
    let name = app.name.to_lowercase();
    if NOISE_NAMES.iter().any(|n| name.contains(n)) {
        return false;
    }
    if let Some(id) = app.aumid.as_deref() {
        let lower = id.to_lowercase();
        if SYSTEM_IDS.iter().any(|p| lower.starts_with(&p.to_lowercase())) || lower.contains("livecaptions") || lower.contains("voiceaccess") {
            return false;
        }
    }
    if let Some(exe) = app.exe_path.as_deref() {
        let path = exe.replace('/', "\\").to_lowercase();
        if path.contains("\\windows\\system32\\") || path.contains("\\windows\\syswow64\\") || path.contains("\\windows\\systemapps\\") {
            return false;
        }
    }
    !app.shell_parsing_name.as_deref().is_some_and(|s| s.starts_with("::{"))
}

// ------------------------------------------------------------------------------------------------ search index

/// Case-insensitive, Unicode-aware, diacritic-insensitive search over app names.
pub fn normalize(s: &str) -> String {
    let d: String = s.nfkd().filter(|c| !is_combining_mark(*c)).flat_map(char::to_lowercase).collect();
    d.nfc().collect()
}

pub struct AppSearchIndex {
    items: Vec<(AppRef, String)>,
}

impl AppSearchIndex {
    pub fn new(apps: impl IntoIterator<Item = AppRef>) -> Self {
        Self { items: apps.into_iter().map(|a| { let n = normalize(&a.name); (a, n) }).collect() }
    }

    /// Empty query returns everything in the original order; otherwise prefix matches, then word-prefix, then substring.
    pub fn search(&self, query: &str) -> Vec<AppRef> {
        let q = normalize(query);
        let q = q.trim();
        if q.is_empty() {
            return self.items.iter().map(|(a, _)| a.clone()).collect();
        }
        let mut scored: Vec<(u8, usize, &AppRef)> = Vec::new();
        for (i, (app, norm)) in self.items.iter().enumerate() {
            let score = if norm.starts_with(q) {
                0
            } else if norm.contains(&format!(" {q}")) {
                1
            } else if norm.contains(q) {
                2
            } else {
                continue;
            };
            scored.push((score, i, app));
        }
        scored.sort_by_key(|(s, i, _)| (*s, *i));
        scored.into_iter().map(|(_, _, a)| a.clone()).collect()
    }
}

// ------------------------------------------------------------------------------------------------ usage

const MAX_ENTRIES: usize = 100;
const MAX_COUNT: i64 = 1_000_000;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "PascalCase")]
pub struct UsageEntry {
    pub count: i64,
    /// ISO-8601 UTC, sortable as text.
    pub last_used: String,
}

/// Only apps successfully launched from search contribute to suggestions. Queries and file paths are not stored.
pub struct SearchUsage {
    entries: Mutex<IndexMap<String, UsageEntry>>,
}

fn parse_usage(text: &str) -> Result<IndexMap<String, UsageEntry>, ParseError> {
    let v = crate::lenient_json::parse(text).map_err(|e| ParseError::Invalid(e.to_string()))?;
    serde_json::from_value(v).map_err(|e| ParseError::Invalid(e.to_string()))
}

/// Now as `2026-11-23T10:05:30.1234567Z`.
pub fn iso_now() -> String {
    let d = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    let secs = d.as_secs() as i64;
    let (days, rem) = (secs.div_euclid(86_400), secs.rem_euclid(86_400));
    // civil-from-days (Howard Hinnant)
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let (y, doy) = (yoe + era * 400, doe - (365 * yoe + yoe / 4 - yoe / 100));
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = if month <= 2 { y + 1 } else { y };
    format!("{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}.{:07}Z", rem / 3600, rem % 3600 / 60, rem % 60, d.subsec_nanos() / 100)
}

impl SearchUsage {
    pub fn load(path: &std::path::Path) -> SearchUsage {
        let loaded = json_file::load(path, parse_usage).value.unwrap_or_default();
        let mut sorted: Vec<(String, UsageEntry)> = loaded.into_iter().filter(|(k, e)| !k.trim().is_empty() && e.count > 0).collect();
        sorted.sort_by(|a, b| b.1.last_used.cmp(&a.1.last_used));
        let mut entries: IndexMap<String, UsageEntry> = IndexMap::new();
        for (key, mut e) in sorted {
            e.count = e.count.min(MAX_COUNT);
            let lk = key.to_lowercase();
            if let Some((_, existing)) = entries.iter_mut().find(|(k, _)| k.to_lowercase() == lk) {
                existing.count = existing.count.max(e.count);
            } else if entries.len() < MAX_ENTRIES {
                entries.insert(key, e);
            }
        }
        SearchUsage { entries: Mutex::new(entries) }
    }

    pub fn has_entries(&self) -> bool {
        !self.entries.lock().unwrap().is_empty()
    }

    pub fn record(&self, app: &AppRef, now: String) {
        let mut e = self.entries.lock().unwrap();
        let key = app.key();
        let count = (e.get(&key).map_or(0, |x| x.count) + 1).min(MAX_COUNT);
        e.insert(key, UsageEntry { count, last_used: now });
        if e.len() > MAX_ENTRIES {
            let mut by_recency: Vec<(String, String)> = e.iter().map(|(k, v)| (k.clone(), v.last_used.clone())).collect();
            by_recency.sort_by(|a, b| b.1.cmp(&a.1));
            for (k, _) in by_recency.into_iter().skip(MAX_ENTRIES) {
                e.shift_remove(&k);
            }
        }
    }

    pub fn snapshot(&self) -> IndexMap<String, UsageEntry> {
        self.entries.lock().unwrap().clone()
    }

    pub fn suggestions(&self, apps: &[AppRef], limit: usize) -> Vec<AppRef> {
        let e = self.entries.lock().unwrap();
        let ci: HashMap<String, &UsageEntry> = e.iter().map(|(k, v)| (k.to_lowercase(), v)).collect();
        let mut hits: Vec<(&UsageEntry, &AppRef)> =
            apps.iter().filter(|a| palette_includes(a)).filter_map(|a| ci.get(&a.key().to_lowercase()).map(|u| (*u, a))).collect();
        hits.sort_by(|a, b| b.0.count.cmp(&a.0.count).then_with(|| b.0.last_used.cmp(&a.0.last_used)));
        hits.into_iter().take(limit).map(|(_, a)| a.clone()).collect()
    }
}

// ------------------------------------------------------------------------------------------------ Start layout export

#[derive(Debug, Clone, PartialEq)]
pub struct StartLayoutPin {
    pub kind: String,
    pub value: String,
}

/// Reads only the pins of `Export-StartLayout`, preserving Start order and flattening pinned folders.
pub fn parse_start_layout_pins(json: &str) -> Result<Vec<StartLayoutPin>, String> {
    if json.len() > 1024 * 1024 {
        return Err("Start layout is too large.".into());
    }
    let v: serde_json::Value = crate::lenient_json::parse(json).map_err(|e| e.to_string())?;
    let list = v.get("pinnedList").and_then(|l| l.as_array()).ok_or("The exported Start layout has no pinnedList.")?;
    let mut pins = Vec::new();
    let mut seen = std::collections::HashSet::new();
    fn read(array: &[serde_json::Value], pins: &mut Vec<StartLayoutPin>, seen: &mut std::collections::HashSet<String>) -> Result<(), String> {
        for item in array {
            if pins.len() >= 256 {
                return Err("Start layout has too many pins.".into());
            }
            let Some(obj) = item.as_object() else { continue };
            for key in ["desktopAppLink", "desktopAppId", "packagedAppId"] {
                let Some(text) = obj.get(key).and_then(|v| v.as_str()) else { continue };
                if !text.trim().is_empty() && text.chars().count() <= 32768 && seen.insert(format!("{}:{}", key.to_lowercase(), text.to_lowercase())) {
                    pins.push(StartLayoutPin { kind: key.to_string(), value: text.to_string() });
                }
                break;
            }
            if let Some(children) = obj.get("folder").and_then(|f| f.get("pinnedList")).and_then(|l| l.as_array()) {
                read(children, pins, seen)?;
            }
        }
        Ok(())
    }
    read(list, &mut pins, &mut seen)?;
    Ok(pins)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn app(name: &str, exe: &str) -> AppRef {
        AppRef { name: name.into(), exe_path: Some(exe.into()), ..Default::default() }
    }

    #[test]
    fn ranks_prefix_then_word_then_substring() {
        let idx = AppSearchIndex::new([app("Visual Studio Code", "c:\\a.exe"), app("Studio One", "c:\\b.exe"), app("Pro Studio", "c:\\c.exe"), app("Mudio", "c:\\d.exe")]);
        let names: Vec<_> = idx.search("studio").iter().map(|a| a.name.clone()).collect();
        assert_eq!(names, ["Studio One", "Visual Studio Code", "Pro Studio"]);
        assert_eq!(idx.search("  ").len(), 4);
        assert_eq!(normalize("Café"), "cafe");
    }

    #[test]
    fn palette_policy_hides_system_and_noise() {
        assert!(palette_includes(&app("Telegram", r"C:\Users\x\Telegram.exe")));
        assert!(!palette_includes(&app("Uninstall Foo", r"C:\foo\u.exe")));
        assert!(!palette_includes(&app("Calc", r"C:\Windows\System32\calc.exe")));
        assert!(!palette_includes(&AppRef { aumid: Some("Microsoft.Windows.Photos_x!App".into()), ..Default::default() }));
        assert!(palette_includes(&AppRef { name: "Uninstall mine".into(), source: AppSource::User, ..Default::default() }));
    }

    #[test]
    fn usage_counts_and_suggests() {
        let dir = std::env::temp_dir().join(format!("fd-usage-{}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        let file = dir.join("search-usage.json");
        let (a, b) = (app("A", "c:\\a.exe"), app("B", "c:\\b.exe"));
        let usage = SearchUsage::load(&file);
        assert!(!usage.has_entries());
        usage.record(&a, "2026-01-01T00:00:00.0000000Z".into());
        usage.record(&b, "2026-01-02T00:00:00.0000000Z".into());
        usage.record(&a, "2026-01-03T00:00:00.0000000Z".into());
        let s = usage.suggestions(&[b.clone(), a.clone()], 8);
        assert_eq!(s[0].name, "A");
        json_file::save(&file, &usage.snapshot()).unwrap();
        assert_eq!(SearchUsage::load(&file).snapshot().len(), 2);
        assert!(iso_now().ends_with('Z') && iso_now().len() == 28);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn start_layout_pins_flatten_folders() {
        let json = r#"{"pinnedList":[{"desktopAppLink":"%A%\\x.lnk"},{"packagedAppId":"Pkg!App"},{"folder":{"pinnedList":[{"desktopAppId":"Id1"}]}},{"packagedAppId":"pkg!app"}]}"#;
        let pins = parse_start_layout_pins(json).unwrap();
        assert_eq!(pins.iter().map(|p| p.kind.as_str()).collect::<Vec<_>>(), ["desktopAppLink", "packagedAppId", "desktopAppId"]);
        assert!(parse_start_layout_pins("{}").is_err());
    }
}
