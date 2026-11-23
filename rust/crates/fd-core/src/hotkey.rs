//! Parses strings like `Ctrl+Alt+Space` into `RegisterHotKey` modifiers and a Windows virtual-key code.

pub const MOD_ALT: u32 = 0x1;
pub const MOD_CONTROL: u32 = 0x2;
pub const MOD_SHIFT: u32 = 0x4;
pub const MOD_WIN: u32 = 0x8;
pub const MOD_NOREPEAT: u32 = 0x4000;

fn named(key: &str) -> Option<u32> {
    Some(match key.to_ascii_lowercase().as_str() {
        "space" => 0x20,
        "enter" | "return" => 0x0D,
        "esc" | "escape" => 0x1B,
        "tab" => 0x09,
        "backspace" => 0x08,
        "left" => 0x25,
        "up" => 0x26,
        "right" => 0x27,
        "down" => 0x28,
        "home" => 0x24,
        "end" => 0x23,
        "pageup" => 0x21,
        "pagedown" => 0x22,
        "insert" => 0x2D,
        "delete" | "del" => 0x2E,
        _ => return None,
    })
}

/// A global hotkey without any modifier would hijack a normal key, so it is refused.
pub fn try_parse(text: &str) -> Option<(u32, u32)> {
    if text.trim().is_empty() {
        return None;
    }
    let (mut modifiers, mut vk, mut have_key) = (0u32, 0u32, false);
    for raw in text.split('+').map(str::trim).filter(|s| !s.is_empty()) {
        match raw.to_ascii_lowercase().as_str() {
            "ctrl" | "control" => {
                modifiers |= MOD_CONTROL;
                continue;
            }
            "alt" => {
                modifiers |= MOD_ALT;
                continue;
            }
            "shift" => {
                modifiers |= MOD_SHIFT;
                continue;
            }
            "win" | "windows" | "super" => {
                modifiers |= MOD_WIN;
                continue;
            }
            _ => {}
        }
        if have_key {
            return None;
        }
        let chars: Vec<char> = raw.chars().collect();
        if let Some(code) = named(raw) {
            vk = code;
        } else if chars.len() == 1 && chars[0].is_ascii_alphanumeric() {
            vk = chars[0].to_ascii_uppercase() as u32;
        } else if (chars.len() == 2 || chars.len() == 3) && matches!(chars[0], 'f' | 'F') {
            match raw[1..].parse::<u32>() {
                Ok(n) if (1..=24).contains(&n) => vk = 0x6F + n,
                _ => return None,
            }
        } else {
            return None;
        }
        have_key = true;
    }
    (have_key && modifiers != 0).then_some((modifiers, vk))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_hotkeys() {
        assert_eq!(try_parse("Ctrl+Alt+Space"), Some((MOD_CONTROL | MOD_ALT, 0x20)));
        assert_eq!(try_parse("ctrl + shift + f12"), Some((MOD_CONTROL | MOD_SHIFT, 0x7B)));
        assert_eq!(try_parse("Win+k"), Some((MOD_WIN, 'K' as u32)));
        assert_eq!(try_parse("Space"), None, "no modifier");
        assert_eq!(try_parse("Ctrl+A+B"), None, "two keys");
        assert_eq!(try_parse("Ctrl+F25"), None);
        assert_eq!(try_parse(""), None);
    }
}
