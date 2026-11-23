//! An icon pack: a folder of PNG / ICO files, optionally with a `manifest.json`; a folder of PNGs without manifest is a valid pack.

use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Component, Path, PathBuf};

use serde_json::{Map, Value};

use super::image_probe::{MAX_FILE_BYTES, MAX_PACK_IMAGES};
use super::name_matcher as nm;
use super::MatchRule;

pub const IMAGE_EXTENSIONS: [&str; 2] = [".png", ".ico"];
pub const MAX_TEXT_ENTRY_BYTES: u64 = 1024 * 1024; // manifest.json, readme

pub fn is_image_extension(ext: &str) -> bool {
    let e = ext.to_lowercase();
    IMAGE_EXTENSIONS.contains(&e.as_str())
}

#[derive(Debug, Clone, PartialEq)]
pub struct IconMatch {
    pub path: PathBuf,
    pub rule: MatchRule,
    pub matched_name: String,
}

#[derive(Debug, Clone, Default)]
pub struct IconPackManifest {
    pub id: Option<String>,
    pub name: Option<String>,
    pub version: Option<String>,
    pub description: Option<String>,
    /// name -> "file.png" or name -> { "file": "file.png", "aliases": ["..."] }, in file order
    pub icons: Vec<(String, Value)>,
}

/// Property names are case-insensitive, like the C# reader.
fn ci<'a>(map: &'a Map<String, Value>, name: &str) -> Option<&'a Value> {
    map.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v)
}

impl IconPackManifest {
    pub fn parse(text: &str) -> Result<IconPackManifest, String> {
        let v = crate::lenient_json::parse(text).map_err(|e| e.to_string())?;
        let Value::Object(map) = v else { return Err("the root is not an object".into()) };
        let s = |n: &str| ci(&map, n).and_then(Value::as_str).map(str::to_string);
        let icons = match ci(&map, "Icons") {
            Some(Value::Object(m)) => m.iter().map(|(k, v)| (k.clone(), v.clone())).collect(),
            _ => Vec::new(),
        };
        Ok(IconPackManifest { id: s("Id"), name: s("Name"), version: s("Version"), description: s("Description"), icons })
    }
}

#[derive(Debug, Clone)]
struct Entry {
    name: String,
    denoised: String,
    phonetic: String,
    path: PathBuf,
    dark: bool,
    from_manifest: bool,
    cyrillic: bool,
}

#[derive(Debug, Clone, Default)]
pub struct IconPack {
    pub id: String,
    pub name: String,
    pub version: String,
    pub description: String,
    pub directory: PathBuf,
    pub is_built_in: bool,
    pub warnings: Vec<String>,
    entries: Vec<Entry>,
    by_norm: HashMap<String, Vec<usize>>,
}

pub fn sanitize_id(id: Option<&str>) -> String {
    let Some(id) = id.filter(|s| !s.trim().is_empty()) else { return String::new() };
    let s: String = id.trim().chars().filter(|c| c.is_alphanumeric() || matches!(c, '-' | '_' | ' ' | '.')).collect();
    s.trim_matches([' ', '.']).to_string()
}

fn ord_ignore_case(a: &Path, b: &Path) -> std::cmp::Ordering {
    a.to_string_lossy().to_uppercase().cmp(&b.to_string_lossy().to_uppercase())
}

fn scan_dirs(root: &Path, depth: u32, out: &mut Vec<PathBuf>) {
    out.push(root.to_path_buf());
    if depth == 0 {
        return;
    }
    let Ok(rd) = fs::read_dir(root) else { return };
    let mut subs: Vec<PathBuf> = rd.flatten().filter(|e| e.file_type().map(|t| t.is_dir()).unwrap_or(false)).map(|e| e.path()).collect();
    subs.sort_by(|a, b| ord_ignore_case(a, b));
    for s in subs {
        scan_dirs(&s, depth - 1, out);
    }
}

