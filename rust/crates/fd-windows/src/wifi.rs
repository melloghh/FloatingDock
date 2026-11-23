//! Wi-Fi networks through the Native Wifi API: the list (optionally after a scan), connect a saved profile, disconnect.

use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use windows::core::{GUID, PCWSTR};
use windows::Win32::Foundation::HANDLE;
use windows::Win32::NetworkManagement::WiFi::{
    wlan_connection_mode_profile, WlanCloseHandle, WlanConnect, WlanDisconnect, WlanEnumInterfaces, WlanFreeMemory,
    WlanGetAvailableNetworkList, WlanGetProfileList, WlanOpenHandle, WlanRegisterNotification, WlanScan, DOT11_BSS_TYPE,
    L2_NOTIFICATION_DATA, WLAN_AVAILABLE_NETWORK_LIST, WLAN_CONNECTION_PARAMETERS, WLAN_INTERFACE_INFO_LIST, WLAN_NOTIFICATION_SOURCE_ACM,
    WLAN_NOTIFICATION_SOURCE_NONE, WLAN_PROFILE_INFO_LIST,
};

use crate::native::wide;

#[derive(Debug, Clone)]
pub struct Network {
    pub adapter: GUID,
    pub name: String,
    pub profile: String,
    pub signal: u32,
    pub connected: bool,
    pub secured: bool,
    pub available: bool,
}

pub struct Snapshot {
    pub networks: Vec<Network>,
    pub error: Option<String>,
}

fn message(code: u32) -> String {
    windows::core::Error::from_hresult(windows::core::HRESULT::from_win32(code)).message()
}

struct Session(HANDLE);

impl Session {
    fn open() -> Result<Session, u32> {
        let (mut negotiated, mut handle) = (0u32, HANDLE::default());
        let rc = unsafe { WlanOpenHandle(2, None, &mut negotiated, &mut handle) };
        if rc != 0 { Err(rc) } else { Ok(Session(handle)) }
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        unsafe { WlanCloseHandle(self.0, None) };
    }
}

struct Scan {
    adapter: GUID,
    done: AtomicBool,
}

unsafe extern "system" fn on_notify(data: *mut L2_NOTIFICATION_DATA, ctx: *mut c_void) {
    let (scan, n) = (&*(ctx as *const Scan), &*data);
    if n.InterfaceGuid == scan.adapter && n.NotificationSource == WLAN_NOTIFICATION_SOURCE_ACM && matches!(n.NotificationCode, 7 | 8) {
        scan.done.store(true, Ordering::SeqCst);
    }
}

fn scan_and_wait(h: HANDLE, id: GUID) {
    let scan = Box::new(Scan { adapter: id, done: AtomicBool::new(false) });
    unsafe {
        let mut prev = 0u32;
        let registered =
            WlanRegisterNotification(h, WLAN_NOTIFICATION_SOURCE_ACM, true, Some(on_notify), Some(&*scan as *const Scan as *const c_void), None, Some(&mut prev)) == 0;
        if WlanScan(h, &id, None, None, None) == 0 {
            let until = Instant::now() + Duration::from_millis(if registered { 4500 } else { 2000 });
            while Instant::now() < until && !(registered && scan.done.load(Ordering::SeqCst)) {
                std::thread::sleep(Duration::from_millis(50));
            }
        }
        if registered {
            WlanRegisterNotification(h, WLAN_NOTIFICATION_SOURCE_NONE, true, None, None, None, Some(&mut prev));
        }
    }
}

fn utf16_name(buf: &[u16]) -> String {
    String::from_utf16_lossy(&buf[..buf.iter().position(|&c| c == 0).unwrap_or(buf.len())])
}

