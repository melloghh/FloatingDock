//! The notification-area icons of Explorer, read through UI Automation (Explorer owns all icon callbacks): the live buttons give
//! the names and the order; the registry (`NotifyIconSettings`) supplies their pictures. Reading never changes any window.
//! Activating an icon invokes its live button (a left click). Run on a COM thread (the shell worker).
//!
//! Not ported yet: opening Explorer's hidden-icons flyout and the right-click context menu of an icon; both replace parts of
//! Explorer's own windows (opacity leases, repositioning) and need a careful test of their own.

use std::ffi::c_void;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use fd_core::win_path;
use windows::core::PCWSTR;
use windows::Win32::Foundation::HWND;
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};
use windows::Win32::System::Ole::{SafeArrayAccessData, SafeArrayDestroy, SafeArrayGetLBound, SafeArrayGetUBound, SafeArrayUnaccessData};
use windows::Win32::System::Registry::{
    RegCloseKey, RegEnumKeyExW, RegGetValueW, RegOpenKeyExW, HKEY, HKEY_CURRENT_USER, KEY_READ, RRF_RT_REG_BINARY, RRF_RT_REG_SZ,
};
use windows::Win32::UI::Accessibility::{
    CUIAutomation, IUIAutomation, IUIAutomationElement, IUIAutomationInvokePattern, IUIAutomationTreeWalker, UIA_InvokePatternId,
};
use windows::Win32::Foundation::{COLORREF, LPARAM, WPARAM};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DwmSetWindowAttribute, DWMWINDOWATTRIBUTE};
use windows::Win32::UI::WindowsAndMessaging::{
    FindWindowW, GetAncestor, GetGUIThreadInfo, GetLayeredWindowAttributes, GetWindowLongPtrW, PostMessageW, SendMessageTimeoutW,
    SetForegroundWindow, SetLayeredWindowAttributes, SetWindowLongPtrW, ShowWindow, GA_ROOT, GUITHREADINFO, GWL_EXSTYLE,
    LAYERED_WINDOW_ATTRIBUTES_FLAGS, SMTO_ABORTIFHUNG, SW_HIDE, SW_SHOWNOACTIVATE, WM_KEYDOWN, WM_KEYUP, WM_NULL,
};

use crate::native;

use crate::native::wide;

#[derive(Debug, Clone)]
pub struct TrayItem {
    pub key: String,
    pub name: String,
    pub icon: Option<Vec<u8>>,
    pub overflow: bool,
    pub executable: Option<String>,
}

struct IconSnapshot {
    name: String,
    executable: String,
    bytes: Option<Vec<u8>>,
}

fn trace(message: &str) {
    if std::env::var_os("FD_TRAY_DEBUG").is_some() {
        use std::io::Write;
        if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(std::env::temp_dir().join("fd-tray-debug.log")) {
            let _ = writeln!(f, "{message}");
        }
    }
}

fn find(class: &str) -> HWND {
    let c = wide(class);
    unsafe { FindWindowW(PCWSTR(c.as_ptr()), PCWSTR::null()).unwrap_or_default() }
}

/// The overflow flyout, when Explorer has created one.
fn overflow_handle() -> HWND {
    let modern = find("TopLevelWindowForOverflowXamlIsland");
    if modern.0.is_null() { find("NotifyIconOverflowWindow") } else { modern }
}

// ------------------------------------------------------------------------------------------------ registry

fn reg_string(key: HKEY, name: &str) -> Option<String> {
    unsafe {
        let n = wide(name);
        let mut size = 0u32;
        if RegGetValueW(key, PCWSTR::null(), PCWSTR(n.as_ptr()), RRF_RT_REG_SZ, None, None, Some(&mut size)).is_err() || size < 2 {
            return None;
        }
        let mut buf = vec![0u16; (size as usize).div_ceil(2)];
        RegGetValueW(key, PCWSTR::null(), PCWSTR(n.as_ptr()), RRF_RT_REG_SZ, None, Some(buf.as_mut_ptr() as *mut c_void), Some(&mut size)).ok().ok()?;
        let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        Some(String::from_utf16_lossy(&buf[..end]))
    }
}