/// Only a cheap size check; the header (dimensions) is checked by the image probe when the image is loaded or imported.
fn acceptable_image(path: &Path) -> Result<(), String> {
    match fs::metadata(path) {
        Ok(m) if m.len() > MAX_FILE_BYTES => Err("the file is too large".into()),
        Ok(m) if m.len() == 0 => Err("the file is empty".into()),
        Ok(_) => Ok(()),
        Err(e) => Err(e.to_string()),
    }
}

impl IconPack {
    pub fn load(directory: &Path, built_in: bool, fallback_name: Option<&str>) -> IconPack {
        let dir_name = fallback_name
            .map(str::to_string)
            .unwrap_or_else(|| directory.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default());
        let mut manifest = None;
        let mut warnings = Vec::new();
        let mf = directory.join("manifest.json");
        if mf.is_file() {
            let parsed = fs::metadata(&mf)
                .map_err(|e| e.to_string())
                .and_then(|m| if m.len() > MAX_TEXT_ENTRY_BYTES { Err("The manifest exceeds 1 MiB.".to_string()) } else { Ok(()) })
                .and_then(|_| fs::read_to_string(&mf).map_err(|e| e.to_string()))
                .and_then(|t| IconPackManifest::parse(t.trim_start_matches('\u{feff}')));
            match parsed {
                Ok(m) => manifest = Some(m),
                Err(e) => warnings.push(format!("manifest.json is invalid: {e}")),
            }
        }
        let id = sanitize_id(manifest.as_ref().and_then(|m| m.id.as_deref()));
        let mut pack = IconPack {
            id: if id.is_empty() { dir_name.clone() } else { id },
            name: manifest.as_ref().and_then(|m| m.name.clone()).unwrap_or(dir_name),
            version: manifest.as_ref().and_then(|m| m.version.clone()).unwrap_or_default(),
            description: manifest.as_ref().and_then(|m| m.description.clone()).unwrap_or_default(),
            directory: directory.to_path_buf(),
            is_built_in: built_in,
            warnings,
            entries: Vec::new(),
            by_norm: HashMap::new(),
        };
        pack.index(manifest.as_ref());
        pack
    }

    pub fn icon_count(&self) -> usize {
        self.entries.iter().map(|e| e.path.to_string_lossy().to_lowercase()).collect::<HashSet<_>>().len()
    }

    /// (name, path) of the light entries.
    pub fn icons(&self) -> impl Iterator<Item = (&str, &Path)> {
        self.entries.iter().filter(|e| !e.dark).map(|e| (e.name.as_str(), e.path.as_path()))
    }

    fn resolve_inside(&self, relative: &str) -> Option<PathBuf> {
        let rel_text = relative.replace('/', "\\");
        let rel = Path::new(&rel_text);
        let mut out = self.directory.clone();
        for c in rel.components() {
            match c {
                Component::Normal(p) => out.push(p),
                Component::CurDir => {}
                _ => return None, // a manifest cannot point outside the pack
            }
        }
        Some(out)
    }

