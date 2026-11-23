//! Thin, safe wrappers over the Win32 window queries the engine needs everywhere.

use std::ffi::c_void;

use windows::core::{BOOL, PCWSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM, RECT};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED};
use windows::Win32::Storage::Packaging::Appx::GetApplicationUserModelId;
use windows::Win32::System::Threading::{
    OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumChildWindows, EnumWindows, GetAncestor, GetClassNameW, GetForegroundWindow, GetWindow, GetWindowLongPtrW,
    GetWindowRect, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsIconic, IsWindow, IsWindowVisible,
    GA_ROOTOWNER, GWL_EXSTYLE, GWL_STYLE, GW_OWNER,
};

pub fn hwnd(id: i64) -> HWND {
    HWND(id as isize as *mut c_void)
}

pub fn hwnd_id(h: HWND) -> i64 {
    h.0 as isize as i64
}

pub fn is_window(h: HWND) -> bool {
    unsafe { IsWindow(Some(h)).as_bool() }
}

pub fn is_visible(h: HWND) -> bool {
    unsafe { IsWindowVisible(h).as_bool() }
}

pub fn is_iconic(h: HWND) -> bool {
    unsafe { IsIconic(h).as_bool() }
}

pub fn foreground() -> HWND {
    unsafe { GetForegroundWindow() }
}

pub fn class_name(h: HWND) -> String {
    let mut buf = [0u16; 256];
    let n = unsafe { GetClassNameW(h, &mut buf) };
    String::from_utf16_lossy(&buf[..n.max(0) as usize])
}

pub fn title(h: HWND) -> String {
    unsafe {
        let len = GetWindowTextLengthW(h);
        if len <= 0 {
            return String::new();
        }
        let mut buf = vec![0u16; len as usize + 1];
        let n = GetWindowTextW(h, &mut buf);
        String::from_utf16_lossy(&buf[..n.max(0) as usize])
    }
}

pub fn style(h: HWND) -> i64 {
    unsafe { GetWindowLongPtrW(h, GWL_STYLE) as i64 }
}

pub fn ex_style(h: HWND) -> i64 {
    unsafe { GetWindowLongPtrW(h, GWL_EXSTYLE) as i64 }
}

pub fn has_owner(h: HWND) -> bool {
    unsafe { GetWindow(h, GW_OWNER).map(|o| !o.0.is_null()).unwrap_or(false) }
}

pub fn window_rect(h: HWND) -> Option<RECT> {
    let mut r = RECT::default();
    unsafe { GetWindowRect(h, &mut r).ok().map(|_| r) }
}

pub fn thread_and_pid(h: HWND) -> (u32, u32) {
    let mut pid = 0u32;
    let thread = unsafe { GetWindowThreadProcessId(h, Some(&mut pid)) };
    (thread, pid)
}

pub fn pid_of(h: HWND) -> u32 {
    thread_and_pid(h).1
}

pub fn is_cloaked(h: HWND) -> bool {
    let mut cloaked: u32 = 0;
    unsafe {
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, &mut cloaked as *mut u32 as *mut c_void, std::mem::size_of::<u32>() as u32)
            .is_ok()
            && cloaked != 0
    }
}

/// The root owner of a window (the window a dialog belongs to); the window itself when it has none.
pub fn root_owner(h: HWND) -> HWND {
    let r = unsafe { GetAncestor(h, GA_ROOTOWNER) };
    if r.0.is_null() { h } else { r }
}

pub fn exe_path(pid: u32) -> Option<String> {
    if pid == 0 {
        return None;
    }
    unsafe {
        let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = [0u16; 1024];
        let mut size = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(h, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut size).is_ok();
        let _ = CloseHandle(h);
        ok.then(|| String::from_utf16_lossy(&buf[..size as usize]))
    }
}

/// The AppUserModelID of a packaged process; `None` for classic desktop processes.
pub fn process_aumid(pid: u32) -> Option<String> {
    unsafe {
        let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut len = 0u32;
        // ERROR_INSUFFICIENT_BUFFER (122) when the process is packaged
        let rc = GetApplicationUserModelId(h, &mut len, None);
        let result = if rc.0 == 122 && len > 0 {
            let mut buf = vec![0u16; len as usize];
            let rc = GetApplicationUserModelId(h, &mut len, Some(PWSTR(buf.as_mut_ptr())));
            (rc.0 == 0).then(|| String::from_utf16_lossy(&buf[..(len as usize).saturating_sub(1)]))
        } else {
            None
        };
        let _ = CloseHandle(h);
        result
    }
}

/// Calls `f` for every top-level window (top of the z-order first) until it returns false.
pub fn enum_windows(mut f: impl FnMut(HWND) -> bool) {
    unsafe extern "system" fn cb(h: HWND, l: LPARAM) -> BOOL {
        let f = &mut *(l.0 as *mut &mut dyn FnMut(HWND) -> bool);
        BOOL::from(f(h))
    }
    let mut dynf: &mut dyn FnMut(HWND) -> bool = &mut f;
    unsafe {
        let _ = EnumWindows(Some(cb), LPARAM(&mut dynf as *mut _ as isize));
    }
}

pub fn enum_children(parent: HWND, mut f: impl FnMut(HWND) -> bool) {
    unsafe extern "system" fn cb(h: HWND, l: LPARAM) -> BOOL {
        let f = &mut *(l.0 as *mut &mut dyn FnMut(HWND) -> bool);
        BOOL::from(f(h))
    }
    let mut dynf: &mut dyn FnMut(HWND) -> bool = &mut f;
    unsafe {
        let _ = EnumChildWindows(Some(parent), Some(cb), LPARAM(&mut dynf as *mut _ as isize));
    }
}

pub fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

pub fn pcwstr(buf: &[u16]) -> PCWSTR {
    PCWSTR(buf.as_ptr())
}