fn reg_binary(key: HKEY, name: &str) -> Option<Vec<u8>> {
    unsafe {
        let n = wide(name);
        let mut size = 0u32;
        if RegGetValueW(key, PCWSTR::null(), PCWSTR(n.as_ptr()), RRF_RT_REG_BINARY, None, None, Some(&mut size)).is_err() || size == 0 {
            return None;
        }
        let mut buf = vec![0u8; size as usize];
        RegGetValueW(key, PCWSTR::null(), PCWSTR(n.as_ptr()), RRF_RT_REG_BINARY, None, Some(buf.as_mut_ptr() as *mut c_void), Some(&mut size)).ok().ok()?;
        buf.truncate(size as usize);
        Some(buf)
    }
}

fn icon_snapshots() -> Vec<IconSnapshot> {
    let mut result = Vec::new();
    unsafe {
        let path = wide("Control Panel\\NotifyIconSettings");
        let mut root = HKEY::default();
        if RegOpenKeyExW(HKEY_CURRENT_USER, PCWSTR(path.as_ptr()), None, KEY_READ, &mut root).is_err() {
            return result;
        }
        for index in 0..512u32 {
            let mut name = [0u16; 256];
            let mut len = name.len() as u32;
            if RegEnumKeyExW(root, index, Some(windows::core::PWSTR(name.as_mut_ptr())), &mut len, None, None, None, None).is_err() {
                break;
            }
            let mut entry = HKEY::default();
            let mut sub = name[..len as usize].to_vec();
            sub.push(0);
            if RegOpenKeyExW(root, PCWSTR(sub.as_ptr()), None, KEY_READ, &mut entry).is_err() {
                continue;
            }
            let tooltip = reg_string(entry, "InitialTooltip").unwrap_or_default();
            let executable = reg_string(entry, "ExecutablePath").unwrap_or_default();
            let bytes = reg_binary(entry, "IconSnapshot").filter(|b| b.len() > 8 && b.len() < 262_144);
            let _ = RegCloseKey(entry);
            if !tooltip.is_empty() || !executable.is_empty() {
                result.push(IconSnapshot { name: tooltip, executable, bytes });
            }
        }
        let _ = RegCloseKey(root);
    }
    result
}

/// `min(width, height)` of a PNG; 0 when it is none or absurd (Explorer snapshots must never be enlarged past their real size).
pub fn png_pixel_size(png: Option<&[u8]>) -> i32 {
    let Some(p) = png else { return 0 };
    if p.len() < 24 || p[..8] != [137, 80, 78, 71, 13, 10, 26, 10] {
        return 0;
    }
    let (w, h) = (i32::from_be_bytes([p[16], p[17], p[18], p[19]]), i32::from_be_bytes([p[20], p[21], p[22], p[23]]));
    if (1..=512).contains(&w) && (1..=512).contains(&h) { w.min(h) } else { 0 }
}

// ------------------------------------------------------------------------------------------------ UI Automation

const CONTROL_TYPE_BUTTON: i32 = 50000;

fn runtime_key(element: &IUIAutomationElement, id: &str, name: &str) -> String {
    unsafe {
        let Ok(array) = element.GetRuntimeId() else { return format!("{id}|{name}") };
        if array.is_null() {
            return format!("{id}|{name}");
        }
        let result = (|| {
            let (lower, upper) = (SafeArrayGetLBound(array, 1).ok()?, SafeArrayGetUBound(array, 1).ok()?);
            if !(0..=63).contains(&(upper - lower)) {
                return None;
            }
            let mut data: *mut c_void = std::ptr::null_mut();
            SafeArrayAccessData(array, &mut data).ok()?;
            let values = std::slice::from_raw_parts(data as *const i32, (upper - lower + 1) as usize).to_vec();
            let _ = SafeArrayUnaccessData(array);
            Some(format!("{id}|{}", values.iter().map(|v| v.to_string()).collect::<Vec<_>>().join(".")))
        })();
        let _ = SafeArrayDestroy(array);
        result.unwrap_or_else(|| format!("{id}|{name}"))
    }
}

