//! Manages the folders below `%LOCALAPPDATA%\FloatingDock\IconPacks`: list, load, import / export ZIP, delete; plus the resolver
//! that picks an icon (user override > active pack).

use std::collections::HashSet;
use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use serde_json::{json, Map, Value};
use zip::ZipArchive;

use super::icon_pack::{is_image_extension, sanitize_id, IconPack, IconPackManifest, MAX_TEXT_ENTRY_BYTES};
use super::image_probe::{self, MAX_FILE_BYTES, MAX_PACKS, MAX_PACK_IMAGES};
use super::override_store::IconOverrideStore;
use super::{IconMatch, MatchRule};
use crate::AppRef;

pub const DEFAULT_PACK_ID: &str = "Default";
pub const CUSTOM_PACK_ID: &str = "Custom";
pub const MAX_TOTAL_BYTES: u64 = 256 * 1024 * 1024;
pub const MAX_ENTRIES: usize = 10_000;

const ALLOWED_EXTENSIONS: [&str; 5] = [".png", ".ico", ".json", ".md", ".txt"];
const FORBIDDEN_EXTENSIONS: [&str; 24] = [
    ".exe", ".dll", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".msi", ".msp", ".scr", ".com", ".lnk", ".url",
    ".hta", ".jar", ".cpl", ".reg", ".sys", ".pif", ".appx",
];

#[derive(Debug, Clone)]
pub struct IconPackError(pub String);

impl std::fmt::Display for IconPackError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl From<std::io::Error> for IconPackError {
    fn from(e: std::io::Error) -> Self {
        IconPackError(e.to_string())
    }
}

fn err<T>(m: impl Into<String>) -> Result<T, IconPackError> {
    Err(IconPackError(m.into()))
}

#[derive(Debug, Clone)]
pub struct ZipPreview {
    pub name: String,
    pub id: String,
    pub version: String,
    pub description: String,
    pub icon_count: usize,
    pub icon_names: Vec<String>,
    pub problems: Vec<String>,
}

pub fn is_built_in_id(id: &str) -> bool {
    id.eq_ignore_ascii_case(DEFAULT_PACK_ID) || id.eq_ignore_ascii_case(CUSTOM_PACK_ID)
}

fn ext_of(name: &str) -> String {
    let f = crate::win_path::file_name(name);
    f.rfind('.').filter(|&i| i > 0).map(|i| f[i..].to_lowercase()).unwrap_or_default()
}

fn is_legacy_bundled(pack: &IconPack) -> bool {
    matches!(pack.id.as_str(), "DockIcons1" | "DockIcons2" | "DockIcons3") && pack.description.contains("из архива владельца проекта")
}

fn is_inside(root: &Path, path: &Path) -> bool {
    let r = root.to_string_lossy().to_lowercase();
    let r = format!("{}\\", r.trim_end_matches(['\\', '/']));
    path.to_string_lossy().to_lowercase().replace('/', "\\").starts_with(&r.replace('/', "\\"))
}

fn is_safe_relative_path(name: &str) -> bool {
    if name.trim().is_empty() || name.chars().count() > 260 || name.starts_with('/') || name.contains(':') || name.contains('\0') {
        return false;
    }
    for seg in name.split('/') {
        if matches!(seg, ".." | ".") || seg.is_empty() || seg.ends_with('.') || seg.ends_with(' ') {
            return false;
        }
        if seg.chars().any(|c| (c as u32) < 32 || matches!(c, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*')) {
            return false;
        }
        let stem = seg.split('.').next().unwrap_or("").to_uppercase();
        if matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$") {
            return false;
        }
        if (stem.starts_with("COM") || stem.starts_with("LPT")) && stem.chars().count() == 4 && "123456789¹²³".contains(stem.chars().nth(3).unwrap()) {
            return false;
        }
    }
    true
}

pub struct IconPackService {
    root: PathBuf,
    temp_root: PathBuf,
}

type Entries = Vec<(usize, String)>;

