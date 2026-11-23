//! Shortcuts, known folders and the installed-apps catalogue. Enumerates the apps from the sources Windows itself uses for
//! "All apps": the AppsFolder (Win32 + packaged apps) and the user / common Start Menu shortcuts. Everything here must run on
//! an STA thread (the [`ShellWorker`](crate::ShellWorker)).

use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

use fd_core::{win_path, AppRef, AppSource};
use windows::core::{GUID, PCWSTR, PWSTR};
use windows::Win32::Foundation::HWND;
use windows::Win32::Storage::FileSystem::WIN32_FIND_DATAW;
use windows::Win32::System::Com::{
    CoCreateInstance, CoTaskMemFree, IPersistFile, CLSCTX_INPROC_SERVER, STGM,
};
use windows::Win32::UI::Shell::PropertiesSystem::{IPropertyStore, SHGetPropertyStoreFromParsingName, GPS_DEFAULT};
use windows::Win32::UI::Shell::{
    BHID_EnumItems, FOLDERID_AppsFolder, IEnumShellItems, IShellItem, IShellLinkW, SHGetKnownFolderItem, SHGetKnownFolderPath,
    ShellLink, KF_FLAG_DEFAULT, SIGDN, SIGDN_DESKTOPABSOLUTEPARSING, SIGDN_NORMALDISPLAY,
};

use crate::native::wide;
use crate::shell::{read_string, PKEY_APP_USER_MODEL_ID};

const SKIP_EXTENSIONS: [&str; 14] =
    [".chm", ".txt", ".html", ".htm", ".url", ".pdf", ".rtf", ".doc", ".docx", ".xml", ".ini", ".log", ".md", ".mht"];
const SKIP_NAME_PARTS: [&str; 10] =
    ["uninstall", "удалить", "видалити", "readme", "release notes", "documentation", "license", "help", "справка", "manual"];

pub struct ShortcutInfo {
    pub target: Option<String>,
    pub arguments: Option<String>,
    pub working_directory: Option<String>,
    pub icon_path: Option<String>,
    pub icon_index: i32,
    pub aumid: Option<String>,
    pub description: Option<String>,
}

pub fn expand_env(s: &str) -> String {
    // %NAME% -> value, unknown names stay as they are
    let mut out = String::new();
    let mut rest = s;
    while let Some(start) = rest.find('%') {
        out.push_str(&rest[..start]);
        match rest[start + 1..].find('%') {
            Some(len) => {
                let name = &rest[start + 1..start + 1 + len];
                match std::env::var(name) {
                    Ok(v) if !name.is_empty() => out.push_str(&v),
                    _ => out.push_str(&rest[start..start + len + 2]),
                }
                rest = &rest[start + len + 2..];
            }
            None => {
                out.push_str(&rest[start..]);
                rest = "";
            }
        }
    }
    out.push_str(rest);
    out
}

fn nz(buf: &[u16]) -> Option<String> {
    let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    let s = expand_env(&String::from_utf16_lossy(&buf[..end]));
    (!s.trim().is_empty()).then_some(s)
}

pub fn known_folder(id: &GUID) -> Option<String> {
    unsafe {
        let p: PWSTR = SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, None).ok()?;
        let s = p.to_string().ok();
        CoTaskMemFree(Some(p.0 as *const _));
        s
    }
}

/// `{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Foo\bar.exe` (AppsFolder parsing name) to a real path.
pub fn resolve_known_folder_prefix(parsing: &str) -> Option<String> {
    let b = parsing.as_bytes();
    if b.len() > 38 && b[0] == b'{' && b[37] == b'}' && b[38] == b'\\' {
        let g = parse_guid(&parsing[1..37])?;
        let root = known_folder(&g)?;
        return Some(format!("{}\\{}", root.trim_end_matches('\\'), &parsing[39..]));
    }
    None
}

fn parse_guid(s: &str) -> Option<GUID> {
    let hex: String = s.chars().filter(|c| *c != '-').collect();
    if hex.len() != 32 {
        return None;
    }
    u128::from_str_radix(&hex, 16).ok().map(GUID::from_u128)
}