    fn index(&mut self, manifest: Option<&IconPackManifest>) {
        // 1. explicit manifest entries
        if let Some(m) = manifest {
            for (name, value) in &m.icons {
                if self.entries.len() >= MAX_PACK_IMAGES {
                    self.warnings.push("Too many manifest entries: the rest was ignored.".into());
                    break;
                }
                let mut file: Option<String> = None;
                let mut names = vec![name.clone()];
                match value {
                    Value::String(s) => file = Some(s.clone()),
                    Value::Object(o) => {
                        file = ci(o, "file").and_then(Value::as_str).map(str::to_string);
                        if let Some(Value::Array(al)) = ci(o, "aliases") {
                            for a in al {
                                if names.len() >= 64 {
                                    break;
                                }
                                if let Some(s) = a.as_str().filter(|s| !s.is_empty()) {
                                    names.push(s.to_string());
                                }
                            }
                        }
                    }
                    _ => {}
                }
                let Some(file) = file.filter(|f| !f.trim().is_empty()) else {
                    self.warnings.push(format!("'{name}': no file"));
                    continue;
                };
                let Some(full) = self.resolve_inside(&file).filter(|p| p.is_file()) else {
                    self.warnings.push(format!("'{name}': file '{file}' not found"));
                    continue;
                };
                if let Err(why) = acceptable_image(&full) {
                    self.warnings.push(format!("'{name}': {why}"));
                    continue;
                }
                let room = MAX_PACK_IMAGES.saturating_sub(self.entries.len());
                for n in names.into_iter().take(room) {
                    self.add(&n, full.clone(), false, true);
                }
            }
        }
        // 2. scan folders (root first, then sub-folders alphabetically, 2 levels deep)
        let mut dirs = Vec::new();
        scan_dirs(&self.directory, 2, &mut dirs);
        let mut scanned = 0usize;
        for dir in dirs {
            let Ok(rd) = fs::read_dir(&dir) else { continue };
            let mut files: Vec<PathBuf> = rd.flatten().filter(|e| e.file_type().map(|t| t.is_file()).unwrap_or(false)).map(|e| e.path()).collect();
            files.sort_by(|a, b| ord_ignore_case(a, b));
            for file in files {
                let ext = file.extension().map(|e| format!(".{}", e.to_string_lossy())).unwrap_or_default();
                if !is_image_extension(&ext) {
                    continue;
                }
                let fname = file.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
                if let Err(why) = acceptable_image(&file) {
                    self.warnings.push(format!("'{fname}': {why}"));
                    continue;
                }
                scanned += 1;
                if scanned > MAX_PACK_IMAGES {
                    self.warnings.push("Too many icon files: the rest of the pack was ignored.".into());
                    return;
                }
                let mut stem = file.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
                if stem.to_lowercase().ends_with("_mask") {
                    continue;
                }
                let dark = stem.to_lowercase().ends_with("_dark");
                if dark {
                    stem.truncate(stem.len() - 5);
                }
                self.add(&stem, file, dark, false);
            }
        }
    }

    fn add(&mut self, name: &str, path: PathBuf, dark: bool, from_manifest: bool) {
        let norm = nm::normalize(name);
        if norm.is_empty() {
            return;
        }
        let idx = self.entries.len();
        self.entries.push(Entry {
            name: name.to_string(),
            denoised: nm::without_noise(name),
            phonetic: nm::phonetic(name),
            path,
            dark,
            from_manifest,
            cyrillic: nm::has_cyrillic(name),
        });
        self.by_norm.entry(norm).or_default().push(idx);
    }

    fn pick<'a>(&'a self, candidates: impl Iterator<Item = &'a Entry>, prefer_dark: bool) -> Option<&'a Entry> {
        let (mut first, mut manifest, mut dark_e, mut light_e) = (None, None, None, None);
        for c in candidates {
            first.get_or_insert(c);
            if c.from_manifest {
                manifest.get_or_insert(c);
            }
            if c.dark {
                dark_e.get_or_insert(c);
            } else {
                light_e.get_or_insert(c);
            }
        }
        if manifest.is_some() {
            return manifest;
        }
        if prefer_dark { dark_e.or(light_e).or(first) } else { light_e.or(dark_e).or(first) }
    }

