//! Maps an HWND to its process, executable path and AppUserModelID - including windows hosted by ApplicationFrameHost
//! (Settings, UWP).

use fd_core::{AppMatching, WindowInfo};
use windows::Win32::Foundation::HWND;

use crate::{native, shell};

pub struct Resolved {
    pub info: WindowInfo,
    pub complete: bool,
}

/// Early tiling needs only native ownership/exclusions, without waiting for Shell/property-store work.
pub fn resolve_basic(hwnd: HWND) -> WindowInfo {
    let pid = native::pid_of(hwnd);
    WindowInfo {
        hwnd: native::hwnd_id(hwnd),
        process_id: pid as i32,
        exe_path: native::exe_path(pid),
        aumid: None,
        title: Some(native::title(hwnd)),
        class_name: Some(native::class_name(hwnd)),
        is_minimized: native::is_iconic(hwnd),
    }
}

/// Must run on an STA thread (property store).
pub fn resolve(hwnd: HWND) -> Resolved {
    let mut pid = native::pid_of(hwnd);
    let class = native::class_name(hwnd);
    let title = native::title(hwnd);
    let mut exe = native::exe_path(pid);
    let mut aumid = shell::read_aumid_of_window(hwnd);
    let is_frame = AppMatching::is_host_exe(exe.as_deref()) || class.eq_ignore_ascii_case("ApplicationFrameWindow");

    if is_frame {
        // the real application lives in a child CoreWindow that belongs to another process (ApplicationFrameHost only draws the frame)
        if let Some((child_pid, child_exe, child_aumid)) = find_hosted_app(hwnd, pid) {
            pid = child_pid;
            if child_exe.is_some() {
                exe = child_exe; // the hosted app's executable replaces the host's
            }
            if aumid.is_none() {
                aumid = child_aumid;
            }
        }
    } else if aumid.is_none() {
        // packaged desktop apps expose their AUMID through the process even if the window has none
        if let Some(a) = native::process_aumid(pid).filter(|a| a.contains('!')) {
            aumid = Some(a);
        }
    }
    // complete = a real executable, or (for hosted windows) an AUMID. A child PID without either is NOT an identity.
    let complete = AppMatching::is_identity_complete(exe.as_deref(), aumid.as_deref(), is_frame);
    Resolved {
        info: WindowInfo {
            hwnd: native::hwnd_id(hwnd),
            process_id: pid as i32,
            exe_path: exe,
            aumid,
            title: Some(title),
            class_name: Some(class),
            is_minimized: native::is_iconic(hwnd),
        },
        complete,
    }
}

fn find_hosted_app(frame: HWND, frame_pid: u32) -> Option<(u32, Option<String>, Option<String>)> {
    let mut found = 0u32;
    native::enum_children(frame, |child| {
        let cpid = native::pid_of(child);
        if cpid != 0 && cpid != frame_pid && found == 0 && native::class_name(child) == "Windows.UI.Core.CoreWindow" {
            found = cpid;
            return false;
        }
        true
    });
    (found != 0).then(|| (found, native::exe_path(found), native::process_aumid(found)))
}