impl IconPackService {
    pub fn new(icon_packs_dir: &Path, temp_dir: &Path) -> IconPackService {
        let _ = fs::create_dir_all(icon_packs_dir.join(DEFAULT_PACK_ID));
        let _ = fs::create_dir_all(icon_packs_dir.join(CUSTOM_PACK_ID));
        IconPackService { root: icon_packs_dir.to_path_buf(), temp_root: temp_dir.to_path_buf() }
    }

    pub fn root_directory(&self) -> &Path {
        &self.root
    }

    /// Only the three identifiable packs supplied by earlier releases are removed.
    pub fn remove_legacy_bundled_packs(&self) {
        for id in ["DockIcons1", "DockIcons2", "DockIcons3"] {
            let dir = self.root.join(id);
            if !dir.is_dir() || fs::symlink_metadata(&dir).map(|m| m.file_type().is_symlink()).unwrap_or(true) {
                continue;
            }
            if is_legacy_bundled(&IconPack::load(&dir, false, None)) {
                let _ = fs::remove_dir_all(&dir);
            }
        }
    }

    pub fn list_packs(&self) -> Vec<IconPack> {
        let Ok(rd) = fs::read_dir(&self.root) else { return Vec::new() };
        let mut dirs: Vec<PathBuf> = rd.flatten().filter(|e| e.file_type().map(|t| t.is_dir()).unwrap_or(false)).map(|e| e.path()).collect();
        dirs.sort_by_key(|d| d.to_string_lossy().to_uppercase());
        let mut list = Vec::new();
        for dir in dirs {
            let name = dir.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            if name.starts_with('.') {
                continue;
            }
            let pack = IconPack::load(&dir, is_built_in_id(&name), None);
            if !is_legacy_bundled(&pack) {
                list.push(pack);
            }
        }
        list
    }

    pub fn load(&self, id: &str) -> Option<IconPack> {
        let dir = self.root.join(id);
        if !id.contains(['\\', '/']) && dir.is_dir() && is_inside(&self.root, &dir) {
            let pack = IconPack::load(&dir, is_built_in_id(id), None);
            return if is_legacy_bundled(&pack) { None } else { Some(pack) };
        }
        self.list_packs().into_iter().find(|p| p.id.eq_ignore_ascii_case(id))
    }

    pub fn delete(&self, id: &str) -> bool {
        if is_built_in_id(id) {
            return false;
        }
        let Some(pack) = self.list_packs().into_iter().find(|p| {
            p.id.eq_ignore_ascii_case(id) || p.directory.file_name().is_some_and(|n| n.to_string_lossy().eq_ignore_ascii_case(id))
        }) else {
            return false;
        };
        if pack.is_built_in || !is_inside(&self.root, &pack.directory) {
            return false;
        }
        fs::remove_dir_all(&pack.directory).is_ok()
    }

    // ------------------------------------------------------------------ ZIP: inspect