pub fn read(scan: bool) -> Snapshot {
    let mut networks: Vec<Network> = Vec::new();
    let mut error: Option<String> = None;
    let session = match Session::open() {
        Ok(s) => s,
        Err(code) => return Snapshot { networks, error: if code == 1062 { None } else { Some(message(code)) } },
    };
    unsafe {
        let mut interfaces: *mut WLAN_INTERFACE_INFO_LIST = std::ptr::null_mut();
        let rc = WlanEnumInterfaces(session.0, None, &mut interfaces);
        if rc != 0 {
            return Snapshot { networks, error: Some(message(rc)) };
        }
        let list = &*interfaces;
        let count = (list.dwNumberOfItems as usize).min(32);
        for i in 0..count {
            let adapter = (*list.InterfaceInfo.as_ptr().add(i)).InterfaceGuid;
            if scan {
                scan_and_wait(session.0, adapter);
            }
            let mut avail: *mut WLAN_AVAILABLE_NETWORK_LIST = std::ptr::null_mut();
            let rc = WlanGetAvailableNetworkList(session.0, &adapter, 0, None, &mut avail);
            if rc != 0 {
                error = Some(if rc == 5 {
                    "Поиск Wi-Fi ограничен Windows. Сохранённые сети доступны без изменения разрешений.".to_string()
                } else {
                    message(rc)
                });
                let mut profiles: *mut WLAN_PROFILE_INFO_LIST = std::ptr::null_mut();
                if WlanGetProfileList(session.0, &adapter, None, &mut profiles) == 0 {
                    let pl = &*profiles;
                    for j in 0..(pl.dwNumberOfItems as usize).min(512) {
                        let name = utf16_name(&(*pl.ProfileInfo.as_ptr().add(j)).strProfileName);
                        if !name.is_empty() {
                            networks.push(Network { adapter, name: name.clone(), profile: name, signal: 0, connected: false, secured: false, available: false });
                        }
                    }
                    WlanFreeMemory(profiles as *const c_void);
                }
                continue;
            }
            let al = &*avail;
            for j in 0..(al.dwNumberOfItems as usize).min(512) {
                let n = &*al.Network.as_ptr().add(j);
                let len = (n.dot11Ssid.uSSIDLength as usize).min(32);
                let name = String::from_utf8_lossy(&n.dot11Ssid.ucSSID[..len]).into_owned();
                if !name.is_empty() {
                    networks.push(Network {
                        adapter,
                        name,
                        profile: utf16_name(&n.strProfileName),
                        signal: n.wlanSignalQuality,
                        connected: n.dwFlags & 1 != 0,
                        secured: n.bSecurityEnabled.as_bool(),
                        available: true,
                    });
                }
            }
            WlanFreeMemory(avail as *const c_void);
        }
        WlanFreeMemory(interfaces as *const c_void);
    }
    // one entry per (adapter, name): the connected, saved, strongest one; connected first, then by signal
    let mut best: Vec<Network> = Vec::new();
    for n in networks {
        match best.iter_mut().find(|b| b.adapter == n.adapter && b.name == n.name) {
            Some(b) => {
                let better = (n.connected, !n.profile.is_empty()) > (b.connected, !b.profile.is_empty());
                if better {
                    *b = n;
                }
            }
            None => best.push(n),
        }
    }
    best.sort_by(|a, b| b.connected.cmp(&a.connected).then(b.signal.cmp(&a.signal)));
    Snapshot { networks: best, error }
}

pub fn connect_saved(n: &Network) -> Result<(), String> {
    if n.profile.is_empty() {
        return Err("Для этой сети ещё не сохранён профиль Windows.".into());
    }
    let session = Session::open().map_err(message)?;
    let profile = wide(&n.profile);
    let params = WLAN_CONNECTION_PARAMETERS {
        wlanConnectionMode: wlan_connection_mode_profile,
        strProfile: PCWSTR(profile.as_ptr()),
        pDot11Ssid: std::ptr::null_mut(),
        pDesiredBssidList: std::ptr::null_mut(),
        dot11BssType: DOT11_BSS_TYPE(1), // infrastructure
        dwFlags: 0,
    };
    let rc = unsafe { WlanConnect(session.0, &n.adapter, &params, None) };
    if rc != 0 { Err(message(rc)) } else { Ok(()) }
}

pub fn disconnect(n: &Network) -> Result<(), String> {
    let session = Session::open().map_err(message)?;
    let rc = unsafe { WlanDisconnect(session.0, &n.adapter, None) };
    if rc != 0 { Err(message(rc)) } else { Ok(()) }
}
