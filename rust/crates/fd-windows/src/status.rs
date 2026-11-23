//! What the top bar shows that Windows tells us about: battery, keyboard layouts, the network, the sound volume.

use windows::core::GUID;
use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows::Win32::Globalization::{GetLocaleInfoW, LOCALE_SISO639LANGNAME, LOCALE_SNATIVEDISPLAYNAME};
use windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume;
use windows::Win32::Media::Audio::{eConsole, eRender, IMMDeviceEnumerator, MMDeviceEnumerator};
use windows::Win32::NetworkManagement::IpHelper::{
    GetAdaptersAddresses, GAA_FLAG_SKIP_ANYCAST, GAA_FLAG_SKIP_DNS_SERVER, GAA_FLAG_SKIP_MULTICAST, IP_ADAPTER_ADDRESSES_LH,
};
use windows::Win32::Networking::WinSock::{AF_INET, AF_UNSPEC, SOCKADDR_IN};
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_ALL};
use windows::Win32::System::Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS};
use windows::Win32::UI::Input::KeyboardAndMouse::{GetKeyboardLayout, GetKeyboardLayoutList, HKL};
use windows::Win32::UI::WindowsAndMessaging::{GetGUIThreadInfo, GetWindowThreadProcessId, PostMessageW, GUITHREADINFO, WM_INPUTLANGCHANGEREQUEST};

use crate::native;

// ------------------------------------------------------------------------------------------------ battery

pub struct Battery {
    pub percent: u8,
    pub charging: bool,
}

/// `None` when the machine has no battery.
pub fn battery() -> Option<Battery> {
    let mut p = SYSTEM_POWER_STATUS::default();
    unsafe { GetSystemPowerStatus(&mut p).ok()? };
    (p.BatteryLifePercent <= 100 && p.BatteryFlag & 128 == 0).then_some(Battery { percent: p.BatteryLifePercent, charging: p.ACLineStatus == 1 })
}

// ------------------------------------------------------------------------------------------------ keyboard layouts

#[derive(Debug, Clone)]
pub struct Layout {
    pub handle: i64,
    pub name: String,
    pub tag: String,
}

fn locale_text(lcid: u32, kind: u32) -> Option<String> {
    let mut buf = [0u16; 128];
    let n = unsafe { GetLocaleInfoW(lcid, kind, Some(&mut buf)) };
    (n > 1).then(|| String::from_utf16_lossy(&buf[..(n as usize - 1)]))
}

/// "EN" / "RU": the two-letter language tag of a layout handle.
pub fn language_tag(hkl: i64) -> Option<String> {
    locale_text((hkl & 0xffff) as u32, LOCALE_SISO639LANGNAME).map(|s| s.to_uppercase())
}

pub fn installed_layouts() -> Vec<Layout> {
    unsafe {
        let count = GetKeyboardLayoutList(None).clamp(0, 128) as usize;
        let mut list = vec![HKL::default(); count];
        let n = GetKeyboardLayoutList(Some(&mut list)).max(0) as usize;
        list.into_iter()
            .take(n)
            .map(|h| {
                let handle = h.0 as isize as i64;
                let lcid = (handle & 0xffff) as u32;
                match (locale_text(lcid, LOCALE_SNATIVEDISPLAYNAME), language_tag(handle)) {
                    (Some(name), Some(tag)) => Layout { handle, name, tag },
                    _ => Layout { handle, name: format!("Раскладка {handle:X}"), tag: "—".into() },
                }
            })
            .collect()
    }
}

pub fn keyboard_layout_of_thread(thread: u32) -> i64 {
    unsafe { GetKeyboardLayout(thread).0 as isize as i64 }
}

/// The window that holds the keyboard focus in `target`'s thread (the input site), else `target`.
pub fn input_site(target: HWND) -> HWND {
    let thread = native::thread_and_pid(target).0;
    let mut info = GUITHREADINFO { cbSize: std::mem::size_of::<GUITHREADINFO>() as u32, ..Default::default() };
    unsafe {
        if thread != 0 && GetGUIThreadInfo(thread, &mut info).is_ok() && native::is_window(info.hwndFocus) {
            return info.hwndFocus;
        }
    }
    target
}

pub fn request_layout(target: HWND, layout: i64) -> bool {
    if !native::is_window(target) || !installed_layouts().iter().any(|l| l.handle == layout) {
        return false;
    }
    unsafe { PostMessageW(Some(input_site(target)), WM_INPUTLANGCHANGEREQUEST, WPARAM(0), LPARAM(layout as isize)).is_ok() }
}

#[allow(dead_code)]
fn _thread_of(h: HWND) -> u32 {
    unsafe { GetWindowThreadProcessId(h, None) }
}

// ------------------------------------------------------------------------------------------------ network adapters

pub struct Adapter {
    pub name: String,
    pub kind: &'static str,
    pub address: Option<String>,
}

const IF_TYPE_LOOPBACK: u32 = 24;
const IF_TYPE_TUNNEL: u32 = 131;
const IF_TYPE_WIFI: u32 = 71;

