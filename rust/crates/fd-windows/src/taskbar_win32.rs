//! The real taskbar: documented AppBar messages and reversible window regions on Explorer taskbars. Every change is read back.

use std::ffi::c_void;
use std::time::Duration;

use fd_core::taskbar::{TaskbarSystem, TaskbarWindow, MAIN_CLASS, MAX_REGION_BYTES, MAX_WINDOWS, SECONDARY_CLASS};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{HWND, RECT};
use windows::Win32::Graphics::Gdi::{
    CreateRectRgn, DeleteObject, ExtCreateRegion, GetRegionData, GetWindowRgn, HRGN, MonitorFromWindow, RGNDATA, HGDIOBJ,
    MONITOR_DEFAULTTONEAREST, SetWindowRgn,
};
use windows::Win32::UI::Shell::{SHAppBarMessage, ABM_GETSTATE, ABM_SETSTATE, APPBARDATA};
use windows::Win32::UI::WindowsAndMessaging::{FindWindowExW, FindWindowW, ShowWindow, SW_HIDE, SW_SHOWNA};

use crate::native::{self, wide};

pub struct Win32TaskbarSystem;

fn main_taskbar() -> HWND {
    let class = wide(MAIN_CLASS);
    unsafe { FindWindowW(PCWSTR(class.as_ptr()), PCWSTR::null()).unwrap_or_default() }
}

fn describe(h: HWND, class: &str) -> TaskbarWindow {
    let monitor = unsafe { MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) };
    let name = if monitor.0.is_null() { String::new() } else { crate::display::describe(monitor).map(|m| m.name).unwrap_or_default() };
    TaskbarWindow { handle: native::hwnd_id(h), class_name: class.to_string(), monitor_id: name, visible: native::is_visible(h) }
}

fn appbar_data(main: HWND, l_param: isize) -> APPBARDATA {
    APPBARDATA {
        cbSize: std::mem::size_of::<APPBARDATA>() as u32,
        hWnd: main,
        lParam: windows::Win32::Foundation::LPARAM(l_param),
        ..Default::default()
    }
}

fn window_region(_h: HWND) -> Option<HRGN> {
    unsafe {
        let r = CreateRectRgn(0, 0, 0, 0);
        (!r.0.is_null()).then_some(r)
    }
}

impl TaskbarSystem for Win32TaskbarSystem {
    fn try_get_app_bar_state(&self) -> Option<i32> {
        let main = main_taskbar();
        if main.0.is_null() || !native::is_window(main) {
            return None;
        }
        let mut abd = appbar_data(main, 0);
        Some((unsafe { SHAppBarMessage(ABM_GETSTATE, &mut abd) } as i64 & 0xF) as i32)
    }

    fn try_set_app_bar_state(&self, flags: i32) -> bool {
        let main = main_taskbar();
        if main.0.is_null() || !native::is_window(main) {
            return false;
        }
        let mut abd = appbar_data(main, (flags & 0x3) as isize);
        unsafe { SHAppBarMessage(ABM_SETSTATE, &mut abd) }; // no meaningful return value: verify by reading back
        for _ in 0..10 {
            if self.try_get_app_bar_state().is_some_and(|now| now & 0x3 == flags & 0x3) {
                return true;
            }
            std::thread::sleep(Duration::from_millis(30));
        }
        false
    }

    fn taskbar_windows(&self) -> Vec<TaskbarWindow> {
        let mut list = Vec::new();
        let main = main_taskbar();
        if !main.0.is_null() {
            list.push(describe(main, MAIN_CLASS));
        }
        let class = wide(SECONDARY_CLASS);
        let mut h = HWND::default();
        while list.len() < MAX_WINDOWS {
            h = unsafe { FindWindowExW(None, Some(h), PCWSTR(class.as_ptr()), PCWSTR::null()).unwrap_or_default() };
            if h.0.is_null() {
                break;
            }
            list.push(describe(h, SECONDARY_CLASS));
        }
        list
    }

    fn is_window(&self, handle: i64) -> bool {
        native::is_window(native::hwnd(handle))
    }

    fn is_clipped_empty(&self, handle: i64) -> bool {
        let Some(region) = window_region(native::hwnd(handle)) else { return false };
        unsafe {
            let r = GetWindowRgn(native::hwnd(handle), region);
            let _ = DeleteObject(HGDIOBJ(region.0));
            r.0 == 1 // NULLREGION
        }
    }

    fn try_get_region(&self, handle: i64) -> Option<Option<Vec<u8>>> {
        let h = native::hwnd(handle);
        if !native::is_window(h) {
            return None;
        }
        let region = window_region(h)?;
        unsafe {
            let result = (|| {
                // ERROR on a valid HWND is the documented representation of "no window region".
                if GetWindowRgn(h, region).0 == 0 {
                    return native::is_window(h).then_some(None);
                }
                let size = GetRegionData(region, 0, None) as usize;
                if size < 32 || size > MAX_REGION_BYTES {
                    return None;
                }
                let mut data = vec![0u8; size];
                (GetRegionData(region, size as u32, Some(data.as_mut_ptr() as *mut RGNDATA)) as usize == size).then_some(Some(data))
            })();
            let _ = DeleteObject(HGDIOBJ(region.0));
            result
        }
    }

    fn try_clip_empty(&self, handle: i64) -> bool {
        let h = native::hwnd(handle);
        if !native::is_window(h) {
            return false;
        }
        if self.is_clipped_empty(handle) {
            return true;
        }
        unsafe {
            let region = CreateRectRgn(0, 0, 0, 0);
            if region.0.is_null() {
                return false;
            }
            if SetWindowRgn(h, Some(region), true) == 0 {
                let _ = DeleteObject(HGDIOBJ(region.0));
                return false;
            }
        }
        // On success the window owns the region. Only query it through a separate region copy.
        self.is_clipped_empty(handle)
    }

    fn try_set_region(&self, handle: i64, data: Option<&[u8]>) -> bool {
        let h = native::hwnd(handle);
        if !native::is_window(h) || data.is_some_and(|b| b.len() < 32 || b.len() > MAX_REGION_BYTES) {
            return false;
        }
        unsafe {
            let region = match data {
                None => HRGN::default(),
                Some(d) => {
                    let r = ExtCreateRegion(None, d.len() as u32, d.as_ptr() as *const RGNDATA);
                    if r.0.is_null() {
                        return false;
                    }
                    r
                }
            };
            let region_arg = if region.0.is_null() { None } else { Some(region) };
            if SetWindowRgn(h, region_arg, true) == 0 {
                if !region.0.is_null() {
                    let _ = DeleteObject(HGDIOBJ(region.0));
                }
                return false;
            }
        }
        match (self.try_get_region(handle), data) {
            (Some(None), None) => true,
            (Some(Some(after)), Some(want)) => after == want,
            _ => false,
        }
    }

    fn try_set_visible(&self, handle: i64, visible: bool) -> bool {
        let h = native::hwnd(handle);
        if !native::is_window(h) {
            return false;
        }
        unsafe {
            let _ = ShowWindow(h, if visible { SW_SHOWNA } else { SW_HIDE }); // the return value is the PREVIOUS state, not success
        }
        for _ in 0..10 {
            if native::is_visible(h) == visible {
                return true;
            }
            std::thread::sleep(Duration::from_millis(30));
        }
        false
    }
}

#[allow(dead_code)]
fn _unused(_: RECT, _: *const c_void) {}
