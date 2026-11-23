//! Where the engine listens: TCP on the loopback interface (a random port) guarded by a random token. An endpoint is the string
//! `tcp:<port>:<token>`; the running engine publishes it in `engine.endpoint` in the profile folder.

use std::fs;
use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4};
use std::path::{Path, PathBuf};

const POINTER_NAME: &str = "engine.endpoint";

pub fn pointer_path(profile_root: &Path) -> PathBuf {
    profile_root.join(POINTER_NAME)
}

/// The endpoint of the engine that is running for this profile, or `None`.
pub fn read(profile_root: &Path) -> Option<String> {
    let text = fs::read_to_string(pointer_path(profile_root)).ok()?;
    let text = text.trim().trim_start_matches('\u{feff}').to_string();
    (!text.is_empty()).then_some(text)
}

pub fn publish(profile_root: &Path, endpoint: &str) {
    let _ = fs::create_dir_all(profile_root);
    let _ = fs::write(pointer_path(profile_root), endpoint);
}

pub fn clear_published(profile_root: &Path) {
    let _ = fs::remove_file(pointer_path(profile_root));
}

pub fn format(port: u16, token: &str) -> String {
    format!("tcp:{port}:{token}")
}

pub fn parse(endpoint: &str) -> Result<(SocketAddr, String), String> {
    let parts: Vec<&str> = endpoint.split(':').collect();
    if parts.len() != 3 || parts[0] != "tcp" {
        return Err(format!("Not an engine endpoint: {endpoint}"));
    }
    let port: u16 = parts[1].parse().map_err(|_| format!("Not an engine endpoint: {endpoint}"))?;
    Ok((SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::LOCALHOST, port)), parts[2].to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_and_parses() {
        let e = format(4242, "abc");
        assert_eq!(e, "tcp:4242:abc");
        let (addr, token) = parse(&e).unwrap();
        assert_eq!(addr.port(), 4242);
        assert_eq!(token, "abc");
        assert!(parse("unix:/x").is_err());
        assert!(parse("tcp:x:y").is_err());
    }
}
