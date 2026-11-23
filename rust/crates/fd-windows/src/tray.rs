//! Notification-area icon with a native popup menu, a native message box, and "Start with Windows". The icon re-adds itself when
//! Explorer restarts.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use fd_core::Event;
use windows::core::PCWSTR;
use windows::Win32::Foundation::{HWND, LPARAM, POINT, WPARAM};
use windows::Win32::System::Registry::{
    RegCloseKey, RegCreateKeyExW, RegDeleteValueW, RegOpenKeyExW, RegQueryValueExW, RegSetValueExW, HKEY, HKEY_CURRENT_USER, KEY_READ,
    KEY_WRITE, REG_OPTION_NON_VOLATILE, REG_SZ,
};
use windows::Win32::UI::Shell::{
    ExtractIconExW, Shell_NotifyIconW, NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_TIP, NIIF_ERROR, NIIF_WARNING, NIM_ADD, NIM_DELETE, NIM_MODIFY,
    NOTIFYICONDATAW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, DestroyIcon, DestroyMenu, GetCursorPos, MessageBoxW, PostMessageW, SetForegroundWindow, TrackPopupMenu,
    HICON, IDYES, MB_ICONERROR, MB_ICONINFORMATION, MB_ICONWARNING, MB_OK, MB_SETFOREGROUND, MB_TOPMOST, MB_YESNO, MF_CHECKED, MF_GRAYED,
    MF_SEPARATOR, MF_STRING, TPM_BOTTOMALIGN, TPM_RETURNCMD, TPM_RIGHTBUTTON, WM_CONTEXTMENU, WM_LBUTTONUP, WM_NULL, WM_RBUTTONUP,
};

use crate::message_window::{MessageWindow, TRAY_CALLBACK_MESSAGE};
use crate::native::wide;

#[derive(Debug, Clone)]
pub struct MenuItem {
    pub id: i32,
    pub text: String,
    pub checked: bool,
    pub disabled: bool,
    pub separator: bool,
}

impl MenuItem {
    pub fn new(id: i32, text: impl Into<String>) -> Self {
        Self { id, text: text.into(), checked: false, disabled: false, separator: false }
    }

    pub fn separator() -> Self {
        Self { id: 0, text: String::new(), checked: false, disabled: false, separator: true }
    }
}

/// A native popup menu. It works reliably from windows that are never activated, dismisses on any outside click, and supports
/// the keyboard out of the box. Returns the chosen id, or 0 when dismissed.
pub fn show_menu(owner: HWND, items: &[MenuItem], at: Option<(i32, i32)>, bottom_align: bool) -> i32 {
    if items.is_empty() {
        return 0;
    }
    unsafe {
        let Ok(menu) = CreatePopupMenu() else { return 0 };
        for i in items {
            if i.separator {
                let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
            } else {
                let text = wide(&i.text);
                let mut flags = MF_STRING;
                if i.checked {
                    flags |= MF_CHECKED;
                }
                if i.disabled {
                    flags |= MF_GRAYED;
                }
                let _ = AppendMenuW(menu, flags, i.id as usize, PCWSTR(text.as_ptr()));
            }
        }
        let (x, y) = at.unwrap_or_else(|| {
            let mut p = POINT::default();
            let _ = GetCursorPos(&mut p);
            (p.x, p.y)
        });
        let _ = SetForegroundWindow(owner); // lets the menu close on an outside click
        let mut flags = TPM_RETURNCMD | TPM_RIGHTBUTTON;
        if bottom_align {
            flags |= TPM_BOTTOMALIGN;
        }
        let cmd = TrackPopupMenu(menu, flags, x, y, Some(0), owner, None);
        let _ = PostMessageW(Some(owner), WM_NULL, WPARAM(0), LPARAM(0));
        let _ = DestroyMenu(menu);
        cmd.0
    }
}

type MenuProvider = Box<dyn Fn() -> Vec<MenuItem> + Send + Sync>;

pub struct TrayIcon {
    window: Arc<MessageWindow>,
    tooltip: String,
    icon: Mutex<isize>,
    added: AtomicBool,
    pub left_click: Event<()>,
    pub menu_command: Event<i32>,
    menu_provider: Mutex<Option<MenuProvider>>,
}

fn copy_utf16<const N: usize>(target: &mut [u16; N], text: &str) {
    let w: Vec<u16> = text.encode_utf16().take(N - 1).collect();
    target[..w.len()].copy_from_slice(&w);
    target[w.len()] = 0;
}

impl TrayIcon {
    pub fn new(window: Arc<MessageWindow>, tooltip: &str, icon_source_exe: Option<&str>) -> Arc<TrayIcon> {
        let mut icon = 0isize;
        if let Some(exe) = icon_source_exe.filter(|e| !e.is_empty()) {
            let path = wide(exe);
            let (mut large, mut small) = ([HICON::default()], [HICON::default()]);
            unsafe {
                if ExtractIconExW(PCWSTR(path.as_ptr()), 0, Some(large.as_mut_ptr()), Some(small.as_mut_ptr()), 1) > 0 {
                    let chosen = if !small[0].0.is_null() { small[0] } else { large[0] };
                    icon = chosen.0 as isize;
                    if !large[0].0.is_null() && large[0] != chosen {
                        let _ = DestroyIcon(large[0]);
                    }
                }
            }
        }
        let tray = Arc::new(TrayIcon {
            window: window.clone(),
            tooltip: tooltip.to_string(),
            icon: Mutex::new(icon),
            added: AtomicBool::new(false),
            left_click: Event::new(),
            menu_command: Event::new(),
            menu_provider: Mutex::new(None),
        });
        let t = tray.clone();
        window.tray_message.subscribe(move |m| t.on_tray_message(*m));
        let t = tray.clone();
        window.taskbar_created.subscribe(move |_| t.add());
        tray.add();
        tray
    }