    fn validate_entries(zip: &mut ZipArchive<File>, problems: &mut Vec<String>) -> (Entries, String) {
        let mut result: Entries = Vec::new();
        if zip.len() > MAX_ENTRIES {
            problems.push("Too many files in the archive.".into());
            return (result, String::new());
        }
        let mut declared: u64 = 0;
        let mut image_count = 0usize;
        let mut paths = HashSet::new();
        for i in 0..zip.len() {
            let Ok(e) = zip.by_index_raw(i) else { continue };
            let full_name = e.name().to_string();
            let name = full_name.replace('\\', "/");
            if name.ends_with('/') {
                continue; // directory entry
            }
            let file = crate::win_path::file_name(&name);
            if name.starts_with("__MACOSX/") || file == ".DS_Store" || file == "Thumbs.db" {
                continue;
            }
            if !is_safe_relative_path(&name) {
                problems.push(format!("Unsafe path in archive: {full_name}"));
                continue;
            }
            if !paths.insert(name.to_lowercase()) {
                problems.push(format!("Unsafe duplicate path in archive: {full_name}"));
                continue;
            }
            let ext = ext_of(&name);
            if FORBIDDEN_EXTENSIONS.contains(&ext.as_str()) {
                problems.push(format!("Forbidden file type in archive: {full_name}"));
                continue;
            }
            if !ALLOWED_EXTENSIONS.contains(&ext.as_str()) {
                problems.push(format!("Skipped unsupported file: {full_name}"));
                continue;
            }
            let is_image = is_image_extension(&ext);
            if e.size() > if is_image { MAX_FILE_BYTES } else { MAX_TEXT_ENTRY_BYTES } {
                problems.push(format!("The archive is too large ({full_name} exceeds the size limit for icons)."));
                continue;
            }
            if is_image {
                image_count += 1;
                if image_count > MAX_PACK_IMAGES {
                    problems.push("Too many icons in the archive.".into());
                    return (result, String::new());
                }
            }
            declared += e.size();
            result.push((i, name));
        }
        if declared > MAX_TOTAL_BYTES {
            problems.push("The archive is too large.".into());
        }
        // strip a single top-level folder ("MyPack/manifest.json" -> "manifest.json")
        let mut prefix = String::new();
        if !result.is_empty() && result.iter().all(|(_, n)| n.contains('/')) {
            let first = &result[0].1;
            let first = first[..first.find('/').unwrap() + 1].to_string();
            if result.iter().all(|(_, n)| n.starts_with(&first)) {
                for (_, n) in result.iter_mut() {
                    *n = n[first.len()..].to_string();
                }
                prefix = first;
            }
        }
        (result, prefix)
    }

    /// Validates the archive without extracting anything and reports what it contains.
    pub fn inspect(&self, zip_path: &Path) -> Result<ZipPreview, IconPackError> {
        let mut zip = ZipArchive::new(File::open(zip_path)?).map_err(|e| IconPackError(format!("The file is not a valid ZIP archive: {e}")))?;
        let mut problems = Vec::new();
        let (entries, prefix) = Self::validate_entries(&mut zip, &mut problems);
        let mut manifest = None;
        if let Some((idx, _)) = entries.iter().find(|(_, r)| r.eq_ignore_ascii_case("manifest.json")) {
            let mut text = String::new();
            let read = zip.by_index(*idx).map_err(|e| e.to_string()).and_then(|mut f| f.read_to_string(&mut text).map(|_| ()).map_err(|e| e.to_string()));
            match read.and_then(|_| IconPackManifest::parse(text.trim_start_matches('\u{feff}'))) {
                Ok(m) => manifest = Some(m),
                Err(e) => problems.push(format!("manifest.json is invalid: {e}")),
            }
        }
        let icons: Vec<String> = entries
            .iter()
            .filter(|(_, r)| is_image_extension(&ext_of(r)))
            .map(|(_, r)| {
                let f = crate::win_path::file_name(r);
                crate::win_path::file_name_without_extension(f).to_string()
            })
            .collect();
        if icons.is_empty() {
            problems.push("The archive contains no PNG or ICO files.".into());
        }
        let fallback = zip_path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
        let top = prefix.trim_end_matches('/').to_string();
        let mut id = sanitize_id(manifest.as_ref().and_then(|m| m.id.as_deref()));
        if id.is_empty() {
            id = sanitize_id(Some(if top.is_empty() { &fallback } else { &top }));
        }
        Ok(ZipPreview {
            name: manifest.as_ref().and_then(|m| m.name.clone()).unwrap_or_else(|| if top.is_empty() { fallback.clone() } else { top.clone() }),
            id,
            version: manifest.as_ref().and_then(|m| m.version.clone()).unwrap_or_default(),
            description: manifest.as_ref().and_then(|m| m.description.clone()).unwrap_or_default(),
            icon_count: icons.len(),
            icon_names: icons,
            problems,
        })
    }

    // ------------------------------------------------------------------ ZIP: import

    pub fn import(&self, zip_path: &Path, overwrite: bool) -> Result<IconPack, IconPackError> {
        let _ = fs::create_dir_all(&self.temp_root);
        let mut rnd = [0u8; 16];
        let _ = getrandom::fill(&mut rnd);
        let staging = self.temp_root.join(format!("import-{}", hex::encode(rnd)));
        let result = self.import_into(zip_path, &staging, overwrite);
        let _ = fs::remove_dir_all(&staging);
        result
    }