/// The AppUserModel.ID property of a file (a `.lnk` of a Store app has one, its target is empty).
pub fn read_aumid_of_file(path: &str) -> Option<String> {
    unsafe {
        let name = wide(path);
        let store: IPropertyStore = SHGetPropertyStoreFromParsingName(PCWSTR(name.as_ptr()), None, GPS_DEFAULT).ok()?;
        read_string(&store, &PKEY_APP_USER_MODEL_ID)
    }
}

/// Resolves a `.lnk` without UI and without searching for moved targets (fast, predictable).
pub fn read_shortcut(lnk_path: &str) -> Option<ShortcutInfo> {
    unsafe {
        let link: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER).ok()?;
        let file: IPersistFile = windows::core::Interface::cast(&link).ok()?;
        let name = wide(lnk_path);
        file.Load(PCWSTR(name.as_ptr()), STGM(0)).ok()?;
        // SLR_NO_UI | SLR_NOUPDATE | SLR_NOSEARCH
        let _ = link.Resolve(HWND::default(), 0x1 | 0x8 | 0x10);
        let mut target = [0u16; 1024];
        let mut fd = WIN32_FIND_DATAW::default();
        let _ = link.GetPath(&mut target, &mut fd, 0);
        let mut args = [0u16; 2048];
        let _ = link.GetArguments(&mut args);
        let mut wd = [0u16; 1024];
        let _ = link.GetWorkingDirectory(&mut wd);
        let mut icon = [0u16; 1024];
        let mut idx = 0i32;
        let _ = link.GetIconLocation(&mut icon, &mut idx);
        let mut desc = [0u16; 512];
        let _ = link.GetDescription(&mut desc);
        Some(ShortcutInfo {
            target: nz(&target),
            arguments: nz(&args),
            working_directory: nz(&wd),
            icon_path: nz(&icon),
            icon_index: idx,
            aumid: read_aumid_of_file(lnk_path),
            description: nz(&desc),
        })
    }
}

pub fn app_from_shortcut(lnk_path: &str, source: AppSource) -> Option<AppRef> {
    let info = read_shortcut(lnk_path)?;
    let mut exe = info.target.clone();
    if let Some(e) = &exe {
        if !e.to_lowercase().ends_with(".exe") && !Path::new(e).exists() {
            exe = None;
        }
    }
    // a shortcut that points to a folder / document is not an application
    if exe.as_deref().is_some_and(|e| !e.to_lowercase().ends_with(".exe")) {
        return None;
    }
    if exe.is_none() && info.aumid.is_none() {
        return None;
    }
    let keep_aumid = info.aumid.as_deref().is_some_and(|a| exe.is_none() || a.contains('!'));
    Some(AppRef {
        name: win_path::file_name_without_extension(lnk_path).to_string(),
        exe_path: exe,
        aumid: if keep_aumid { info.aumid } else { None },
        shortcut_path: Some(lnk_path.to_string()),
        arguments: info.arguments,
        working_directory: info.working_directory,
        source,
        ..Default::default()
    })
}

/// The display name of an executable (its description, else its product name).
pub fn exe_display_name(exe_path: &str) -> Option<String> {
    use windows::Win32::Storage::FileSystem::{GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW};
    unsafe {
        let path = wide(exe_path);
        let size = GetFileVersionInfoSizeW(PCWSTR(path.as_ptr()), None);
        if size == 0 {
            return None;
        }
        let mut data = vec![0u8; size as usize];
        GetFileVersionInfoW(PCWSTR(path.as_ptr()), None, size, data.as_mut_ptr() as *mut _).ok()?;
        let query = |sub: &str| -> Option<String> {
            let q = wide(sub);
            let mut ptr: *mut std::ffi::c_void = std::ptr::null_mut();
            let mut len = 0u32;
            if !VerQueryValueW(data.as_ptr() as *const _, PCWSTR(q.as_ptr()), &mut ptr, &mut len).as_bool() || ptr.is_null() || len == 0 {
                return None;
            }
            let slice = std::slice::from_raw_parts(ptr as *const u16, len as usize);
            nz(slice)
        };
        let (mut lang, mut cp) = (0x0409u16, 0x04B0u16);
        let q = wide("\\VarFileInfo\\Translation");
        let mut ptr: *mut std::ffi::c_void = std::ptr::null_mut();
        let mut len = 0u32;
        if VerQueryValueW(data.as_ptr() as *const _, PCWSTR(q.as_ptr()), &mut ptr, &mut len).as_bool() && len >= 4 && !ptr.is_null() {
            let t = std::slice::from_raw_parts(ptr as *const u16, 2);
            (lang, cp) = (t[0], t[1]);
        }
        let key = |n: &str| format!("\\StringFileInfo\\{lang:04x}{cp:04x}\\{n}");
        query(&key("FileDescription")).or_else(|| query(&key("ProductName"))).map(|s| s.trim().to_string()).filter(|s| !s.is_empty())
    }
}