    /// Finds the icon for an app given all its names.
    pub fn find(&self, app_names: &[String], prefer_dark: bool) -> Option<IconMatch> {
        if self.entries.is_empty() {
            return None;
        }
        let names: Vec<String> = nm::expand(app_names.iter().map(String::as_str)).into_iter().filter(|n| !n.trim().is_empty()).collect();
        if names.is_empty() {
            return None;
        }
        let hit = |e: &Entry, rule, n: &str| Some(IconMatch { path: e.path.clone(), rule, matched_name: n.to_string() });
        // 1. exact
        for n in &names {
            if let Some(list) = self.by_norm.get(&nm::normalize(n)) {
                if let Some(e) = self.pick(list.iter().map(|&i| &self.entries[i]), prefer_dark) {
                    return hit(e, MatchRule::Exact, n);
                }
            }
        }
        // 2. without noise words
        for n in &names {
            let d = nm::without_noise(n);
            if nm::is_too_generic(&d) {
                continue;
            }
            if let Some(e) = self.pick(self.entries.iter().filter(|x| x.denoised == d), prefer_dark) {
                return hit(e, MatchRule::WithoutNoise, n);
            }
        }
        // 3. same pronunciation, also across alphabets
        for n in &names {
            let ph = nm::phonetic(n);
            if nm::is_too_generic(&ph) {
                continue;
            }
            if let Some(e) = self.pick(self.entries.iter().filter(|x| x.phonetic == ph), prefer_dark) {
                return hit(e, MatchRule::SamePronunciation, n);
            }
        }
        // 4. one name contains the other
        for n in &names {
            let d = nm::without_noise(n);
            if nm::is_too_generic(&d) {
                continue;
            }
            if let Some(e) = self.pick(
                self.entries.iter().filter(|x| !nm::is_too_generic(&x.denoised) && nm::contains_match(&d, &x.denoised)),
                prefer_dark,
            ) {
                return hit(e, MatchRule::Contains, n);
            }
        }
        // 5. a one-letter difference in a Cyrillic name (transliteration is never exact)
        for n in &names {
            let ph = nm::phonetic(n);
            let n_cyr = nm::has_cyrillic(n);
            if let Some(e) = self.pick(self.entries.iter().filter(|x| (n_cyr || x.cyrillic) && nm::one_letter_apart(&ph, &x.phonetic)), prefer_dark) {
                return hit(e, MatchRule::OneLetterDifference, n);
            }
        }
        None
    }

    pub fn add_warning(&mut self, text: String) {
        self.warnings.push(text);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("fd-pack-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn finds_icons_by_exact_noise_and_pronunciation() {
        let d = temp("find");
        for f in ["photoshop.png", "Telegram.png", "chrome_dark.png", "chrome.png", "Фотошоп2.png", "x_mask.png"] {
            fs::write(d.join(f), b"123456789").unwrap();
        }
        let pack = IconPack::load(&d, false, None);
        let find = |n: &[&str]| pack.find(&n.iter().map(|s| s.to_string()).collect::<Vec<_>>(), true);
        assert_eq!(find(&["telegram.exe"]).unwrap().rule, MatchRule::Exact);
        let dark = find(&["Chrome"]).unwrap();
        assert!(dark.path.ends_with("chrome_dark.png"), "dark variant preferred");
        assert_eq!(pack.find(&["Chrome".to_string()], false).unwrap().path.file_name().unwrap(), "chrome.png");
        assert_eq!(find(&["Adobe Photoshop 2024"]).unwrap().rule, MatchRule::WithoutNoise);
        assert!(find(&["Nothing Like It"]).is_none());
        assert!(pack.icons().all(|(n, _)| n != "x_mask"), "mask files are not icons");
        let _ = fs::remove_dir_all(d);
    }

    #[test]
    fn manifest_entries_win_and_cannot_escape() {
        let d = temp("manifest");
        fs::write(d.join("a.png"), b"123456789").unwrap();
        fs::write(
            d.join("manifest.json"),
            r#"{"Id":"My Pack","name":"Mine","icons":{"Zed":{"file":"a.png","aliases":["zeta"]},"Bad":"../x.png"}}"#,
        )
        .unwrap();
        let pack = IconPack::load(&d, false, None);
        assert_eq!((pack.id.as_str(), pack.name.as_str()), ("My Pack", "Mine"));
        assert!(pack.find(&["zeta".to_string()], true).is_some());
        assert!(pack.warnings.iter().any(|w| w.contains("'Bad'")));
        let _ = fs::remove_dir_all(d);
    }
}
