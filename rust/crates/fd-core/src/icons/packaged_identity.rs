//! Legacy WindowsApps overrides survive a package version change, only within the same family and relative executable.

use crate::window_info::AppMatching;

/// `name_version_arch_resource_publisher` of a package folder -> (name, publisher), or `None`.
fn parse_package_folder(folder: &str) -> Option<(String, String)> {
    let parts: Vec<&str> = folder.split('_').collect();
    if parts.len() < 4 {
        return None;
    }
    let version_ok = {
        let segs: Vec<&str> = parts[1].split('.').collect();
        segs.len() == 4 && segs.iter().all(|s| !s.is_empty() && s.chars().all(|c| c.is_ascii_digit()))
    };
    let arch_ok = matches!(parts[2].to_lowercase().as_str(), "x64" | "x86" | "arm64" | "neutral");
    if !version_ok || !arch_ok || parts[0].is_empty() {
        return None;
    }
    let publisher = *parts.last()?;
    if publisher.is_empty() {
        return None;
    }
    Some((parts[0].to_string(), publisher.to_string()))
}

/// The version-independent identity of an `exe:` key inside `\windowsapps\`, or `None`.
pub fn from_exe_key(key: &str) -> Option<String> {
    let rest = key.strip_prefix("exe:").or_else(|| key.strip_prefix("EXE:"))?;
    let path = AppMatching::normalize_path(rest);
    let marker = "\\windowsapps\\";
    let at = path.find(marker)?;
    let start = at + marker.len();
    let end = start + path[start..].find('\\')?;
    if end == path.len() - 1 {
        return None;
    }
    let (name, publisher) = parse_package_folder(&path[start..end])?;
    Some(format!("{}{}_{}{}", &path[..start], name, publisher, &path[end..]))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn package_family_survives_a_version_change() {
        let a = from_exe_key(r"exe:c:\program files\windowsapps\claude_6.1.0.0_x64__pzs8sxrjxfjjc\app\claude.exe");
        let b = from_exe_key(r"exe:c:\program files\windowsapps\claude_7.0.0.0_x64__pzs8sxrjxfjjc\app\claude.exe");
        assert!(a.is_some());
        assert_eq!(a, b);
        assert_eq!(from_exe_key(r"exe:c:\tools\x.exe"), None);
        assert_eq!(from_exe_key("aumid:x"), None);
    }
}