// ------------------------------------------------------------------------------------------------ catalogue

pub struct CatalogResult {
    pub apps: Vec<AppRef>,
    pub warnings: Vec<String>,
}

fn is_noise(a: &AppRef) -> bool {
    let lower = a.name.to_lowercase();
    if a.source != AppSource::User && SKIP_NAME_PARTS.iter().any(|p| lower.contains(p)) {
        return true;
    }
    if let Some(e) = a.exe_path.as_deref().filter(|e| !e.is_empty()) {
        let ext = win_path::extension(e).to_lowercase();
        if SKIP_EXTENSIONS.contains(&ext.as_str()) {
            return true;
        }
    }
    false
}

pub fn load_detailed() -> CatalogResult {
    let mut warnings = Vec::new();
    let mut all = Vec::new();
    enumerate_apps_folder(&mut all, &mut warnings);
    let names: HashSet<String> = all.iter().map(|a| a.name.to_lowercase()).collect();
    all.extend(enumerate_start_menu(&names));

    all.sort_by_key(|a| a.name.to_lowercase());
    let mut seen = HashSet::new();
    let apps = all.into_iter().filter(|a| !a.name.trim().is_empty() && !is_noise(a) && seen.insert(a.key())).collect();
    CatalogResult { apps, warnings }
}

pub fn load() -> Vec<AppRef> {
    load_detailed().apps
}

fn display_name(item: &IShellItem, sigdn: SIGDN) -> Option<String> {
    unsafe {
        let p = item.GetDisplayName(sigdn).ok()?;
        let s = p.to_string().ok();
        CoTaskMemFree(Some(p.0 as *const _));
        s
    }
}

fn enumerate_apps_folder(into: &mut Vec<AppRef>, warnings: &mut Vec<String>) {
    unsafe {
        let folder: IShellItem = match SHGetKnownFolderItem(&FOLDERID_AppsFolder, KF_FLAG_DEFAULT, None) {
            Ok(f) => f,
            Err(e) => {
                warnings.push(format!("AppsFolder: {e}"));
                return;
            }
        };
        let en: IEnumShellItems = match folder.BindToHandler(None, &BHID_EnumItems) {
            Ok(e) => e,
            Err(e) => {
                warnings.push(format!("AppsFolder: BindToHandler failed ({e})"));
                return;
            }
        };
        loop {
            let mut items: [Option<IShellItem>; 1] = [None];
            let mut fetched = 0u32;
            let hr = en.Next(&mut items, Some(&mut fetched));
            let Some(item) = items[0].take().filter(|_| hr.is_ok() && fetched == 1) else {
                if hr.is_err() {
                    warnings.push(format!("AppsFolder: enumeration stopped ({hr:?})"));
                }
                break;
            };
            add_item(&item, into);
        }
    }
}