    fn import_into(&self, zip_path: &Path, staging: &Path, overwrite: bool) -> Result<IconPack, IconPackError> {
        let mut fallback_name = zip_path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
        {
            let mut zip = ZipArchive::new(File::open(zip_path)?).map_err(|e| IconPackError(format!("The file is not a valid ZIP archive: {e}")))?;
            let mut problems = Vec::new();
            let (entries, prefix) = Self::validate_entries(&mut zip, &mut problems);
            if !prefix.is_empty() {
                fallback_name = prefix.trim_end_matches('/').to_string();
            }
            let is_fatal = |p: &String| p.starts_with("Unsafe") || p.starts_with("Forbidden") || p.starts_with("The archive is too") || p.starts_with("Too many");
            if let Some(p) = problems.iter().find(|p| is_fatal(p)) {
                return err(p.clone());
            }
            if !entries.iter().any(|(_, r)| is_image_extension(&ext_of(r))) {
                return err("The archive contains no PNG or ICO files.");
            }
            fs::create_dir_all(staging)?;
            let mut total = 0u64;
            for (idx, rel) in &entries {
                let dest = staging.join(rel.replace('/', "\\"));
                if !is_inside(staging, &dest) {
                    return err(format!("Unsafe path in archive: {rel}"));
                }
                if let Some(d) = dest.parent() {
                    fs::create_dir_all(d)?;
                }
                // copy with a hard limit: the sizes in the zip header cannot be trusted
                let mut src = zip.by_index(*idx).map_err(|e| IconPackError(e.to_string()))?;
                let mut dst = File::create(&dest)?;
                let limit = if is_image_extension(&ext_of(rel)) { MAX_FILE_BYTES } else { MAX_TEXT_ENTRY_BYTES };
                let (mut written, mut buf) = (0u64, vec![0u8; 81920]);
                loop {
                    let n = src.read(&mut buf)?;
                    if n == 0 {
                        break;
                    }
                    written += n as u64;
                    total += n as u64;
                    if written > limit || total > MAX_TOTAL_BYTES {
                        return err("The archive is too large.");
                    }
                    dst.write_all(&buf[..n])?;
                }
            }
        }

        let skipped = remove_invalid_images(staging);
        if !all_files(staging).iter().any(|f| is_image_extension(&ext_of(&f.to_string_lossy()))) {
            return err(format!("No usable icons: {}", skipped.first().cloned().unwrap_or_else(|| "the archive contains no PNG or ICO files.".into())));
        }
        let pack = IconPack::load(staging, false, Some(&fallback_name));
        let mut manifest_id = sanitize_id(Some(&pack.id));
        if manifest_id.is_empty() {
            manifest_id = sanitize_id(zip_path.file_stem().map(|s| s.to_string_lossy().into_owned()).as_deref());
        }
        if manifest_id.is_empty() {
            manifest_id = "Imported".into();
        }
        if is_built_in_id(&manifest_id) {
            manifest_id.push_str("-imported");
        }
        if !staging.join("manifest.json").exists() || manifest_id != pack.id {
            rewrite_manifest_id(staging, &manifest_id, &pack.name);
        }
        let target = self.root.join(&manifest_id);
        if !is_inside(&self.root, &target) {
            return err("Invalid pack id.");
        }
        if !target.exists() {
            let count = fs::read_dir(&self.root).map(|r| r.flatten().filter(|e| e.path().is_dir()).count()).unwrap_or(0);
            if count >= MAX_PACKS {
                return err("Too many icon packs are installed. Delete one first.");
            }
        }
        if target.exists() {
            if !overwrite {
                return err(format!("A pack named '{manifest_id}' already exists."));
            }
            let mut rnd = [0u8; 8];
            let _ = getrandom::fill(&mut rnd);
            let backup = self.root.join(format!(".old-{manifest_id}-{}", hex::encode(rnd)));
            fs::rename(&target, &backup)?;
            if let Err(e) = move_dir(staging, &target) {
                let _ = fs::rename(&backup, &target);
                return Err(e.into());
            }
            let _ = fs::remove_dir_all(&backup);
        } else {
            move_dir(staging, &target)?;
        }
        let mut imported = IconPack::load(&target, false, None);
        for w in skipped {
            imported.add_warning(w);
        }
        Ok(imported)
    }