struct Uia {
    automation: IUIAutomation,
    walker: IUIAutomationTreeWalker,
}

impl Uia {
    fn new() -> Option<Uia> {
        unsafe {
            let automation: IUIAutomation = CoCreateInstance(&CUIAutomation, None, CLSCTX_INPROC_SERVER).ok()?;
            let walker = automation.RawViewWalker().ok()?;
            Some(Uia { automation, walker })
        }
    }

    /// Calls `visitor(element, automation id, name)` for every button below `hwnd` (breadth is bounded) until it returns true.
    fn visit_icons(&self, hwnd: HWND, mut visitor: impl FnMut(&IUIAutomationElement, &str, &str) -> bool) -> bool {
        if hwnd.0.is_null() {
            return false;
        }
        unsafe {
            let Ok(root) = self.automation.ElementFromHandle(hwnd) else { return false };
            let mut pending: Vec<(IUIAutomationElement, u32)> = vec![(root, 0)];
            let mut count = 0;
            while let Some((element, depth)) = pending.pop() {
                count += 1;
                if count > 700 {
                    break;
                }
                if element.CurrentControlType().map(|t| t.0).unwrap_or(0) == CONTROL_TYPE_BUTTON {
                    let id = element.CurrentAutomationId().map(|b| b.to_string()).unwrap_or_default();
                    let name = element.CurrentName().map(|b| b.to_string()).unwrap_or_default();
                    if visitor(&element, &id, &name) {
                        return true;
                    }
                }
                if depth >= 18 {
                    continue;
                }
                let mut child = self.walker.GetFirstChildElement(&element).ok();
                while let Some(c) = child {
                    if pending.len() >= 700 {
                        break;
                    }
                    let next = self.walker.GetNextSiblingElement(&c).ok();
                    pending.push((c, depth + 1));
                    child = next;
                }
            }
        }
        false
    }
}

// ------------------------------------------------------------------------------------------------ opacity lease

const WS_EX_LAYERED: isize = 0x80000;
const DWMWA_TRANSITIONS_FORCEDISABLED: DWMWINDOWATTRIBUTE = DWMWINDOWATTRIBUTE(3);

/// A reversible "invisible" state of a foreign window: alpha 0 on a layered window plus no DWM transitions. DWMWA_CLOAK cannot be
/// set on Explorer's HWND (access denied); an alpha lease works. Everything the lease changed is put back when it is dropped.
struct OpacityLease {
    window: i64,
    pid: u32,
    key: u32,
    alpha: u8,
    flags: u32,
    added_layer: bool,
    transitions: i32,
    armed: bool,
}

fn responsive(h: HWND) -> bool {
    let mut result = 0usize;
    unsafe { SendMessageTimeoutW(h, WM_NULL, WPARAM(0), LPARAM(0), SMTO_ABORTIFHUNG, 25, Some(&mut result)).0 != 0 }
}

impl OpacityLease {
    fn try_acquire(h: HWND) -> Option<OpacityLease> {
        if !native::is_window(h) || !responsive(h) {
            return None;
        }
        let pid = native::pid_of(h);
        unsafe {
            let style = GetWindowLongPtrW(h, GWL_EXSTYLE);
            let added = style & WS_EX_LAYERED == 0;
            let (mut key, mut alpha, mut flags) = (COLORREF(0), 255u8, 2u32);
            if !added {
                // UpdateLayeredWindow and colour-key owners retain their own rendering policy.
                let mut f = LAYERED_WINDOW_ATTRIBUTES_FLAGS(0);
                if GetLayeredWindowAttributes(h, Some(&mut key), Some(&mut alpha), Some(&mut f)).is_err() || f.0 & 1 != 0 || alpha == 0 {
                    return None;
                }
                flags = f.0;
            }
            if added {
                SetWindowLongPtrW(h, GWL_EXSTYLE, style | WS_EX_LAYERED);
            }
            if SetLayeredWindowAttributes(h, key, 0, LAYERED_WINDOW_ATTRIBUTES_FLAGS(2)).is_err() {
                if added {
                    SetWindowLongPtrW(h, GWL_EXSTYLE, style);
                }
                return None;
            }
            let mut transitions = 0i32;
            if DwmGetWindowAttribute(h, DWMWA_TRANSITIONS_FORCEDISABLED, &mut transitions as *mut i32 as *mut c_void, 4).is_err() {
                transitions = 0;
            }
            let disabled = 1i32;
            if DwmSetWindowAttribute(h, DWMWA_TRANSITIONS_FORCEDISABLED, &disabled as *const i32 as *const c_void, 4).is_err() {
                let _ = SetLayeredWindowAttributes(h, key, alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS(flags));
                if added {
                    SetWindowLongPtrW(h, GWL_EXSTYLE, style);
                }
                return None;
            }
            Some(OpacityLease { window: native::hwnd_id(h), pid, key: key.0, alpha, flags, added_layer: added, transitions, armed: true })
        }
    }

