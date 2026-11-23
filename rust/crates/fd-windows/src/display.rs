//! Monitors (with effective DPI) and the "does a window cover this monitor" rules the dock and the top bar need.

use std::ffi::c_void;

use fd_core::win_path;
use windows::core::BOOL;
use windows::Win32::Foundation::{HWND, LPARAM, POINT, RECT};
use windows::Win32::Graphics::Gdi::{
    EnumDisplayMonitors, GetMonitorInfoW, MonitorFromPoint, MonitorFromWindow, HDC, HMONITOR, MONITORINFO, MONITORINFOEXW,
    MONITOR_DEFAULTTONEAREST,
};
use windows::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI};
use windows::Win32::UI::Shell::{SHQueryUserNotificationState, QUNS_PRESENTATION_MODE, QUNS_RUNNING_D3D_FULL_SCREEN};
use windows::Win32::UI::WindowsAndMessaging::{
    GetCursorPos, IsZoomed, SetWindowPos, SWP_NOACTIVATE, SWP_NOOWNERZORDER, SWP_NOZORDER, WS_CAPTION,
};

use crate::native;

#[derive(Debug, Clone)]
pub struct Monitor {
    pub handle: isize,
    pub name: String,
    pub bounds: RECT,
    pub work: RECT,
    pub primary: bool,
    pub scale: f64,
}

impl Monitor {
    pub fn hmonitor(&self) -> HMONITOR {
        HMONITOR(self.handle as *mut c_void)
    }

    pub fn width(&self) -> i32 {
        self.bounds.right - self.bounds.left
    }

    pub fn height(&self) -> i32 {
        self.bounds.bottom - self.bounds.top
    }
}

pub fn describe(h: HMONITOR) -> Option<Monitor> {
    unsafe {
        let mut mi = MONITORINFOEXW::default();
        mi.monitorInfo.cbSize = std::mem::size_of::<MONITORINFOEXW>() as u32;
        if !GetMonitorInfoW(h, &mut mi as *mut _ as *mut MONITORINFO).as_bool() {
            return None;
        }
        let (mut dx, mut dy) = (0u32, 0u32);
        let scale = if GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, &mut dx, &mut dy).is_ok() && dx > 0 { dx as f64 / 96.0 } else { 1.0 };
        let len = mi.szDevice.iter().position(|&c| c == 0).unwrap_or(mi.szDevice.len());
        Some(Monitor {
            handle: h.0 as isize,
            name: String::from_utf16_lossy(&mi.szDevice[..len]),
            bounds: mi.monitorInfo.rcMonitor,
            work: mi.monitorInfo.rcWork,
            primary: mi.monitorInfo.dwFlags & 1 != 0,
            scale,
        })
    }
}

pub fn monitors() -> Vec<Monitor> {
    unsafe extern "system" fn cb(h: HMONITOR, _dc: HDC, _r: *mut RECT, l: LPARAM) -> BOOL {
        let list = &mut *(l.0 as *mut Vec<Monitor>);
        if let Some(m) = describe(h) {
            list.push(m);
        }
        BOOL(1)
    }
    let mut list: Vec<Monitor> = Vec::new();
    unsafe {
        let _ = EnumDisplayMonitors(None, None, Some(cb), LPARAM(&mut list as *mut _ as isize));
    }
    list.sort_by_key(|m| (!m.primary, m.bounds.left, m.bounds.top));
    list
}

pub fn primary() -> Option<Monitor> {
    let all = monitors();
    all.iter().find(|m| m.primary).cloned().or_else(|| all.first().cloned())
}

pub fn cursor_position() -> Option<(i32, i32)> {
    let mut p = POINT::default();
    unsafe { GetCursorPos(&mut p).ok().map(|_| (p.x, p.y)) }
}

pub fn monitor_from_cursor() -> Option<Monitor> {
    let (x, y) = cursor_position()?;
    let h = unsafe { MonitorFromPoint(POINT { x, y }, MONITOR_DEFAULTTONEAREST) };
    if h.0.is_null() { primary() } else { describe(h) }
}

