//! Where the minimize / restore animations of Windows fly to. The shell asks (HSHELL_GETMINRECT) with a small payload that lives in
//! this process; the answer is written into it. A hidden window on its own thread answers synchronously, so it never waits for the
//! UI loop. Only the shell's local, writable payload is touched: no foreign-process memory, no hooks.

use std::ffi::c_void;
use std::sync::mpsc;
use std::sync::{Arc, Mutex, OnceLock};

use windows::core::PCWSTR;
use windows::Win32::Foundation::{HANDLE, HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows::Win32::System::Diagnostics::Debug::{ReadProcessMemory, WriteProcessMemory};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::GetCurrentProcess;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetMessageW, GetSystemMetrics, PostMessageW, PostQuitMessage,
    RegisterClassExW, RegisterShellHookWindow, RegisterWindowMessageW, TranslateMessage, UnregisterClassW, DeregisterShellHookWindow, MSG,
    WNDCLASSEXW, WM_CLOSE, WS_EX_TOOLWINDOW, WS_POPUP,
};

use crate::native::{self, wide};

type Provider = Arc<dyn Fn(HWND) -> Option<RECT> + Send + Sync>;

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct Info {
    window: isize,
    rect: RECT,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct Header {
    window: isize,
    first: i32,
    second: i32,
}

fn in_i16(v: i32) -> bool {
    (i16::MIN as i32..=i16::MAX as i32).contains(&v)
}

/// Reads the GETMINRECT payload at `payload` and writes the rectangle `provider` gives for its window. True when it was written.
pub fn try_update(payload: isize, provider: &dyn Fn(HWND) -> Option<RECT>) -> bool {
    if payload == 0 {
        return false;
    }
    unsafe {
        let process: HANDLE = GetCurrentProcess();
        let mut header = Header::default();
        let mut read = 0usize;
        let header_len = std::mem::size_of::<Header>();
        if ReadProcessMemory(process, payload as *const c_void, &mut header as *mut _ as *mut c_void, header_len, Some(&mut read)).is_err()
            || read != header_len
            || header.window == 0
        {
            return false;
        }
        let mut info = Info::default();
        let len = std::mem::size_of::<Info>();
        let full_read = ReadProcessMemory(process, payload as *const c_void, &mut info as *mut _ as *mut c_void, len, Some(&mut read)).is_ok() && read == len;
        if !full_read {
            info = Info { window: header.window, rect: RECT { left: header.first, top: header.second, right: 0, bottom: 0 } };
        }
        let Some(mut rect) = provider(HWND(info.window as *mut c_void)) else { return false };
        if rect.right - rect.left <= 0 || rect.bottom - rect.top <= 0 {
            return false;
        }
        let mut rect_len = std::mem::size_of::<RECT>();
        // Packed POINTS occupy only two LONGs. Trailing storage is not part of that payload and is not guaranteed to contain zeroes.
        let (packed_left, packed_top) = (info.rect.left as i16 as i32, (info.rect.left >> 16) as i16 as i32);
        let (packed_right, packed_bottom) = (info.rect.top as i16 as i32, (info.rect.top >> 16) as i16 as i32);
        let packed_shape = packed_right > packed_left && packed_bottom > packed_top;
        if !full_read && !packed_shape {
            return false;
        }
        let full_shape = info.rect.right > info.rect.left && info.rect.bottom > info.rect.top;
        let encoded = !in_i16(info.rect.left) || !in_i16(info.rect.top);
        let mut full_on_desktop = false;
        if full_shape && packed_shape && encoded {
            let (x, y) = (GetSystemMetrics(windows::Win32::UI::WindowsAndMessaging::SM_XVIRTUALSCREEN), GetSystemMetrics(windows::Win32::UI::WindowsAndMessaging::SM_YVIRTUALSCREEN));
            let (w, h) = (GetSystemMetrics(windows::Win32::UI::WindowsAndMessaging::SM_CXVIRTUALSCREEN), GetSystemMetrics(windows::Win32::UI::WindowsAndMessaging::SM_CYVIRTUALSCREEN));
            full_on_desktop = info.rect.left >= x && info.rect.top >= y && info.rect.right <= x + w && info.rect.bottom <= y + h;
        }
        if (packed_shape && (!full_shape || (encoded && !full_on_desktop))) || (info.rect.right == 0 && info.rect.bottom == 0 && (info.rect.left != 0 || info.rect.top != 0)) {
            if !(in_i16(rect.left) && in_i16(rect.top) && in_i16(rect.right) && in_i16(rect.bottom)) {
                return false;
            }
            rect = RECT {
                left: (rect.left & 0xffff) | ((rect.top & 0xffff) << 16),
                top: (rect.right & 0xffff) | ((rect.bottom & 0xffff) << 16),
                right: 0,
                bottom: 0,
            };
            rect_len = 2 * std::mem::size_of::<i32>(); // do not touch padding beyond the two POINTS
        }
        let mut written = 0usize;
        WriteProcessMemory(process, (payload + std::mem::size_of::<isize>() as isize) as *mut c_void, &rect as *const _ as *const c_void, rect_len, Some(&mut written)).is_ok()
            && written == rect_len
    }
}

// ------------------------------------------------------------------------------------------------ the window

fn slot() -> &'static Mutex<Option<Provider>> {
    static S: OnceLock<Mutex<Option<Provider>>> = OnceLock::new();
    S.get_or_init(|| Mutex::new(None))
}