    fn restore(&self) {
        let h = native::hwnd(self.window);
        if native::pid_of(h) != self.pid || !native::is_window(h) {
            return;
        }
        unsafe {
            // Remove only the bit that this lease introduced; preserve unrelated style changes.
            let _ = SetLayeredWindowAttributes(h, COLORREF(self.key), self.alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS(self.flags));
            if self.added_layer {
                let style = GetWindowLongPtrW(h, GWL_EXSTYLE);
                SetWindowLongPtrW(h, GWL_EXSTYLE, style & !WS_EX_LAYERED);
            }
            let _ = DwmSetWindowAttribute(h, DWMWA_TRANSITIONS_FORCEDISABLED, &self.transitions as *const i32 as *const c_void, 4);
        }
    }
}

impl Drop for OpacityLease {
    fn drop(&mut self) {
        if !self.armed {
            return;
        }
        // A hung owner must not freeze the caller during restoration.
        if responsive(native::hwnd(self.window)) {
            self.restore();
        } else {
            let copy = OpacityLease { armed: false, ..*self };
            std::thread::spawn(move || copy.restore());
        }
    }
}

/// Explorer keeps its live UI Automation controls without presenting a second tray: while an operation runs the taskbar window
/// and the flyout are invisible. Bounded to one discovery/action; the leases are given back at the end.
struct PresentationScope {
    stop: Arc<AtomicBool>,
    watcher: Option<std::thread::JoinHandle<()>>,
}

impl PresentationScope {
    fn new() -> PresentationScope {
        let leases: Arc<Mutex<Vec<OpacityLease>>> = Arc::new(Mutex::new(Vec::new()));
        let suppress = {
            let leases = leases.clone();
            move |window: HWND| {
                if window.0.is_null() {
                    return;
                }
                let id = native::hwnd_id(window);
                let mut l = leases.lock().unwrap();
                if l.iter().any(|x| x.window == id) {
                    return;
                }
                if let Some(lease) = OpacityLease::try_acquire(window) {
                    l.push(lease);
                }
            }
        };
        suppress(find("Shell_TrayWnd"));
        suppress(overflow_handle());
        let stop = Arc::new(AtomicBool::new(false));
        let stop2 = stop.clone();
        let watcher = std::thread::Builder::new()
            .name("FloatingDock.TrayPresentation".into())
            .spawn(move || {
                // Explorer can create the overflow lazily. Observe its HWND during this operation only.
                let start = Instant::now();
                while !stop2.load(Ordering::SeqCst) && start.elapsed() < Duration::from_millis(3000) {
                    suppress(overflow_handle());
                    std::thread::sleep(Duration::from_millis(12));
                }
                leases.lock().unwrap().clear(); // dropping gives every window back
            })
            .ok();
        PresentationScope { stop, watcher }
    }
}

impl Drop for PresentationScope {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        if let Some(w) = self.watcher.take() {
            let _ = w.join();
        }
    }
}

// ------------------------------------------------------------------------------------------------ the hidden-icons flyout

static TEMPORARILY_SHOWN_SHELL: Mutex<i64> = Mutex::new(0);
static RETURN_FOCUS: Mutex<i64> = Mutex::new(0);

fn overflow_visible() -> bool {
    native::is_visible(overflow_handle())
}

