//! Merges the three independent inputs (pinned apps, running windows, special items) into the ordered list of dock slots.
//! Keeps the order of un-pinned running apps stable (first seen first). Pure; no OS dependency.

use std::collections::{HashMap, HashSet};

use crate::app_ref::AppRef;
use crate::window_info::{AppMatching, WindowInfo};

pub const START_KEY: &str = "special:start";
pub const LAUNCHPAD_KEY: &str = "special:launchpad";
pub const SEPARATOR_KEY: &str = "special:separator";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DockEntryKind {
    Start,
    Launchpad,
    App,
    Separator,
}

impl DockEntryKind {
    pub fn as_str(self) -> &'static str {
        match self {
            DockEntryKind::Start => "Start",
            DockEntryKind::Launchpad => "Launchpad",
            DockEntryKind::App => "App",
            DockEntryKind::Separator => "Separator",
        }
    }
}

/// One slot of the dock as the model sees it (no UI state, no animation state).
#[derive(Debug, Clone)]
pub struct DockEntry {
    pub kind: DockEntryKind,
    pub key: String,
    pub name: String,
    pub app: Option<AppRef>,
    pub is_pinned: bool,
    pub is_active: bool,
    pub windows: Vec<WindowInfo>,
}

impl DockEntry {
    pub fn is_running(&self) -> bool {
        !self.windows.is_empty()
    }

    fn special(kind: DockEntryKind, key: &str, name: &str) -> DockEntry {
        DockEntry { kind, key: key.into(), name: name.into(), app: None, is_pinned: false, is_active: false, windows: Vec::new() }
    }
}

#[derive(Debug, Clone, Copy)]
pub struct DockEntryOptions {
    pub show_start: bool,
    pub show_launchpad: bool,
    pub show_pinned: bool,
    pub show_running: bool,
}

#[derive(Default)]
pub struct DockEntryReconciler {
    running_order: Vec<String>,
}

