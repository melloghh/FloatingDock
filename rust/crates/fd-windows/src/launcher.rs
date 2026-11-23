//! Launches applications through ShellExecute: shortcuts, Store apps (AppsFolder), plain executables. Never creates a window
//! of its own. Run it on the [`ShellWorker`](crate::ShellWorker) thread.

use std::path::Path;
use std::process::Command;

use fd_core::win_path;
use fd_core::AppRef;
use windows::core::PCWSTR;
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, VIRTUAL_KEY, VK_LWIN, VK_X,
};
use windows::Win32::UI::Shell::{ShellExecuteExW, SEE_MASK_FLAG_NO_UI, SHELLEXECUTEINFOW};
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

use crate::native::wide;

fn non_empty(s: &Option<String>) -> Option<&str> {
    s.as_deref().filter(|v| !v.is_empty())
}

pub fn launch(app: &AppRef, _run_as_new_instance: bool) -> bool {
    if let Some(s) = non_empty(&app.shortcut_path) {
        if Path::new(s).is_file() {
            return execute(s, None, None);
        }
    }
    if let Some(a) = non_empty(&app.aumid) {
        if a.contains('!') || non_empty(&app.exe_path).is_none() {
            return execute(&format!("shell:AppsFolder\\{a}"), None, None);
        }
    }
    if let Some(e) = non_empty(&app.exe_path) {
        if Path::new(e).is_file() {
            let dir = app.working_directory.clone().filter(|d| !d.trim().is_empty()).or_else(|| win_path::directory(e).map(str::to_string));
            return execute(e, app.arguments.as_deref(), dir.as_deref());
        }
    }
    if let Some(p) = non_empty(&app.shell_parsing_name) {
        return execute(&format!("shell:AppsFolder\\{p}"), None, None);
    }
    false
}

pub fn execute(file: &str, args: Option<&str>, working_dir: Option<&str>) -> bool {
    let file = wide(file);
    let args = args.filter(|a| !a.trim().is_empty()).map(wide);
    let dir = working_dir.filter(|a| !a.trim().is_empty()).map(wide);
    let mut info = SHELLEXECUTEINFOW {
        cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_FLAG_NO_UI,
        hwnd: HWND::default(),
        lpFile: PCWSTR(file.as_ptr()),
        lpParameters: args.as_ref().map_or(PCWSTR::null(), |a| PCWSTR(a.as_ptr())),
        lpDirectory: dir.as_ref().map_or(PCWSTR::null(), |d| PCWSTR(d.as_ptr())),
        nShow: SW_SHOWNORMAL.0,
        ..Default::default()
    };
    unsafe { ShellExecuteExW(&mut info).is_ok() }
}

/// Opens Explorer with the file selected.
pub fn reveal_in_explorer(path: &str) -> bool {
    if !Path::new(path).exists() {
        return false;
    }
    Command::new("explorer.exe").arg(format!("/select,\"{path}\"")).spawn().is_ok()
}

pub fn location_of(app: &AppRef) -> Option<String> {
    if let Some(s) = non_empty(&app.shortcut_path) {
        if Path::new(s).is_file() {
            return Some(s.to_string());
        }
    }
    if let Some(e) = non_empty(&app.exe_path) {
        if Path::new(e).is_file() {
            return Some(e.to_string());
        }
    }
    None
}

pub fn open_folder(folder: &Path) {
    let _ = std::fs::create_dir_all(folder);
    let _ = Command::new("explorer.exe").arg(folder).spawn();
}

fn key(vk: VIRTUAL_KEY, up: bool) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT { wVk: vk, dwFlags: if up { KEYEVENTF_KEYUP } else { Default::default() }, ..Default::default() },
        },
    }
}

fn send(inputs: &[INPUT]) {
    unsafe {
        SendInput(inputs, std::mem::size_of::<INPUT>() as i32);
    }
}

/// A tap of the Windows key.
pub fn toggle_start_menu() {
    send(&[key(VK_LWIN, false), key(VK_LWIN, true)]);
}

/// Win+X "power user" menu.
pub fn show_win_x_menu() {
    send(&[key(VK_LWIN, false), key(VK_X, false), key(VK_X, true), key(VK_LWIN, true)]);
}