fn shell_message() -> u32 {
    static M: OnceLock<u32> = OnceLock::new();
    *M.get_or_init(|| unsafe { RegisterWindowMessageW(windows::core::w!("SHELLHOOK")) })
}

fn taskbar_message() -> u32 {
    static M: OnceLock<u32> = OnceLock::new();
    *M.get_or_init(|| unsafe { RegisterWindowMessageW(windows::core::w!("TaskbarCreated")) })
}

unsafe extern "system" fn proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    let outcome = std::panic::catch_unwind(|| {
        if msg == shell_message() && wparam.0 == 5 {
            // HSHELL_GETMINRECT
            let provider = slot().lock().ok().and_then(|s| s.clone());
            let updated = provider.is_some_and(|p| try_update(lparam.0, &*p));
            return Some(LRESULT(updated as isize));
        }
        if msg == taskbar_message() {
            // Explorer restarted: the shell hook has to be registered again.
            let _ = DeregisterShellHookWindow(hwnd);
            let _ = RegisterShellHookWindow(hwnd);
            return Some(LRESULT(0));
        }
        if msg == WM_CLOSE {
            PostQuitMessage(0);
            return Some(LRESULT(0));
        }
        None
    });
    match outcome {
        Ok(Some(r)) => r,
        Ok(None) => DefWindowProcW(hwnd, msg, wparam, lparam),
        Err(_) => LRESULT(0),
    }
}

pub struct ShellAnimationWindow {
    hwnd: isize,
    registered: bool,
    thread: Mutex<Option<std::thread::JoinHandle<()>>>,
}

impl ShellAnimationWindow {
    pub fn start(provider: impl Fn(HWND) -> Option<RECT> + Send + Sync + 'static) -> Result<ShellAnimationWindow, String> {
        *slot().lock().unwrap() = Some(Arc::new(provider));
        let (tx, rx) = mpsc::channel::<Result<(isize, bool), String>>();
        let thread = std::thread::Builder::new()
            .name("FloatingDock shell animation".into())
            .spawn(move || unsafe {
                let class = wide(&format!("FloatingDock.ShellAnimation.{}", std::process::id()));
                let inst = match GetModuleHandleW(None) {
                    Ok(i) => i,
                    Err(e) => {
                        let _ = tx.send(Err(e.to_string()));
                        return;
                    }
                };
                let wc = WNDCLASSEXW {
                    cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
                    lpfnWndProc: Some(proc),
                    hInstance: inst.into(),
                    lpszClassName: PCWSTR(class.as_ptr()),
                    ..Default::default()
                };
                if RegisterClassExW(&wc) == 0 {
                    let _ = tx.send(Err("Shell window registration failed.".into()));
                    return;
                }
                let title = wide("FloatingDock shell animation");
                let hwnd = match CreateWindowExW(WS_EX_TOOLWINDOW, PCWSTR(class.as_ptr()), PCWSTR(title.as_ptr()), WS_POPUP, 0, 0, 0, 0, None, None, Some(inst.into()), None) {
                    Ok(h) => h,
                    Err(e) => {
                        let _ = tx.send(Err(format!("Shell window creation failed: {e}")));
                        return;
                    }
                };
                let _ = shell_message();
                let _ = taskbar_message();
                let registered = RegisterShellHookWindow(hwnd).as_bool();
                let _ = tx.send(Ok((hwnd.0 as isize, registered)));
                let mut msg = MSG::default();
                while GetMessageW(&mut msg, None, 0, 0).as_bool() {
                    let _ = TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
                if registered {
                    let _ = DeregisterShellHookWindow(hwnd);
                }
                let _ = DestroyWindow(hwnd);
                let _ = UnregisterClassW(PCWSTR(class.as_ptr()), Some(inst.into()));
            })
            .map_err(|e| e.to_string())?;
        let (hwnd, registered) = rx.recv().map_err(|_| "The shell animation thread stopped.".to_string())??;
        Ok(ShellAnimationWindow { hwnd, registered, thread: Mutex::new(Some(thread)) })
    }

    pub fn registered(&self) -> bool {
        self.registered
    }

    pub fn dispose(&self) {
        unsafe {
            let _ = PostMessageW(Some(HWND(self.hwnd as *mut c_void)), WM_CLOSE, WPARAM(0), LPARAM(0));
        }
        if let Some(t) = self.thread.lock().unwrap().take() {
            let _ = t.join();
        }
        *slot().lock().unwrap() = None;
    }
}

#[allow(dead_code)]
fn _keep(_: fn(HWND) -> bool) {
    let _ = native::is_window;
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A GETMINRECT payload: the window handle, then the rectangle the shell wants filled in.
    #[test]
    fn writes_the_full_rectangle_into_a_full_payload() {
        let mut payload = Info { window: 0x1234, rect: RECT::default() };
        let target = RECT { left: 100, top: 900, right: 164, bottom: 964 };
        let ok = try_update(&mut payload as *mut Info as isize, &|h| (h.0 as isize == 0x1234).then_some(target));
        assert!(ok);
        assert_eq!((payload.rect.left, payload.rect.top, payload.rect.right, payload.rect.bottom), (100, 900, 164, 964));
        assert!(!try_update(0, &|_| Some(target)));
        let mut other = Info { window: 0, rect: RECT::default() };
        assert!(!try_update(&mut other as *mut Info as isize, &|_| Some(target)), "no window in the payload");
        let mut empty = Info { window: 5, rect: RECT::default() };
        assert!(!try_update(&mut empty as *mut Info as isize, &|_| None));
    }
}
