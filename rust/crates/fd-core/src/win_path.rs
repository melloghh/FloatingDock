//! Windows-style path helpers that behave identically on every OS (paths coming from Windows APIs use `\` or `/`).

pub fn file_name(path: &str) -> &str {
    match path.rfind(['\\', '/']) {
        Some(i) => &path[i + 1..],
        None => path,
    }
}

pub fn extension(path: &str) -> &str {
    let f = file_name(path);
    match f.rfind('.') {
        Some(i) if i > 0 => &f[i..],
        _ => "",
    }
}

pub fn file_name_without_extension(path: &str) -> &str {
    let f = file_name(path);
    match f.rfind('.') {
        Some(i) if i > 0 => &f[..i],
        _ => f,
    }
}

pub fn directory(path: &str) -> Option<&str> {
    match path.rfind(['\\', '/']) {
        Some(i) if i > 0 => Some(&path[..i]),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_paths() {
        assert_eq!(file_name(r"C:\a\b.exe"), "b.exe");
        assert_eq!(file_name_without_extension("C:/a/b.tar.gz"), "b.tar");
        assert_eq!(extension(r"C:\a\.hidden"), "");
        assert_eq!(directory(r"C:\a\b.exe"), Some(r"C:\a"));
    }
}