/// Adapters that are up, without loopback and tunnels (what `NetworkInterface.GetAllNetworkInterfaces()` filtered to).
pub fn adapters() -> Vec<Adapter> {
    let mut out = Vec::new();
    unsafe {
        let flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
        let mut size = 16 * 1024u32;
        let mut buf: Vec<u8> = vec![0; size as usize];
        let mut rc = GetAdaptersAddresses(AF_UNSPEC.0 as u32, flags, None, Some(buf.as_mut_ptr() as *mut IP_ADAPTER_ADDRESSES_LH), &mut size);
        if rc == 111 {
            buf = vec![0; size as usize];
            rc = GetAdaptersAddresses(AF_UNSPEC.0 as u32, flags, None, Some(buf.as_mut_ptr() as *mut IP_ADAPTER_ADDRESSES_LH), &mut size);
        }
        if rc != 0 {
            return out;
        }
        let mut cur = buf.as_ptr() as *const IP_ADAPTER_ADDRESSES_LH;
        while !cur.is_null() {
            let a = &*cur;
            if a.OperStatus.0 == 1 && a.IfType != IF_TYPE_LOOPBACK && a.IfType != IF_TYPE_TUNNEL {
                let name = a.FriendlyName.to_string().unwrap_or_default();
                let mut address = None;
                let mut u = a.FirstUnicastAddress;
                while !u.is_null() && address.is_none() {
                    let sa = (*u).Address.lpSockaddr;
                    if !sa.is_null() && (*sa).sa_family == AF_INET {
                        let ip = (*(sa as *const SOCKADDR_IN)).sin_addr.S_un.S_un_b;
                        address = Some(format!("{}.{}.{}.{}", ip.s_b1, ip.s_b2, ip.s_b3, ip.s_b4));
                    }
                    u = (*u).Next;
                }
                out.push(Adapter { name, kind: if a.IfType == IF_TYPE_WIFI { "Wi-Fi" } else { "Ethernet" }, address });
            }
            cur = a.Next;
        }
    }
    out
}

/// At least one adapter is up (like `NetworkInterface.GetIsNetworkAvailable()`).
pub fn network_available() -> bool {
    !adapters().is_empty()
}

// ------------------------------------------------------------------------------------------------ audio

pub struct Volume {
    pub volume: f32,
    pub muted: bool,
}

/// The default output endpoint, opened on demand so device changes never leave a stale endpoint. Needs COM on the thread.
fn with_endpoint<T>(f: impl FnOnce(&IAudioEndpointVolume) -> windows::core::Result<T>) -> windows::core::Result<T> {
    unsafe {
        let enumerator: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)?;
        let device = enumerator.GetDefaultAudioEndpoint(eRender, eConsole)?;
        let endpoint: IAudioEndpointVolume = device.Activate(CLSCTX_ALL, None)?;
        f(&endpoint)
    }
}

pub fn read_volume() -> Result<Volume, String> {
    with_endpoint(|e| unsafe { Ok(Volume { volume: e.GetMasterVolumeLevelScalar()?, muted: e.GetMute()?.as_bool() }) }).map_err(|e| e.to_string())
}

pub fn set_volume(volume: f32) -> Result<(), String> {
    with_endpoint(|e| unsafe { e.SetMasterVolumeLevelScalar(volume.clamp(0.0, 1.0), std::ptr::null()) }).map_err(|e| e.to_string())
}

pub fn set_muted(mute: bool) -> Result<(), String> {
    with_endpoint(|e| unsafe { e.SetMute(mute, std::ptr::null()) }).map_err(|e| e.to_string())
}

#[allow(dead_code)]
const _: GUID = GUID::zeroed();

// ------------------------------------------------------------------------------------------------ network change events

pub struct NetworkWatcher {
    handle: windows::Win32::Foundation::HANDLE,
    _callback: Box<Box<dyn Fn() + Send + Sync>>,
}

// The handle is an opaque registration id; cancelling it from another thread is allowed.
unsafe impl Send for NetworkWatcher {}

unsafe extern "system" fn on_interface_change(
    ctx: *const std::ffi::c_void,
    _row: *const windows::Win32::NetworkManagement::IpHelper::MIB_IPINTERFACE_ROW,
    _kind: windows::Win32::NetworkManagement::IpHelper::MIB_NOTIFICATION_TYPE,
) {
    let f = &*(ctx as *const Box<dyn Fn() + Send + Sync>);
    f();
}

/// Calls `f` (on a pool thread) whenever an IP interface comes, goes or changes.
pub fn watch_network_changes(f: impl Fn() + Send + Sync + 'static) -> Option<NetworkWatcher> {
    use windows::Win32::NetworkManagement::IpHelper::NotifyIpInterfaceChange;
    let callback: Box<Box<dyn Fn() + Send + Sync>> = Box::new(Box::new(f));
    let mut handle = windows::Win32::Foundation::HANDLE::default();
    let rc = unsafe {
        NotifyIpInterfaceChange(AF_UNSPEC, Some(on_interface_change), Some(&*callback as *const Box<dyn Fn() + Send + Sync> as *const std::ffi::c_void), false, &mut handle)
    };
    (rc.0 == 0).then_some(NetworkWatcher { handle, _callback: callback })
}

impl Drop for NetworkWatcher {
    fn drop(&mut self) {
        unsafe {
            let _ = windows::Win32::NetworkManagement::IpHelper::CancelMibChangeNotify2(self.handle);
        }
    }
}