fn release_temporary_taskbar() {
    let shell = std::mem::take(&mut *TEMPORARILY_SHOWN_SHELL.lock().unwrap());
    if shell != 0 && native::is_window(native::hwnd(shell)) {
        unsafe {
            let _ = ShowWindow(native::hwnd(shell), SW_HIDE);
        }
    }
}

/// Closes Explorer's flyout the way its own light-dismiss does: Escape to Explorer's own input site (never into the user's
/// application), then hide it.
fn dismiss() {
    if !overflow_visible() {
        release_temporary_taskbar();
        return;
    }
    let hwnd = overflow_handle();
    let thread = native::thread_and_pid(hwnd).0;
    let mut info = GUITHREADINFO { cbSize: std::mem::size_of::<GUITHREADINFO>() as u32, ..Default::default() };
    unsafe {
        let focused = GetGUIThreadInfo(thread, &mut info).is_ok() && !info.hwndFocus.0.is_null() && GetAncestor(info.hwndFocus, GA_ROOT) == hwnd;
        let target = if focused { info.hwndFocus } else { hwnd };
        let _ = PostMessageW(Some(target), WM_KEYDOWN, WPARAM(0x1B), LPARAM(0));
        let _ = PostMessageW(Some(target), WM_KEYUP, WPARAM(0x1B), LPARAM(0));
        let mut r = 0usize;
        SendMessageTimeoutW(target, 0x001F, WPARAM(0), LPARAM(0), SMTO_ABORTIFHUNG, 25, Some(&mut r));
        if target != hwnd {
            SendMessageTimeoutW(hwnd, 0x001F, WPARAM(0), LPARAM(0), SMTO_ABORTIFHUNG, 25, Some(&mut r));
        }
        let foreground = native::foreground();
        let back = native::hwnd(*RETURN_FOCUS.lock().unwrap());
        if (foreground == hwnd || foreground == find("Shell_TrayWnd")) && native::is_visible(back) && !native::is_iconic(back) {
            let _ = SetForegroundWindow(back);
        }
        let _ = ShowWindow(hwnd, SW_HIDE); // capture / fullscreen dismissal must remove native pixels immediately
    }
    release_temporary_taskbar();
}

const CHEVRON_PREFIXES: [&str; 3] = ["show hidden icons", "отображать скрытые значки", "показать скрытые значки"];

/// Finds the chevron ("show hidden icons") in the taskbar and invokes it.
fn press_chevron(uia: &Uia, shell: HWND) -> bool {
    let mut pressed = false;
    uia.visit_icons(shell, |element, id, name| {
        let class = unsafe { element.CurrentClassName().map(|b| b.to_string()).unwrap_or_default() };
        let lower = name.to_lowercase();
        let named = CHEVRON_PREFIXES.iter().any(|p| lower.starts_with(p));
        let matched = matches!(id, "SystemTray.ChevronButton" | "ChevronButton" | "NotificationChevron")
            || ((class == "Button" || class == "SystemTray.NormalButton") && named);
        if !matched {
            return false;
        }
        pressed = unsafe { element.GetCurrentPatternAs::<IUIAutomationInvokePattern>(UIA_InvokePatternId).and_then(|p| p.Invoke()).is_ok() };
        true
    });
    pressed
}

/// Opens Explorer's hidden-icons flyout (through its own chevron). The taskbar window is shown for a moment when it is hidden
/// (its empty region still keeps it invisible); `dismiss` puts everything back.
fn try_open() -> bool {
    if overflow_visible() {
        dismiss();
    }
    *RETURN_FOCUS.lock().unwrap() = native::hwnd_id(native::foreground());
    let shell = find("Shell_TrayWnd");
    if shell.0.is_null() {
        return false;
    }
    let hidden = !native::is_visible(shell);
    let mut opened = false;
    let Some(uia) = Uia::new() else { return false };
    if hidden {
        unsafe {
            let _ = ShowWindow(shell, SW_SHOWNOACTIVATE); // temporary presentation only; no taskbar state or work-area changes
        }
    }
    let mut invoked = false;
    for attempt in 0..5 {
        if attempt > 0 {
            if attempt == 1 {
                unsafe {
                    let _ = SetForegroundWindow(shell);
                }
            }
            std::thread::sleep(Duration::from_millis(80));
        }
        if press_chevron(&uia, shell) {
            invoked = true;
            break;
        }
    }
    if invoked {
        let start = Instant::now();
        while start.elapsed() < Duration::from_millis(1200) {
            if overflow_visible() {
                opened = true;
                if hidden {
                    *TEMPORARILY_SHOWN_SHELL.lock().unwrap() = native::hwnd_id(shell);
                }
                break;
            }
            std::thread::sleep(Duration::from_millis(25));
        }
    }
    if hidden && !opened {
        unsafe {
            let _ = ShowWindow(shell, SW_HIDE);
        }
    }
    opened
}

