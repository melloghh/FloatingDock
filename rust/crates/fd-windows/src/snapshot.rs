//! Small pictures of what a window showed the last time it was seen, kept in memory only. DWM draws nothing live for a minimized
//! window, so the dock's preview shows this picture instead of an empty card. A picture is taken just as a window starts to
//! minimize and when another window takes the focus from it; a window that is minimized never gets one.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use fd_core::icons::png_writer;
use windows::Win32::Foundation::HWND;
use windows::Win32::Graphics::Gdi::{
    CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, GetDC, ReleaseDC, SelectObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB,
    DIB_RGB_COLORS, HGDIOBJ,
};
use windows::Win32::Storage::Xps::{PrintWindow, PRINT_WINDOW_FLAGS};
use windows::Win32::UI::WindowsAndMessaging::IsHungAppWindow;

use crate::native;

const MAX_ENTRIES: usize = 24;
const MAX_WIDTH: usize = 480;
const MAX_HEIGHT: usize = 320;
const MIN_GAP: Duration = Duration::from_millis(2000);

#[derive(Default)]
struct State {
    png: HashMap<i64, Vec<u8>>,
    taken: HashMap<i64, Instant>,
    order: Vec<i64>,
}

#[derive(Default)]
pub struct WindowSnapshotCache {
    state: Mutex<State>,
}

impl WindowSnapshotCache {
    /// Takes a picture of the window unless one was taken a moment ago (or `force`); true when the cache has a new one.
    pub fn remember(&self, id: i64, force: bool) -> bool {
        let now = Instant::now();
        {
            let mut st = self.state.lock().unwrap();
            if !force && st.taken.get(&id).is_some_and(|t| now.duration_since(*t) < MIN_GAP) {
                return false;
            }
            st.taken.insert(id, now);
        }
        let Some(png) = capture(native::hwnd(id)) else { return false };
        let mut st = self.state.lock().unwrap();
        st.png.insert(id, png);
        st.order.retain(|x| *x != id);
        st.order.push(id);
        while st.order.len() > MAX_ENTRIES {
            let oldest = st.order.remove(0);
            st.png.remove(&oldest);
            st.taken.remove(&oldest);
        }
        true
    }

    /// The PNG of the last picture of the window, or `None`.
    pub fn get(&self, id: i64) -> Option<Vec<u8>> {
        self.state.lock().unwrap().png.get(&id).cloned()
    }

    /// The window is gone: its picture goes with it.
    pub fn forget(&self, id: i64) {
        let mut st = self.state.lock().unwrap();
        st.png.remove(&id);
        st.taken.remove(&id);
        st.order.retain(|x| *x != id);
    }
}

fn capture(hwnd: HWND) -> Option<Vec<u8>> {
    if hwnd.0.is_null() || !native::is_visible(hwnd) || native::is_iconic(hwnd) || unsafe { IsHungAppWindow(hwnd).as_bool() } {
        return None;
    }
    let r = native::window_rect(hwnd)?;
    let (w, h) = ((r.right - r.left) as usize, (r.bottom - r.top) as usize);
    if !(64..=8192).contains(&w) || !(64..=8192).contains(&h) {
        return None;
    }
    unsafe {
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: w as i32,
                biHeight: -(h as i32),
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let screen = GetDC(None);
        let mem = CreateCompatibleDC(Some(screen));
        let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
        let result = (|| {
            let dib = CreateDIBSection(Some(screen), &info, DIB_RGB_COLORS, &mut bits, None, 0).ok()?;
            if bits.is_null() {
                let _ = DeleteObject(HGDIOBJ(dib.0));
                return None;
            }
            let old = SelectObject(mem, HGDIOBJ(dib.0));
            // PW_RENDERFULLCONTENT: windows drawn with DirectComposition (browsers, Electron apps) are included.
            let ok = PrintWindow(hwnd, mem, PRINT_WINDOW_FLAGS(2)).as_bool();
            let png = ok.then(|| {
                let pixels = std::slice::from_raw_parts(bits as *const u8, w * h * 4);
                shrink(pixels, w, h)
            });
            SelectObject(mem, old);
            let _ = DeleteObject(HGDIOBJ(dib.0));
            png.flatten()
        })();
        let _ = DeleteDC(mem);
        ReleaseDC(None, screen);
        result
    }
}

/// Averages the pixels down to at most `MAX_WIDTH` x `MAX_HEIGHT` and encodes the result; `None` for a picture that is all black.
fn shrink(src: &[u8], w: usize, h: usize) -> Option<Vec<u8>> {
    let f = (MAX_WIDTH as f64 / w as f64).min(MAX_HEIGHT as f64 / h as f64).min(1.0);
    let (nw, nh) = (((w as f64 * f) as usize).max(1), ((h as f64 * f) as usize).max(1));
    let mut dst = vec![0u8; nw * nh * 4];
    let mut light: u64 = 0;
    for y in 0..nh {
        let y0 = y * h / nh;
        let y1 = ((y + 1) * h / nh).max(y0 + 1);
        for x in 0..nw {
            let x0 = x * w / nw;
            let x1 = ((x + 1) * w / nw).max(x0 + 1);
            let (mut b, mut g, mut r) = (0u64, 0u64, 0u64);
            for sy in y0..y1 {
                let mut i = (sy * w + x0) * 4;
                for _ in x0..x1 {
                    b += src[i] as u64;
                    g += src[i + 1] as u64;
                    r += src[i + 2] as u64;
                    i += 4;
                }
            }
            let n = ((y1 - y0) * (x1 - x0)) as u64;
            let o = (y * nw + x) * 4;
            dst[o] = (b / n) as u8;
            dst[o + 1] = (g / n) as u8;
            dst[o + 2] = (r / n) as u8;
            dst[o + 3] = 255;
            light += dst[o] as u64 + dst[o + 1] as u64 + dst[o + 2] as u64;
        }
    }
    // A window that could not draw itself comes back black: that is not a picture worth showing.
    if light < (nw * nh * 3) as u64 {
        return None;
    }
    png_writer::encode_bgra(nw, nh, &dst, false).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shrinks_and_rejects_black_pictures() {
        let (w, h) = (960usize, 640usize);
        let mut px = vec![200u8; w * h * 4];
        let png = shrink(&px, w, h).expect("a lit picture is kept");
        let info = fd_core::icons::image_probe::probe_bytes(&png).unwrap();
        assert_eq!((info.width, info.height), (480, 320));
        px.iter_mut().for_each(|b| *b = 0);
        assert!(shrink(&px, w, h).is_none());
    }

    #[test]
    fn cache_limits_and_forgets() {
        let c = WindowSnapshotCache::default();
        {
            let mut st = c.state.lock().unwrap();
            for i in 0..30 {
                st.png.insert(i, vec![1]);
                st.order.push(i);
            }
        }
        assert!(c.get(5).is_some());
        c.forget(5);
        assert!(c.get(5).is_none());
    }
}