// ------------------------------------------------------------------------------------------------ fullscreen

fn is_desktop_class(class: &str) -> bool {
    matches!(class, "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd")
}

/// A normal framed maximized window extends its resize borders beyond the monitor; a captioned window counts as full screen
/// only when its outer bounds match the screen.
pub fn is_window_fullscreen_on(window: HWND, monitor: &Monitor, own_pid: u32) -> bool {
    if window.0.is_null() || !native::is_visible(window) || native::is_iconic(window) {
        return false;
    }
    if native::pid_of(window) == own_pid || is_desktop_class(&native::class_name(window)) {
        return false;
    }
    if unsafe { MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST) }.0 as isize != monitor.handle {
        return false;
    }
    let Some(r) = native::window_rect(window) else { return false };
    let m = monitor.bounds;
    if !(r.left <= m.left && r.top <= m.top && r.right >= m.right && r.bottom >= m.bottom) {
        return false;
    }
    let captioned = native::style(window) & WS_CAPTION.0 as i64 == WS_CAPTION.0 as i64;
    if captioned && ((r.left - m.left).abs() > 1 || (r.top - m.top).abs() > 1 || (r.right - m.right).abs() > 1 || (r.bottom - m.bottom).abs() > 1) {
        return false;
    }
    true
}

/// Includes ordinary maximized apps covering the monitor's work area, where the dock also lives.
pub fn is_window_maximized_on(window: HWND, monitor: &Monitor, own_pid: u32) -> bool {
    if window.0.is_null() || !native::is_visible(window) || native::is_iconic(window) || !unsafe { IsZoomed(window).as_bool() } {
        return false;
    }
    if native::pid_of(window) == own_pid || is_desktop_class(&native::class_name(window)) {
        return false;
    }
    if unsafe { MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST) }.0 as isize != monitor.handle {
        return false;
    }
    let Some(r) = native::window_rect(window) else { return false };
    let w = monitor.work;
    r.left <= w.left && r.top <= w.top && r.right >= w.right && r.bottom >= w.bottom
}

/// Moves a covering window onto `area` (the monitor work area, which excludes the reserved dock strip). Only acts when the
/// window actually extends past the area; no activation or z-order change.
pub fn fit_to_area(window: HWND, area: RECT) -> bool {
    let Some(r) = native::window_rect(window) else { return false };
    if r.left >= area.left && r.top >= area.top && r.right <= area.right && r.bottom <= area.bottom {
        return false;
    }
    unsafe {
        SetWindowPos(window, None, area.left, area.top, area.right - area.left, area.bottom - area.top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER).is_ok()
    }
}

const VIDEO_HOSTS: [&str; 33] = [
    "chrome", "msedge", "firefox", "zen", "brave", "opera", "opera_gx", "vivaldi", "browser", "arc", "librewolf", "waterfox", "chromium",
    "yandex", "vlc", "mpv", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayermini", "potplayermini64", "wmplayer", "video.ui",
    "microsoft.media.player", "kmplayer", "smplayer", "celluloid", "stremio", "plex", "netflix", "spotify", "helium",
];

/// True when the window belongs to a browser or media player, where full screen means a video.
pub fn is_video_host(window: HWND) -> bool {
    if window.0.is_null() {
        return false;
    }
    let Some(exe) = native::exe_path(native::pid_of(window)) else { return false };
    let name = win_path::file_name_without_extension(&exe).to_lowercase();
    VIDEO_HOSTS.contains(&name.as_str())
}

/// Windows' own opinion (D3D fullscreen game / presentation mode). Any failure of the API means "unknown" = false.
pub fn windows_reports_fullscreen() -> bool {
    unsafe { SHQueryUserNotificationState().map(|s| s == QUNS_RUNNING_D3D_FULL_SCREEN || s == QUNS_PRESENTATION_MODE).unwrap_or(false) }
}
