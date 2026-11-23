//! The single place that asks Windows for an application icon. Produces a cached PNG (via `IShellItemImageFactory`, up to
//! 256 px with alpha) so that the UI only ever loads plain image files - the same way for system icons, icon-pack icons and
//! user overrides. Call it on the [`ShellWorker`](crate::ShellWorker) thread.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::SystemTime;

use fd_core::icons::png_writer;
use fd_core::AppRef;
use sha2::{Digest, Sha256};
use windows::core::PCWSTR;
use windows::Win32::Foundation::SIZE;
use windows::Win32::Graphics::Gdi::{
    DeleteObject, GetDC, GetDIBits, GetObjectW, ReleaseDC, BITMAP, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS, HBITMAP,
    HGDIOBJ,
};
use windows::Win32::UI::Shell::{IShellItemImageFactory, SHCreateItemFromParsingName, SIIGBF_BIGGERSIZEOK, SIIGBF_ICONONLY};

use crate::native::wide;

pub struct ShellIconExtractor {
    cache_dir: PathBuf,
    writes: AtomicUsize,
}

fn non_empty(s: &Option<String>) -> Option<&str> {
    s.as_deref().filter(|v| !v.is_empty())
}

/// Shell items that carry the icon of `app`, in preference order.
pub fn icon_sources(app: &AppRef) -> Vec<String> {
    let mut v = Vec::new();
    if let Some(a) = non_empty(&app.aumid) {
        if a.contains('!') || non_empty(&app.exe_path).is_none() {
            v.push(format!("shell:AppsFolder\\{a}"));
        }
    }
    if let Some(s) = non_empty(&app.shortcut_path) {
        if Path::new(s).is_file() {
            v.push(s.to_string());
        }
    }
    if let Some(e) = non_empty(&app.exe_path) {
        if Path::new(e).is_file() {
            v.push(e.to_string());
        }
    }
    if let Some(p) = non_empty(&app.shell_parsing_name) {
        v.push(format!("shell:AppsFolder\\{p}"));
    }
    v
}

impl ShellIconExtractor {
    pub fn new(cache_dir: &Path) -> ShellIconExtractor {
        let _ = fs::create_dir_all(cache_dir);
        let e = ShellIconExtractor { cache_dir: cache_dir.to_path_buf(), writes: AtomicUsize::new(0) };
        e.prune_cache();
        e
    }

    pub fn get_icon_png(&self, app: &AppRef, size_px: i32) -> Option<PathBuf> {
        let size = size_px.clamp(16, 256);
        for source in icon_sources(app) {
            let cached = self.cache_path(&source, size);
            if cached.is_file() && is_fresh(&source, &cached) {
                return Some(cached);
            }
            let Some(png) = render(&source, size) else { continue };
            if fs::write(&cached, png).is_err() {
                return None;
            }
            if self.writes.fetch_add(1, Ordering::Relaxed) % 32 == 31 {
                self.prune_cache();
            }
            return Some(cached);
        }
        None
    }

    fn prune_cache(&self) {
        let Ok(rd) = fs::read_dir(&self.cache_dir) else { return };
        let mut files: Vec<(SystemTime, u64, PathBuf)> = rd
            .flatten()
            .filter(|e| e.path().extension().is_some_and(|x| x == "png"))
            .filter_map(|e| {
                let m = e.metadata().ok()?;
                Some((m.modified().ok()?, m.len(), e.path()))
            })
            .collect();
        files.sort_by(|a, b| b.0.cmp(&a.0));
        let mut bytes = 0u64;
        for (i, (_, len, path)) in files.iter().enumerate() {
            bytes += len;
            if i >= 2048 || bytes > 64 * 1024 * 1024 {
                let _ = fs::remove_file(path);
            }
        }
    }

    fn cache_path(&self, source: &str, size: i32) -> PathBuf {
        let hash = hex::encode_upper(Sha256::digest(source.to_lowercase().as_bytes()));
        self.cache_dir.join(format!("{}_{size}.png", &hash[..24]))
    }
}

fn is_fresh(source: &str, cached: &Path) -> bool {
    // AppsFolder entries are no files: refreshed by clearing the cache when the app list changes
    let (Ok(s), Ok(c)) = (fs::metadata(source), fs::metadata(cached)) else { return true };
    match (s.modified(), c.modified()) {
        (Ok(sm), Ok(cm)) => sm <= cm,
        _ => true,
    }
}

fn render(source: &str, size: i32) -> Option<Vec<u8>> {
    unsafe {
        let name = wide(source);
        let factory: IShellItemImageFactory = SHCreateItemFromParsingName(PCWSTR(name.as_ptr()), None).ok()?;
        let hbm = factory.GetImage(SIZE { cx: size, cy: size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK).ok()?;
        let png = bitmap_to_png(hbm);
        let _ = DeleteObject(HGDIOBJ(hbm.0));
        png
    }
}

unsafe fn bitmap_to_png(hbm: HBITMAP) -> Option<Vec<u8>> {
    let mut bmp = BITMAP::default();
    if GetObjectW(HGDIOBJ(hbm.0), std::mem::size_of::<BITMAP>() as i32, Some(&mut bmp as *mut _ as *mut _)) == 0 || bmp.bmWidth <= 0 || bmp.bmHeight == 0 {
        return None;
    }
    let (w, h) = (bmp.bmWidth as usize, bmp.bmHeight.unsigned_abs() as usize);
    if w > 4096 || h > 4096 {
        return None; // a shell icon is never this large; do not allocate for a bogus size
    }
    let mut info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: w as i32,
            biHeight: -(h as i32), // top-down
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..Default::default()
        },
        ..Default::default()
    };
    let mut pixels = vec![0u8; w * h * 4];
    let dc = GetDC(None);
    let lines = GetDIBits(dc, hbm, 0, h as u32, Some(pixels.as_mut_ptr() as *mut _), &mut info, DIB_RGB_COLORS);
    ReleaseDC(None, dc);
    if lines == 0 {
        return None;
    }
    // some icons come without an alpha channel: treat colour as opaque
    let mut any_alpha = false;
    let mut any_colour = false;
    for px in pixels.chunks_exact(4) {
        if px[3] != 0 {
            any_alpha = true;
            break;
        }
        if (px[0] | px[1] | px[2]) != 0 {
            any_colour = true;
        }
    }
    if !any_alpha {
        if !any_colour {
            return None;
        }
        for px in pixels.chunks_exact_mut(4) {
            px[3] = 255;
        }
    }
    png_writer::encode_bgra(w, h, &pixels, false).ok()
}