impl DockEntryReconciler {
    pub fn build(&mut self, opt: DockEntryOptions, pinned: &[AppRef], windows: &[WindowInfo], foreground_hwnd: i64) -> Vec<DockEntry> {
        let mut result = Vec::new();
        if opt.show_start {
            result.push(DockEntry::special(DockEntryKind::Start, START_KEY, "Start"));
        }
        if opt.show_launchpad {
            result.push(DockEntry::special(DockEntryKind::Launchpad, LAUNCHPAD_KEY, "Launchpad"));
        }

        // 1. assign every window to the first pinned app that claims it
        let mut pinned_windows: Vec<Vec<WindowInfo>> = vec![Vec::new(); pinned.len()];
        let mut unclaimed = Vec::new();
        for w in windows {
            match pinned.iter().position(|p| AppMatching::matches(p, w)) {
                Some(i) => pinned_windows[i].push(w.clone()),
                None => unclaimed.push(w.clone()),
            }
        }

        // 2. group the rest by program
        let mut groups: HashMap<String, Vec<WindowInfo>> = HashMap::new();
        for w in unclaimed {
            groups.entry(AppMatching::group_key(&w)).or_default().push(w);
        }
        // pinned apps that are hidden from the dock still show up while running
        let mut hidden_pinned_running: Vec<(AppRef, Vec<WindowInfo>)> = Vec::new();
        if !opt.show_pinned {
            for (i, app) in pinned.iter().enumerate() {
                if !pinned_windows[i].is_empty() {
                    hidden_pinned_running.push((app.clone(), pinned_windows[i].clone()));
                }
            }
        }

        // 3. maintain first-seen order of the un-pinned running keys
        let mut running_keys: HashSet<String> = groups.keys().cloned().collect();
        for (app, _) in &hidden_pinned_running {
            running_keys.insert(app.key());
        }
        self.running_order.retain(|k| running_keys.contains(k));
        let mut fresh: Vec<&String> = running_keys.iter().collect();
        fresh.sort();
        for k in fresh {
            if !self.running_order.contains(k) {
                self.running_order.push(k.clone());
            }
        }

        let is_active = |ws: &[WindowInfo]| foreground_hwnd != 0 && ws.iter().any(|w| w.hwnd == foreground_hwnd);

        let mut pinned_added = 0;
        if opt.show_pinned {
            let mut seen = HashSet::new();
            let mut shown: Vec<AppRef> = Vec::new();
            for (i, app) in pinned.iter().enumerate() {
                if !seen.insert(app.key()) {
                    continue; // the same application pinned twice is shown once
                }
                if app.is_separator {
                    result.push(DockEntry {
                        kind: DockEntryKind::Separator,
                        key: app.key(),
                        name: String::new(),
                        app: Some(app.clone()),
                        is_pinned: true,
                        is_active: false,
                        windows: Vec::new(),
                    });
                    continue;
                }
                if shown.iter().any(|o| AppMatching::are_equivalent(o, app)) {
                    continue; // ... also when it was pinned once by exe and once by AUMID
                }
                shown.push(app.clone());
                let ws = pinned_windows[i].clone();
                result.push(DockEntry {
                    kind: DockEntryKind::App,
                    key: app.key(),
                    name: app.name.clone(),
                    app: Some(app.clone()),
                    is_pinned: true,
                    is_active: is_active(&ws),
                    windows: ws,
                });
                pinned_added += 1;
            }
        }

        if opt.show_running || !opt.show_pinned {
            let mut running = Vec::new();
            for key in &self.running_order {
                if let Some(ws) = groups.get(key) {
                    if !opt.show_running {
                        continue;
                    }
                    let app = AppMatching::from_window(&ws[0], None);
                    running.push(DockEntry {
                        kind: DockEntryKind::App,
                        key: key.clone(),
                        name: app.name.clone(),
                        app: Some(app),
                        is_pinned: false,
                        is_active: is_active(ws),
                        windows: ws.clone(),
                    });
                } else if let Some((app, ws)) = hidden_pinned_running.iter().find(|(a, _)| a.key() == *key) {
                    running.push(DockEntry {
                        kind: DockEntryKind::App,
                        key: key.clone(),
                        name: app.name.clone(),
                        app: Some(app.clone()),
                        is_pinned: false,
                        is_active: is_active(ws),
                        windows: ws.clone(),
                    });
                }
            }
            if !running.is_empty() {
                if pinned_added > 0 {
                    result.push(DockEntry::special(DockEntryKind::Separator, SEPARATOR_KEY, ""));
                }
                result.extend(running);
            }
        }
        result
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn win(hwnd: i64, exe: &str) -> WindowInfo {
        WindowInfo {
            hwnd,
            process_id: hwnd as i32,
            exe_path: Some(exe.into()),
            aumid: None,
            title: None,
            class_name: None,
            is_minimized: false,
        }
    }

    fn pinned(exe: &str) -> AppRef {
        AppRef { name: exe.into(), exe_path: Some(exe.into()), ..Default::default() }
    }

    const ALL: DockEntryOptions = DockEntryOptions { show_start: true, show_launchpad: true, show_pinned: true, show_running: true };

    #[test]
    fn pinned_running_and_separator() {
        let mut r = DockEntryReconciler::default();
        let entries = r.build(ALL, &[pinned(r"c:\a.exe")], &[win(1, r"C:\a.exe"), win(2, r"C:\b.exe"), win(3, r"C:\b.exe")], 2);
        let keys: Vec<_> = entries.iter().map(|e| e.key.as_str()).collect();
        assert_eq!(keys[..2], [START_KEY, LAUNCHPAD_KEY]);
        assert_eq!(entries[2].windows.len(), 1);
        assert!(entries[2].is_pinned);
        assert_eq!(entries[3].key, SEPARATOR_KEY);
        assert_eq!(entries[4].windows.len(), 2);
        assert!(entries[4].is_active && !entries[2].is_active);
    }

    #[test]
    fn running_order_is_stable_and_hidden_pinned_still_show() {
        let mut r = DockEntryReconciler::default();
        let p = [pinned(r"c:\a.exe")];
        let first = r.build(DockEntryOptions { show_pinned: false, ..ALL }, &p, &[win(1, r"C:\a.exe")], 0);
        assert!(first.iter().any(|e| e.key == p[0].key() && !e.is_pinned));
        let order1 = r.build(ALL, &[], &[win(5, r"C:\z.exe")], 0);
        let order2 = r.build(ALL, &[], &[win(5, r"C:\z.exe"), win(6, r"C:\c.exe")], 0);
        let names: Vec<_> = order2.iter().filter(|e| e.kind == DockEntryKind::App).map(|e| e.name.clone()).collect();
        assert_eq!(names, ["z", "c"], "the app seen first stays first");
        assert_eq!(order1.len(), 3);
    }
}
