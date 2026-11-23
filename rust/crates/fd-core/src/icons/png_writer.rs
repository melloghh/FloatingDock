//! Minimal PNG encoder (RGBA8, no interlace) used to store extracted shell icons in the disk cache.

use std::io::Write;

use flate2::write::ZlibEncoder;
use flate2::Compression;

fn crc_table() -> &'static [u32; 256] {
    static T: std::sync::OnceLock<[u32; 256]> = std::sync::OnceLock::new();
    T.get_or_init(|| {
        let mut t = [0u32; 256];
        for n in 0..256u32 {
            let mut c = n;
            for _ in 0..8 {
                c = if c & 1 != 0 { 0xEDB88320 ^ (c >> 1) } else { c >> 1 };
            }
            t[n as usize] = c;
        }
        t
    })
}

fn write_chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_be_bytes());
    out.extend_from_slice(kind);
    out.extend_from_slice(data);
    let t = crc_table();
    let mut crc = 0xFFFF_FFFFu32;
    for b in kind.iter().chain(data.iter()) {
        crc = t[((crc ^ *b as u32) & 0xFF) as usize] ^ (crc >> 8);
    }
    out.extend_from_slice(&(crc ^ 0xFFFF_FFFF).to_be_bytes());
}

/// `bgra`: top-down 32 bpp pixels, B G R A order. `premultiplied`: colour channels are alpha-premultiplied (they are
/// un-premultiplied for PNG).
pub fn encode_bgra(width: usize, height: usize, bgra: &[u8], premultiplied: bool) -> Result<Vec<u8>, String> {
    if width == 0 || height == 0 || bgra.len() < width * height * 4 {
        return Err("bad pixel buffer".into());
    }
    let mut raw = Vec::with_capacity((width * 4 + 1) * height);
    for y in 0..height {
        raw.push(0); // filter: none
        for x in 0..width {
            let i = (y * width + x) * 4;
            let (mut b, mut g, mut r, a) = (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
            if premultiplied && a > 0 && a < 255 {
                let un = |c: u8| ((c as u32 * 255 / a as u32).min(255)) as u8;
                r = un(r);
                g = un(g);
                b = un(b);
            }
            raw.extend_from_slice(&[r, g, b, a]);
        }
    }
    let mut out = vec![0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    let mut ihdr = Vec::with_capacity(13);
    ihdr.extend_from_slice(&(width as u32).to_be_bytes());
    ihdr.extend_from_slice(&(height as u32).to_be_bytes());
    ihdr.extend_from_slice(&[8, 6, 0, 0, 0]);
    write_chunk(&mut out, b"IHDR", &ihdr);
    let mut z = ZlibEncoder::new(Vec::new(), Compression::fast());
    z.write_all(&raw).map_err(|e| e.to_string())?;
    let compressed = z.finish().map_err(|e| e.to_string())?;
    write_chunk(&mut out, b"IDAT", &compressed);
    write_chunk(&mut out, b"IEND", &[]);
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::icons::image_probe;

    #[test]
    fn encodes_a_png_the_probe_accepts() {
        let pixels = vec![200u8; 4 * 3 * 4];
        let png = encode_bgra(4, 3, &pixels, false).unwrap();
        let info = image_probe::probe_bytes(&png).unwrap();
        assert_eq!((info.width, info.height), (4, 3));
        assert!(encode_bgra(0, 3, &pixels, false).is_err());
    }
}