fn add_item(item: &IShellItem, into: &mut Vec<AppRef>) {
    let (Some(name), Some(parsing)) = (display_name(item, SIGDN_NORMALDISPLAY), display_name(item, SIGDN_DESKTOPABSOLUTEPARSING)) else { return };
    let mut app = AppRef { name: name.clone(), source: AppSource::AppsFolder, shell_parsing_name: Some(parsing.clone()), ..Default::default() };
    if parsing.contains('!') || (!parsing.contains('\\') && !parsing.contains('/') && !parsing.contains(':')) {
        app.aumid = Some(parsing); // packaged app or explicit AUMID
    } else {
        let path = resolve_known_folder_prefix(&parsing).or_else(|| Path::new(&parsing).is_absolute().then(|| parsing.clone()));
        if let Some(path) = path {
            if path.to_lowercase().ends_with(".lnk") {
                let Some(mut from_lnk) = app_from_shortcut(&path, AppSource::AppsFolder) else { return };
                from_lnk.name = name;
                from_lnk.shell_parsing_name = Some(parsing);
                into.push(from_lnk);
                return;
            }
            app.exe_path = Some(path);
        }
    }
    into.push(app);
}

fn collect_lnk(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(rd) = fs::read_dir(dir) else { return };
    for e in rd.flatten() {
        let p = e.path();
        match e.file_type() {
            Ok(t) if t.is_dir() => collect_lnk(&p, out),
            Ok(t) if t.is_file() && p.extension().is_some_and(|x| x.eq_ignore_ascii_case("lnk")) => out.push(p),
            _ => {}
        }
    }
}

fn enumerate_start_menu(known_names: &HashSet<String>) -> Vec<AppRef> {
    let programs = |id: &GUID| known_folder(id);
    // FOLDERID_CommonPrograms / FOLDERID_Programs
    let roots = [
        programs(&GUID::from_u128(0x0139D44E_6AFE_49F2_8690_3DAFCAE6FFB8)),
        programs(&GUID::from_u128(0xA77F5D77_2E2B_44C3_A6A2_ABA601054A51)),
    ];
    let mut seen_roots = HashSet::new();
    let mut out = Vec::new();
    for root in roots.into_iter().flatten() {
        if !Path::new(&root).is_dir() || !seen_roots.insert(root.to_lowercase()) {
            continue;
        }
        let mut files = Vec::new();
        collect_lnk(Path::new(&root), &mut files);
        for lnk in files {
            let name = lnk.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
            if known_names.contains(&name.to_lowercase()) {
                continue; // AppsFolder already lists it (and resolves it more reliably)
            }
            if let Some(app) = app_from_shortcut(&lnk.to_string_lossy(), AppSource::StartMenu) {
                out.push(app);
            }
        }
    }
    out
}

/// Shortcuts that Windows itself keeps for the taskbar, oldest first.
pub fn read_windows_taskbar_pins() -> Vec<AppRef> {
    let Some(appdata) = std::env::var_os("APPDATA") else { return Vec::new() };
    let dir = Path::new(&appdata).join("Microsoft").join("Internet Explorer").join("Quick Launch").join("User Pinned").join("TaskBar");
    let Ok(rd) = fs::read_dir(&dir) else { return Vec::new() };
    let mut files: Vec<(std::time::SystemTime, PathBuf)> = rd
        .flatten()
        .filter(|e| e.path().extension().is_some_and(|x| x.eq_ignore_ascii_case("lnk")))
        .map(|e| (e.metadata().and_then(|m| m.created()).unwrap_or(std::time::UNIX_EPOCH), e.path()))
        .collect();
    files.sort_by_key(|(t, _)| *t);
    files
        .into_iter()
        .filter_map(|(_, p)| app_from_shortcut(&p.to_string_lossy(), AppSource::WindowsTaskbar))
        .map(|mut a| {
            a.source = AppSource::WindowsTaskbar;
            a
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn expands_environment_variables() {
        std::env::set_var("FD_TEST_VAR", "value");
        assert_eq!(expand_env("%FD_TEST_VAR%\\x"), "value\\x");
        assert_eq!(expand_env("%NO_SUCH_FD_VAR%\\x"), "%NO_SUCH_FD_VAR%\\x");
        assert_eq!(expand_env("100%"), "100%");
    }

    #[test]
    fn guid_prefix_of_apps_folder_names() {
        assert!(parse_guid("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E").is_some());
        assert!(resolve_known_folder_prefix("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\\Foo\\bar.exe").is_some());
        assert!(resolve_known_folder_prefix("C:\\plain\\path.exe").is_none());
    }
}