    // ------------------------------------------------------------------ ZIP: export

    pub fn export(&self, id: &str, zip_path: &Path) -> Result<(), IconPackError> {
        let pack = self
            .list_packs()
            .into_iter()
            .find(|p| p.id.eq_ignore_ascii_case(id) || p.directory.file_name().is_some_and(|n| n.to_string_lossy().eq_ignore_ascii_case(id)))
            .ok_or_else(|| IconPackError(format!("Pack '{id}' not found.")))?;
        let staging = PathBuf::from(format!("{}.tmp-export", zip_path.display()));
        let build = || -> Result<(), IconPackError> {
            let mut zip = zip::ZipWriter::new(File::create(&staging)?);
            let options = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
            let mut has_manifest = false;
            for file in all_files(&pack.directory) {
                let rel = file.strip_prefix(&pack.directory).unwrap_or(&file).to_string_lossy().replace('\\', "/");
                if !ALLOWED_EXTENSIONS.contains(&ext_of(&rel).as_str()) {
                    continue;
                }
                if rel.eq_ignore_ascii_case("manifest.json") {
                    has_manifest = true;
                }
                zip.start_file(rel, options).map_err(|e| IconPackError(e.to_string()))?;
                zip.write_all(&fs::read(&file)?)?;
            }
            if !has_manifest {
                let mut m = Map::new();
                m.insert("Id".into(), json!(pack.id));
                m.insert("Name".into(), json!(pack.name));
                m.insert("Version".into(), json!(if pack.version.is_empty() { "1.0.0" } else { &pack.version }));
                m.insert("Description".into(), json!(pack.description));
                zip.start_file("manifest.json", options).map_err(|e| IconPackError(e.to_string()))?;
                zip.write_all(serde_json::to_string_pretty(&Value::Object(m)).unwrap().as_bytes())?;
            }
            zip.finish().map_err(|e| IconPackError(e.to_string()))?;
            Ok(())
        };
        let r = build().and_then(|_| fs::rename(&staging, zip_path).map_err(Into::into));
        if r.is_err() {
            let _ = fs::remove_file(&staging);
        }
        r
    }
}

fn move_dir(from: &Path, to: &Path) -> std::io::Result<()> {
    fs::rename(from, to)
}

fn all_files(dir: &Path) -> Vec<PathBuf> {
    let mut out = Vec::new();
    let Ok(rd) = fs::read_dir(dir) else { return out };
    let mut items: Vec<_> = rd.flatten().collect();
    items.sort_by_key(|e| e.path().to_string_lossy().to_uppercase());
    for e in items {
        let Ok(t) = e.file_type() else { continue };
        if t.is_symlink() {
            continue;
        }
        if t.is_dir() {
            out.extend(all_files(&e.path()));
        } else {
            out.push(e.path());
        }
    }
    out
}

/// Deletes images whose header violates the icon limits (too many pixels, not really a PNG / ICO). One message per rejected file.
fn remove_invalid_images(dir: &Path) -> Vec<String> {
    let mut skipped = Vec::new();
    for f in all_files(dir) {
        if !is_image_extension(&ext_of(&f.to_string_lossy())) {
            continue;
        }
        if let Err(why) = image_probe::probe_file(&f) {
            let rel = f.strip_prefix(dir).unwrap_or(&f).to_string_lossy().replace('\\', "/");
            skipped.push(format!("Skipped {rel}: {why}"));
            let _ = fs::remove_file(&f);
        }
    }
    skipped
}

