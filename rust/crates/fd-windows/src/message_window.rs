//! A hidden top-level window that receives what the UI does not expose: global hotkeys, display / work-area changes, the tray
//! icon callback, the "taskbar re-created" broadcast (Explorer restart) and the end-of-session notification.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex, OnceLock};

use fd_core::Event;
use windows::core::PCWSTR;
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Input::KeyboardAndMouse::{RegisterHotKey, UnregisterHotKey, HOT_KEY_MODIFIERS};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, RegisterClassExW, RegisterWindowMessageW, WNDCLASSEXW, WM_APP, WM_DISPLAYCHANGE,
    WM_ENDSESSION, WM_HOTKEY, WM_QUERYENDSESSION, WM_SETTINGCHANGE, WS_EX_TOOLWINDOW, WS_POPUP,
};

use crate::native::wide;

const CLASS_NAME: &str = "FloatingDock.MessageWindow";
pub const TRAY_CALLBACK_MESSAGE: u32 = WM_APP + 1;

pub struct MessageWindow {
    hwnd: isize,
    hotkeys: Mutex<HashSet<i32>>,
    pub hotkey_pressed: Event<i32>,
    pub display_changed: Event<()>,
    pub work_area_or_settings_changed: Event<()>,
    pub taskbar_created: Event<()>,
    pub session_ending: Event<()>,
    /// The mouse message of the tray icon (WM_LBUTTONUP, WM_RBUTTONUP, ...).
    pub tray_message: Event<u32>,
}

fn instances() -> &'static Mutex<HashMap<isize, Arc<MessageWindow>>> {
    static I: OnceLock<Mutex<HashMap<isize, Arc<MessageWindow>>>> = OnceLock::new();
    I.get_or_init(|| Mutex::new(HashMap::new()))
}

fn taskbar_created_message() -> u32 {
    static M: OnceLock<u32> = OnceLock::new();
    *M.get_or_init(|| unsafe { RegisterWindowMessageW(windows::core::w!("TaskbarCreated")) })
}

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    let window = instances().lock().ok().and_then(|i| i.get(&(hwnd.0 as isize)).cloned());
    let Some(w) = window else { return DefWindowProcW(hwnd, msg, wparam, lparam) };
    // never let an exception cross the native boundary
    let handled = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| w.handle(msg, wparam, lparam)));
    match handled {
        Ok(Some(r)) => r,
        Ok(None) => DefWindowProcW(hwnd, msg, wparam, lparam),
        Err(_) => LRESULT(0),
    }
}

impl MessageWindow {
    pub fn new() -> Result<Arc<MessageWindow>, String> {
        unsafe {
            let inst = GetModuleHandleW(None).map_err(|e| e.to_string())?;
            let class = wide(CLASS_NAME);
            let wc = WNDCLASSEXW {
                cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
                lpfnWndProc: Some(wnd_proc),
                hInstance: inst.into(),
                lpszClassName: PCWSTR(class.as_ptr()),
                ..Default::default()
            };
            RegisterClassExW(&wc); // a second registration in the same process fails harmlessly
            let title = wide("FloatingDock");
            let hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, PCWSTR(class.as_ptr()), PCWSTR(title.as_ptr()), WS_POPUP, 0, 0, 0, 0, None, None, Some(inst.into()), None)
                .map_err(|e| format!("CreateWindowEx failed: {e}"))?;
            let w = Arc::new(MessageWindow {
                hwnd: hwnd.0 as isize,
                hotkeys: Mutex::new(HashSet::new()),
                hotkey_pressed: Event::new(),
                display_changed: Event::new(),
                work_area_or_settings_changed: Event::new(),
                taskbar_created: Event::new(),
                session_ending: Event::new(),
                tray_message: Event::new(),
            });
            let _ = taskbar_created_message();
            instances().lock().unwrap().insert(w.hwnd, w.clone());
            Ok(w)
        }
    }

    pub fn handle(&self, msg: u32, wparam: WPARAM, lparam: LPARAM) -> Option<LRESULT> {
        if msg == taskbar_created_message() {
            self.taskbar_created.emit(&());
            return Some(LRESULT(0));
        }
        match msg {
            WM_HOTKEY => {
                self.hotkey_pressed.emit(&(wparam.0 as i32));
                Some(LRESULT(0))
            }
            WM_DISPLAYCHANGE => {
                self.display_changed.emit(&());
                Some(LRESULT(0))
            }
            WM_SETTINGCHANGE => {
                self.work_area_or_settings_changed.emit(&());
                Some(LRESULT(0))
            }
            TRAY_CALLBACK_MESSAGE => {
                self.tray_message.emit(&((lparam.0 as u32) & 0xFFFF));
                Some(LRESULT(0))
            }
            WM_QUERYENDSESSION => Some(LRESULT(1)),
            WM_ENDSESSION => {
                if wparam.0 != 0 {
                    self.session_ending.emit(&());
                }
                Some(LRESULT(0))
            }
            _ => None,
        }
    }

    pub fn hwnd(&self) -> HWND {
        HWND(self.hwnd as *mut std::ffi::c_void)
    }

    /// Registers a global hotkey; false if the combination is taken by another program.
    pub fn register_hotkey(&self, id: i32, modifiers: u32, vk: u32) -> bool {
        self.unregister_hotkey(id);
        // MOD_NOREPEAT
        let ok = unsafe { RegisterHotKey(Some(self.hwnd()), id, HOT_KEY_MODIFIERS(modifiers | 0x4000), vk).is_ok() };
        if ok {
            self.hotkeys.lock().unwrap().insert(id);
        }
        ok
    }

    pub fn unregister_hotkey(&self, id: i32) {
        if self.hotkeys.lock().unwrap().remove(&id) {
            unsafe {
                let _ = UnregisterHotKey(Some(self.hwnd()), id);
            }
        }
    }

    pub fn dispose(&self) {
        // the guard of the temporary must not live through the loop: unregister_hotkey locks the same mutex
        let ids: Vec<i32> = self.hotkeys.lock().unwrap().iter().copied().collect();
        for id in ids {
            self.unregister_hotkey(id);
        }
        if instances().lock().unwrap().remove(&self.hwnd).is_some() {
            unsafe {
                let _ = DestroyWindow(self.hwnd());
            }
        }
    }
}