    pub fn set_menu_provider(&self, f: impl Fn() -> Vec<MenuItem> + Send + Sync + 'static) {
        *self.menu_provider.lock().unwrap() = Some(Box::new(f));
    }

    fn data(&self) -> NOTIFYICONDATAW {
        let icon = *self.icon.lock().unwrap();
        let mut d = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: self.window.hwnd(),
            uID: 1,
            uFlags: NIF_MESSAGE | NIF_TIP | if icon != 0 { NIF_ICON } else { Default::default() },
            uCallbackMessage: TRAY_CALLBACK_MESSAGE,
            hIcon: HICON(icon as *mut std::ffi::c_void),
            ..Default::default()
        };
        copy_utf16(&mut d.szTip, &self.tooltip);
        d
    }

    fn add(&self) {
        let d = self.data();
        let ok = unsafe { Shell_NotifyIconW(NIM_ADD, &d).as_bool() };
        self.added.store(ok, Ordering::SeqCst);
    }

    /// A balloon / toast next to the tray icon (the only user-visible channel the dock has when no window is open).
    pub fn show_balloon(&self, title: &str, text: &str, error: bool) {
        if !self.added.load(Ordering::SeqCst) {
            return;
        }
        let mut d = self.data();
        d.uFlags |= NIF_INFO;
        copy_utf16(&mut d.szInfoTitle, title);
        copy_utf16(&mut d.szInfo, text);
        d.dwInfoFlags = if error { NIIF_ERROR } else { NIIF_WARNING };
        unsafe {
            let _ = Shell_NotifyIconW(NIM_MODIFY, &d);
        }
    }

    fn on_tray_message(&self, mouse: u32) {
        if mouse == WM_LBUTTONUP {
            self.left_click.emit(&());
        } else if mouse == WM_RBUTTONUP || mouse == WM_CONTEXTMENU {
            let items = self.menu_provider.lock().unwrap().as_ref().map(|f| f()).unwrap_or_default();
            let cmd = show_menu(self.window.hwnd(), &items, None, true);
            if cmd != 0 {
                self.menu_command.emit(&cmd);
            }
        }
    }

    pub fn dispose(&self) {
        if self.added.swap(false, Ordering::SeqCst) {
            let d = self.data();
            unsafe {
                let _ = Shell_NotifyIconW(NIM_DELETE, &d);
            }
        }
        let icon = std::mem::take(&mut *self.icon.lock().unwrap());
        if icon != 0 {
            unsafe {
                let _ = DestroyIcon(HICON(icon as *mut std::ffi::c_void));
            }
        }
    }
}

// ------------------------------------------------------------------------------------------------ dialogs

const CAPTION: &str = "FloatingDock";

fn message_box(text: &str, flags: windows::Win32::UI::WindowsAndMessaging::MESSAGEBOX_STYLE) -> i32 {
    let (t, c) = (wide(text), wide(CAPTION));
    unsafe { MessageBoxW(None, PCWSTR(t.as_ptr()), PCWSTR(c.as_ptr()), flags | MB_SETFOREGROUND | MB_TOPMOST).0 }
}

pub fn confirm(text: &str, warning: bool) -> bool {
    message_box(text, MB_YESNO | if warning { MB_ICONWARNING } else { windows::Win32::UI::WindowsAndMessaging::MB_ICONQUESTION }) == IDYES.0
}

pub fn error_box(text: &str) {
    message_box(text, MB_OK | MB_ICONERROR);
}

pub fn info_box(text: &str) {
    message_box(text, MB_OK | MB_ICONINFORMATION);
}

// ------------------------------------------------------------------------------------------------ autostart

const RUN_KEY: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
const VALUE_NAME: &str = "FloatingDock";

/// "Start with Windows": a value in `HKCU\...\Run` (no admin rights, removed again when the option is turned off).
pub fn autostart_enabled() -> bool {
    unsafe {
        let key_name = wide(RUN_KEY);
        let mut key = HKEY::default();
        if RegOpenKeyExW(HKEY_CURRENT_USER, PCWSTR(key_name.as_ptr()), Some(0), KEY_READ, &mut key).is_err() {
            return false;
        }
        let name = wide(VALUE_NAME);
        let mut size = 0u32;
        let rc = RegQueryValueExW(key, PCWSTR(name.as_ptr()), None, None, None, Some(&mut size));
        let _ = RegCloseKey(key);
        rc.is_ok() && size > 2
    }
}

pub fn set_autostart(enabled: bool, exe_path: &str) -> Result<(), String> {
    unsafe {
        let key_name = wide(RUN_KEY);
        let mut key = HKEY::default();
        RegCreateKeyExW(HKEY_CURRENT_USER, PCWSTR(key_name.as_ptr()), None, PCWSTR::null(), REG_OPTION_NON_VOLATILE, KEY_WRITE, None, &mut key, None)
            .ok()
            .map_err(|e| e.to_string())?;
        let name = wide(VALUE_NAME);
        let result = if enabled {
            let value = wide(&format!("\"{exe_path}\" --autostart"));
            let bytes = std::slice::from_raw_parts(value.as_ptr() as *const u8, value.len() * 2);
            RegSetValueExW(key, PCWSTR(name.as_ptr()), None, REG_SZ, Some(bytes)).ok().map_err(|e| e.to_string())
        } else {
            let rc = RegDeleteValueW(key, PCWSTR(name.as_ptr()));
            // a missing value is fine: it is already off
            if rc.is_ok() || rc.0 == 2 { Ok(()) } else { Err(format!("error {}", rc.0)) }
        };
        let _ = RegCloseKey(key);
        result
    }
}