fn rewrite_manifest_id(dir: &Path, new_id: &str, name: &str) {
    let mf = dir.join("manifest.json");
    let mut obj: Map<String, Value> = match fs::read_to_string(&mf).ok().and_then(|t| crate::lenient_json::parse(&t).ok()) {
        Some(Value::Object(o)) => o,
        _ => Map::new(),
    };
    obj.retain(|k, _| !k.eq_ignore_ascii_case("id"));
    obj.insert("id".into(), json!(new_id));
    if !obj.keys().any(|k| k.eq_ignore_ascii_case("name")) {
        obj.insert("name".into(), json!(name));
    }
    let _ = fs::write(&mf, serde_json::to_string_pretty(&Value::Object(obj)).unwrap());
}

// ---------------------------------------------------------------------------------------------------- resolver

struct ResolverState {
    active: Option<Arc<IconPack>>,
    use_icon_pack: bool,
    prefer_dark: bool,
    active_id: String,
}

/// Priority: user override > active icon pack > (the caller falls back to the Windows icon).
pub struct IconResolver {
    overrides: Arc<IconOverrideStore>,
    packs: Arc<IconPackService>,
    state: Mutex<ResolverState>,
}

impl IconResolver {
    pub fn new(overrides: Arc<IconOverrideStore>, packs: Arc<IconPackService>) -> IconResolver {
        IconResolver {
            overrides,
            packs,
            state: Mutex::new(ResolverState { active: None, use_icon_pack: true, prefer_dark: true, active_id: DEFAULT_PACK_ID.into() }),
        }
    }

    pub fn configure(&self, use_icon_pack: bool, active_pack_id: &str, prefer_dark: bool) {
        {
            let mut st = self.state.lock().unwrap();
            st.use_icon_pack = use_icon_pack;
            st.prefer_dark = prefer_dark;
            st.active_id = active_pack_id.to_string();
        }
        self.reload();
    }

    /// Re-reads the active pack from disk (after import, file changes, or switching).
    pub fn reload(&self) {
        let (use_pack, id) = {
            let st = self.state.lock().unwrap();
            (st.use_icon_pack, st.active_id.clone())
        };
        let active = if use_pack { self.packs.load(&id).map(Arc::new) } else { None };
        self.state.lock().unwrap().active = active;
    }

    pub fn resolve(&self, app: &AppRef) -> Option<IconMatch> {
        if let Some(path) = self.overrides.try_get_for_app(app) {
            return Some(IconMatch { path, rule: MatchRule::Override, matched_name: "override".into() });
        }
        let (active, dark) = {
            let st = self.state.lock().unwrap();
            (st.active.clone(), st.prefer_dark)
        };
        active?.find(&app.icon_names(), dark)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn safe_paths() {
        assert!(is_safe_relative_path("icons/a.png"));
        assert!(!is_safe_relative_path("../a.png"));
        assert!(!is_safe_relative_path("c:/a.png"));
        assert!(!is_safe_relative_path("icons/CON.png"));
        assert!(!is_safe_relative_path("a/b./c.png"));
        assert!(!is_safe_relative_path("a//b.png"));
    }

    #[test]
    fn export_then_inspect_then_import() {
        let root = std::env::temp_dir().join(format!("fd-packsvc-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let packs = root.join("IconPacks");
        let svc = IconPackService::new(&packs, &root.join("tmp"));
        let mine = packs.join("Mine");
        fs::create_dir_all(&mine).unwrap();
        let png = crate::icons::png_writer::encode_bgra(2, 2, &[255u8; 16], false).unwrap();
        fs::write(mine.join("telegram.png"), &png).unwrap();
        let zip = root.join("mine.zip");
        svc.export("Mine", &zip).unwrap();

        let preview = svc.inspect(&zip).unwrap();
        assert_eq!((preview.id.as_str(), preview.icon_count), ("Mine", 1));
        fs::remove_dir_all(&mine).unwrap();
        let imported = svc.import(&zip, true).unwrap();
        assert_eq!(imported.id, "Mine");
        assert!(svc.list_packs().iter().any(|p| p.id == "Mine" && p.icon_count() == 1));
        assert!(svc.delete("Mine"));
        assert!(!svc.delete("Default"));
        let _ = fs::remove_dir_all(&root);
    }
}