// ------------------------------------------------------------------------------------------------ read / activate

/// Reads Explorer's live notification buttons: those of the hidden-icons flyout (opened for the moment of reading) and the visible
/// ones of the taskbar. The registry supplies only their pictures.
pub fn read_items() -> Vec<TrayItem> {
    trace("read_items: start");
    let _scope = PresentationScope::new();
    let Some(uia) = Uia::new() else {
        trace("read_items: UI Automation could not be created");
        return Vec::new();
    };
    let icons = icon_snapshots();
    trace(&format!("read_items: {} icon snapshots", icons.len()));
    let mut result: Vec<TrayItem> = Vec::new();
    if overflow_visible() {
        dismiss();
    }
    read_into(&uia, &icons, overflow_handle(), true, &mut result);
    trace(&format!("read_items: {} before opening", result.len()));
    if !result.iter().any(|i| i.overflow) {
        let opened = try_open();
        trace(&format!("read_items: try_open = {opened}"));
        if opened {
            read_into(&uia, &icons, overflow_handle(), true, &mut result);
        }
    }
    read_into(&uia, &icons, find("Shell_TrayWnd"), false, &mut result);
    trace(&format!("read_items: {} in total", result.len()));
    dismiss();
    let mut seen = std::collections::HashSet::new();
    result.retain(|i| seen.insert(i.key.clone()));
    result.truncate(64);
    result
}

fn read_into(uia: &Uia, icons: &[IconSnapshot], hwnd: HWND, overflow: bool, result: &mut Vec<TrayItem>) {
    uia.visit_icons(hwnd, |element, id, name| {
        if name.is_empty() {
            return false;
        }
        let label = name.trim();
        let lower = label.to_lowercase();
        let mut image: Option<&IconSnapshot> = icons
            .iter()
            .filter(|i| {
                let n = i.name.trim().to_lowercase();
                !n.is_empty() && (lower == n || lower.starts_with(&format!("{n},")) || lower.starts_with(&format!("{n}\n")))
            })
            .max_by_key(|i| (i.name.len(), png_pixel_size(i.bytes.as_deref())));
        if image.is_none() {
            image = icons.iter().find(|i| {
                let exe = win_path::file_name_without_extension(&i.executable);
                !exe.is_empty() && lower.starts_with(&exe.to_lowercase())
            });
        }
        if image.is_none() && (lower.starts_with("windows security") || lower.starts_with("безопасность windows")) {
            image = icons.iter().find(|i| win_path::file_name(&i.executable).eq_ignore_ascii_case("SecurityHealthSystray.exe"));
        }
        if image.is_none() && (lower.contains("извлечение") || lower.contains("safely remove")) {
            image = icons
                .iter()
                .find(|i| win_path::file_name(&i.executable).eq_ignore_ascii_case("explorer.exe") && i.bytes.as_ref().is_some_and(|b| b.len() > 500));
        }
        let class = unsafe { element.CurrentClassName().map(|b| b.to_string()).unwrap_or_default() };
        if !overflow && id != "NotifyItemIcon" && !class.to_lowercase().contains("notifyicon") && class != "ToolbarWindow32" {
            return false;
        }
        result.push(TrayItem {
            key: runtime_key(element, id, name),
            name: label.to_string(),
            icon: image.and_then(|i| i.bytes.clone()),
            overflow,
            executable: image.map(|i| i.executable.clone()).filter(|e| !e.is_empty()),
        });
        false
    });
}

