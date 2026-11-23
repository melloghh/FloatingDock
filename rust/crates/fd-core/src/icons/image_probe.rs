//! Limits for every locally supplied icon (user override, icon pack entry), checked on the file header BEFORE any decoder sees
//! the file. Reads just enough of a PNG / ICO header to know its dimensions; never decodes pixels.

use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

pub const MAX_FILE_BYTES: u64 = 20 * 1024 * 1024;
pub const MAX_DIMENSION: i64 = 4096;
pub const MAX_PIXELS: i64 = 4096 * 2048;
pub const MAX_ICO_IMAGES: usize = 64;
pub const MAX_OVERRIDES: usize = 1000;
pub const MAX_PACK_IMAGES: usize = 5000;
pub const MAX_PACKS: usize = 100;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ImageFormat {
    Png,
    Ico,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ImageInfo {
    pub format: ImageFormat,
    pub width: i64,
    pub height: i64,
    pub image_count: usize,
}

const PNG_SIGNATURE: [u8; 8] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

/// Validates a file on disk.
pub fn probe_file(path: &Path) -> Result<ImageInfo, String> {
    let meta = std::fs::metadata(path).map_err(|_| "The file does not exist.".to_string())?;
    if meta.len() > MAX_FILE_BYTES {
        return Err("The file is too large.".into());
    }
    let mut f = File::open(path).map_err(|e| e.to_string())?;
    probe(&mut f, meta.len())
}

pub fn probe_bytes(bytes: &[u8]) -> Result<ImageInfo, String> {
    probe(&mut std::io::Cursor::new(bytes), bytes.len() as u64)
}

fn probe<S: Read + Seek>(s: &mut S, length: u64) -> Result<ImageInfo, String> {
    if length > MAX_FILE_BYTES {
        return Err("The file is too large.".into());
    }
    if length < 8 {
        return Err("The file is too small to be an image.".into());
    }
    let mut head = vec![0u8; length.min(24) as usize];
    s.seek(SeekFrom::Start(0)).map_err(|e| e.to_string())?;
    s.read_exact(&mut head).map_err(|e| e.to_string())?;
    if head[..8] == PNG_SIGNATURE {
        return probe_png(&head);
    }
    if head[0] == 0 && head[1] == 0 && (head[2] == 1 || head[2] == 2) && head[3] == 0 {
        return probe_ico(s, length);
    }
    Err("Only PNG and ICO images are supported.".into())
}

fn be32(b: &[u8], o: usize) -> i64 {
    (b[o] as i64) << 24 | (b[o + 1] as i64) << 16 | (b[o + 2] as i64) << 8 | b[o + 3] as i64
}

fn probe_png(head: &[u8]) -> Result<ImageInfo, String> {
    // signature (8) + chunk length (4) + "IHDR" (4) + width (4) + height (4)
    if head.len() < 24 || &head[12..16] != b"IHDR" {
        return Err("The PNG header is damaged.".into());
    }
    check_dimensions(ImageFormat::Png, be32(head, 16), be32(head, 20), 1)
}

fn check_dimensions(format: ImageFormat, w: i64, h: i64, count: usize) -> Result<ImageInfo, String> {
    if w <= 0 || h <= 0 {
        return Err("The image has no size.".into());
    }
    if w > MAX_DIMENSION || h > MAX_DIMENSION {
        return Err(format!("The image is too large ({w}x{h}, at most {MAX_DIMENSION}x{MAX_DIMENSION})."));
    }
    if w * h > MAX_PIXELS {
        return Err(format!("The image has too many pixels ({w}x{h})."));
    }
    Ok(ImageInfo { format, width: w, height: h, image_count: count })
}

fn le32(b: &[u8], o: usize) -> u32 {
    u32::from_le_bytes([b[o], b[o + 1], b[o + 2], b[o + 3]])
}

fn probe_ico<S: Read + Seek>(s: &mut S, length: u64) -> Result<ImageInfo, String> {
    let mut dir_head = [0u8; 6];
    s.seek(SeekFrom::Start(0)).map_err(|e| e.to_string())?;
    s.read_exact(&mut dir_head).map_err(|e| e.to_string())?;
    let count = dir_head[4] as usize | (dir_head[5] as usize) << 8;
    if count < 1 {
        return Err("The icon contains no images.".into());
    }
    if count > MAX_ICO_IMAGES {
        return Err("The icon contains too many images.".into());
    }
    if 6 + count as u64 * 16 > length {
        return Err("The icon directory is damaged.".into());
    }
    let mut dir = vec![0u8; count * 16];
    s.read_exact(&mut dir).map_err(|e| e.to_string())?;
    let (mut max_w, mut max_h) = (0i64, 0i64);
    for i in 0..count {
        let e = &dir[i * 16..i * 16 + 16];
        let (mut w, mut h) = (if e[0] == 0 { 256 } else { e[0] as i64 }, if e[1] == 0 { 256 } else { e[1] as i64 });
        let size = le32(e, 8) as u64;
        let offset = le32(e, 12) as u64;
        if size == 0 || offset < 6 + count as u64 * 16 || offset + size > length {
            return Err("The icon directory is damaged.".into());
        }
        // an image inside an ICO may be a PNG that is larger than 256 pixels: check its real header
        let mut png_image = false;
        if size >= 24 {
            let mut ph = [0u8; 24];
            s.seek(SeekFrom::Start(offset)).map_err(|e| e.to_string())?;
            s.read_exact(&mut ph).map_err(|e| e.to_string())?;
            if ph[..8] == PNG_SIGNATURE {
                png_image = true;
                let inner = probe_png(&ph)?;
                w = inner.width;
                h = inner.height;
            }
        }
        if !png_image {
            if size < 12 {
                return Err("The ICO bitmap header is truncated.".into());
            }
            let mut dib = [0u8; 12];
            s.seek(SeekFrom::Start(offset)).map_err(|e| e.to_string())?;
            s.read_exact(&mut dib).map_err(|e| e.to_string())?;
            let header_size = le32(&dib, 0) as u64;
            let (real_w, real_h): (i64, i64) = if header_size == 12 {
                (u16::from_le_bytes([dib[4], dib[5]]) as i64, u16::from_le_bytes([dib[6], dib[7]]) as i64)
            } else if (40..=size).contains(&header_size) {
                (i32::from_le_bytes([dib[4], dib[5], dib[6], dib[7]]) as i64, (i32::from_le_bytes([dib[8], dib[9], dib[10], dib[11]]) as i64).abs())
            } else {
                return Err("The ICO bitmap header is invalid.".into());
            };
            if real_h % 2 != 0 {
                return Err("The ICO bitmap height is invalid.".into());
            }
            check_dimensions(ImageFormat::Ico, real_w, real_h / 2, count)?;
            w = real_w;
            h = real_h / 2;
        }
        max_w = max_w.max(w);
        max_h = max_h.max(h);
    }
    check_dimensions(ImageFormat::Ico, max_w, max_h, count)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn png(w: u32, h: u32) -> Vec<u8> {
        let mut v = PNG_SIGNATURE.to_vec();
        v.extend_from_slice(&13u32.to_be_bytes());
        v.extend_from_slice(b"IHDR");
        v.extend_from_slice(&w.to_be_bytes());
        v.extend_from_slice(&h.to_be_bytes());
        v
    }

    #[test]
    fn png_dimensions_and_limits() {
        assert_eq!(probe_bytes(&png(64, 48)).unwrap().width, 64);
        assert!(probe_bytes(&png(5000, 10)).is_err());
        assert!(probe_bytes(&png(4096, 4096)).is_err());
        assert!(probe_bytes(&png(0, 10)).is_err());
        assert!(probe_bytes(b"not an image at all").is_err());
        assert!(probe_bytes(&[1, 2, 3]).is_err());
    }

    #[test]
    fn ico_with_an_embedded_png() {
        let inner = png(256, 256);
        let mut ico = vec![0, 0, 1, 0, 1, 0];
        ico.extend_from_slice(&[0, 0, 0, 0, 1, 0, 32, 0]);
        ico.extend_from_slice(&(inner.len() as u32).to_le_bytes());
        ico.extend_from_slice(&22u32.to_le_bytes());
        ico.extend_from_slice(&inner);
        let info = probe_bytes(&ico).unwrap();
        assert_eq!((info.format, info.width, info.image_count), (ImageFormat::Ico, 256, 1));
    }
}
