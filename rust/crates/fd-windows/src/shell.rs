//! Small helpers around the shell: property stores (AppUserModelID of a window), known folders.
//! Everything here must run on an STA thread (the [`ShellWorker`](crate::ShellWorker)).

use windows::core::{GUID, PWSTR};
use windows::Win32::Foundation::{HWND, PROPERTYKEY};
use windows::Win32::System::Com::StructuredStorage::PropVariantToStringAlloc;
use windows::Win32::System::Com::CoTaskMemFree;
use windows::Win32::UI::Shell::PropertiesSystem::{IPropertyStore, SHGetPropertyStoreForWindow};

/// `System.AppUserModel.ID`
pub const PKEY_APP_USER_MODEL_ID: PROPERTYKEY =
    PROPERTYKEY { fmtid: GUID::from_u128(0x9F4C2855_9F79_4B39_A8D0_E1D42DE1D5F3), pid: 5 };

pub fn read_string(store: &IPropertyStore, key: &PROPERTYKEY) -> Option<String> {
    unsafe {
        let pv = store.GetValue(key).ok()?;
        let p: PWSTR = PropVariantToStringAlloc(&pv).ok()?;
        let s = p.to_string().ok();
        CoTaskMemFree(Some(p.0 as *const _));
        s.filter(|s| !s.trim().is_empty())
    }
}

/// The AppUserModelID a window declares (a packaged app's windows have one; most classic windows do not).
pub fn read_aumid_of_window(hwnd: HWND) -> Option<String> {
    unsafe {
        let store: IPropertyStore = SHGetPropertyStoreForWindow(hwnd).ok()?;
        read_string(&store, &PKEY_APP_USER_MODEL_ID)
    }
}