/// Invokes the live button of `item` (a left click). Explorer still owns notification callbacks, so the button is looked up again
/// on every click and stale icons cannot be invoked. `false` when the button is not available.
pub fn activate_item(item: &TrayItem) -> bool {
    let _scope = PresentationScope::new();
    let Some(uia) = Uia::new() else { return false };
    let opened = item.overflow && try_open();
    if item.overflow && !opened {
        return false;
    }
    let hwnd = if item.overflow { overflow_handle() } else { find("Shell_TrayWnd") };
    let invoke = |element: &IUIAutomationElement| -> bool {
        unsafe { element.GetCurrentPatternAs::<IUIAutomationInvokePattern>(UIA_InvokePatternId).and_then(|p| p.Invoke()).is_ok() }
    };
    let (mut matched, mut invoked) = (false, false);
    uia.visit_icons(hwnd, |element, id, name| {
        if runtime_key(element, id, name) != item.key {
            return false;
        }
        matched = true;
        invoked = invoke(element);
        true
    });
    // Explorer rebuilds virtual runtime ids when its presentation changes: re-resolve a unique live tooltip, never choose among duplicates.
    if !matched {
        let mut candidates = 0;
        uia.visit_icons(hwnd, |_, _, name| {
            if name.trim() == item.name {
                candidates += 1;
            }
            false
        });
        if candidates == 1 {
            uia.visit_icons(hwnd, |element, _, name| {
                if name.trim() != item.name {
                    return false;
                }
                invoked = invoke(element);
                true
            });
        }
    }
    if opened {
        dismiss();
    }
    invoked
}

/// A readable dump of the first nodes of a window's UI Automation tree (for diagnosing Explorer builds).
pub fn describe(hwnd: HWND, limit: usize) -> Vec<String> {
    let mut out = Vec::new();
    let Some(uia) = Uia::new() else { return vec!["no UI Automation".into()] };
    unsafe {
        let Ok(root) = uia.automation.ElementFromHandle(hwnd) else { return vec!["ElementFromHandle failed".into()] };
        let mut pending: Vec<(IUIAutomationElement, u32)> = vec![(root, 0)];
        while let Some((e, depth)) = pending.pop() {
            if out.len() >= limit {
                break;
            }
            out.push(format!(
                "{}{} | id={} | name={} | class={}",
                "  ".repeat(depth as usize),
                e.CurrentControlType().map(|t| t.0).unwrap_or(0),
                e.CurrentAutomationId().map(|b| b.to_string()).unwrap_or_default(),
                e.CurrentName().map(|b| b.to_string()).unwrap_or_default(),
                e.CurrentClassName().map(|b| b.to_string()).unwrap_or_default()
            ));
            if depth >= 12 {
                continue;
            }
            let mut kids = Vec::new();
            let mut child = uia.walker.GetFirstChildElement(&e).ok();
            while let Some(c) = child {
                let next = uia.walker.GetNextSiblingElement(&c).ok();
                kids.push(c);
                child = next;
            }
            for k in kids.into_iter().rev() {
                pending.push((k, depth + 1));
            }
        }
    }
    out
}

#[cfg(test)]
mod diagnostics {
    use super::*;
    use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};

    /// `cargo test -p fd-windows dump_taskbar_tree -- --ignored --nocapture`
    #[test]
    #[ignore]
    fn dump_taskbar_tree() {
        unsafe {
            let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        }
        for line in describe(find("Shell_TrayWnd"), 160) {
            println!("{line}");
        }
        let _scope = PresentationScope::new();
        println!("--- try_open: {}", try_open());
        println!("--- overflow handle {:?} visible {}", overflow_handle().0, overflow_visible());
        for line in describe(overflow_handle(), 120) {
            println!("{line}");
        }
        dismiss();
        drop(_scope);
        for round in 0..4 {
            let started = Instant::now();
            let items = read_items();
            println!("--- round {round}: {} items ({} overflow) in {} ms", items.len(), items.iter().filter(|i| i.overflow).count(), started.elapsed().as_millis());
            std::thread::sleep(Duration::from_millis(400));
        }
    }
}
