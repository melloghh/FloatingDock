//! Name normalisation used to connect an app (exe name, AUMID parts, display name, shortcut name, aliases) with a file or
//! manifest key of an icon pack. Rules are applied in order; the first rule that finds an entry wins.

use std::collections::HashSet;

use unicode_normalization::{char::is_combining_mark, UnicodeNormalization};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MatchRule {
    None,
    Exact,
    WithoutNoise,
    SamePronunciation,
    Contains,
    OneLetterDifference,
    Override,
}

const NOISE_WORDS: [&str; 24] = [
    "adobe", "microsoft", "google", "app", "apps", "desktop", "pro", "cc", "cs", "x64", "x86", "win", "windows10", "windows11", "for",
    "the", "inc", "ltd", "official", "icon", "flat", "dark", "light", "mask",
];

const GENERIC_NAMES: [&str; 18] = [
    "app", "apps", "desktop", "windows", "file", "files", "launcher", "setup", "update", "updater", "installer", "helper", "host",
    "service", "client", "main", "default", "program",
];

fn aliases_of(norm: &str) -> &'static [&'static str] {
    match norm {
        "msedge" => &["edge", "microsoft edge"],
        "edge" => &["msedge"],
        "calculatorapp" => &["calculator", "calc"],
        "calculator" => &["calculatorapp", "calc"],
        "calc" => &["calculator"],
        "windowsterminal" => &["terminal", "wt"],
        "wt" => &["terminal", "windows terminal"],
        "code" => &["visual studio code", "vscode"],
        "explorer" => &["file explorer", "files"],
        "winword" => &["word"],
        "excel" => &["microsoft excel"],
        "powerpnt" => &["powerpoint"],
        "outlook" => &["microsoft outlook"],
        "ms-teams" => &["teams", "msteams"],
        "msteams" => &["teams"],
        "notepad" => &["blocnotes"],
        "mspaint" => &["paint"],
        "systemsettings" => &["settings"],
        "photoshop" => &["adobe photoshop"],
        "afterfx" => &["after effects", "adobe after effects"],
        "discord" => &["discordptb", "discordcanary"],
        "chrome" => &["google chrome"],
        "firefox" => &["mozilla firefox"],
        "steamwebhelper" => &["steam"],
        _ => &[],
    }
}

fn is_known_extension(ext: &str) -> bool {
    matches!(ext.to_lowercase().as_str(), ".png" | ".ico" | ".exe" | ".lnk" | ".jpg" | ".jpeg" | ".bmp" | ".gif" | ".webp")
}

pub fn strip_extension(name: &str) -> &str {
    if let Some(dot) = name.rfind('.') {
        let ext = &name[dot..];
        let ext_ok = ext.chars().count() >= 2 && ext.chars().count() <= 5 && ext.chars().skip(1).all(char::is_alphanumeric) && !ext.contains(['\\', '/']);
        if ext_ok && is_known_extension(ext) {
            return &name[..dot];
        }
    }
    name
}

/// Lower-case letters and digits only: "Ms-Teams.png" -> "msteams".
pub fn normalize(name: &str) -> String {
    let mut out = String::with_capacity(name.len());
    for ch in strip_extension(name).nfkd() {
        if is_combining_mark(ch) {
            continue;
        }
        if ch.is_alphanumeric() {
            out.extend(ch.to_lowercase());
        }
    }
    out
}

fn tokenize(s: &str) -> Vec<String> {
    let mut res = Vec::new();
    let mut cur = String::new();
    let mut prev = '\0';
    for ch in s.nfkd() {
        if is_combining_mark(ch) {
            continue;
        }
        if !ch.is_alphanumeric() {
            if !cur.is_empty() {
                res.push(std::mem::take(&mut cur));
            }
            prev = '\0';
            continue;
        }
        // camelCase boundary: "AfterEffects" -> after, effects ; letter/digit boundary: "cc2024"
        if !cur.is_empty() && ((ch.is_uppercase() && prev.is_lowercase()) || (ch.is_ascii_digit() != prev.is_ascii_digit())) {
            res.push(std::mem::take(&mut cur));
        }
        cur.extend(ch.to_lowercase());
        prev = ch;
    }
    if !cur.is_empty() {
        res.push(cur);
    }
    res
}

/// Removes vendor / edition words, versions and years: "Adobe Photoshop 2024" -> "photoshop".
pub fn without_noise(name: &str) -> String {
    let kept: String = tokenize(strip_extension(name))
        .into_iter()
        .filter(|t| {
            if NOISE_WORDS.contains(&t.as_str()) {
                return false;
            }
            if t.chars().all(|c| c.is_ascii_digit()) {
                return false; // 2024, 11
            }
            if t.len() > 1 && t.starts_with('v') && t.chars().skip(1).all(|c| c.is_ascii_digit() || c == '.') {
                return false; // v2
            }
            true
        })
        .collect();
    if kept.is_empty() { normalize(name) } else { kept }
}

fn cyr(c: char) -> Option<&'static str> {
    Some(match c {
        'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d", 'е' => "e", 'ё' => "e", 'ж' => "zh", 'з' => "z", 'и' => "i", 'й' => "i",
        'к' => "k", 'л' => "l", 'м' => "m", 'н' => "n", 'о' => "o", 'п' => "p", 'р' => "r", 'с' => "s", 'т' => "t", 'у' => "u", 'ф' => "f",
        'х' => "h", 'ц' => "ts", 'ч' => "ch", 'ш' => "sh", 'щ' => "sh", 'ъ' => "", 'ы' => "i", 'ь' => "", 'э' => "e", 'ю' => "iu", 'я' => "ia",
        'і' => "i", 'ї' => "i", 'є' => "e", 'ґ' => "g",
        _ => return None,
    })
}

pub fn has_cyrillic(s: &str) -> bool {
    s.chars().any(|c| ('\u{0400}'..='\u{04FF}').contains(&c))
}

/// Rough phonetic key shared by Latin and Cyrillic spellings: "Photoshop", "Фотошоп" -> "fotoshop".
pub fn phonetic(name: &str) -> String {
    let s = without_noise(name).to_lowercase();
    let mut t = String::new();
    for ch in s.chars() {
        match cyr(ch) {
            Some(l) => t.push_str(l),
            None => t.push(ch),
        }
    }
    let t = t.replace("ph", "f").replace("ck", "k").replace("kh", "h").replace('q', "k").replace('x', "ks").replace('w', "v").replace('y', "i").replace('j', "i");
    let chars: Vec<char> = t.chars().collect();
    let mut out = String::new();
    for (i, &c0) in chars.iter().enumerate() {
        let mut c = c0;
        if c == 'c' {
            c = if matches!(chars.get(i + 1), Some('e') | Some('i')) { 's' } else { 'k' };
        }
        if out.chars().last() == Some(c) {
            continue; // double letters
        }
        out.push(c);
    }
    out
}

pub fn is_too_generic(normalized: &str) -> bool {
    normalized.chars().count() < 3 || GENERIC_NAMES.iter().any(|g| g.eq_ignore_ascii_case(normalized))
}

/// The names plus the well-known real-name aliases of each, de-duplicated (case-insensitive).
pub fn expand<'a>(names: impl IntoIterator<Item = &'a str>) -> Vec<String> {
    let mut seen: HashSet<String> = HashSet::new();
    let mut out = Vec::new();
    for n in names {
        if n.trim().is_empty() || !seen.insert(n.to_lowercase()) {
            continue;
        }
        out.push(n.to_string());
        for a in aliases_of(&normalize(n)) {
            if seen.insert(a.to_lowercase()) {
                out.push((*a).to_string());
            }
        }
    }
    out
}

pub fn contains_match(a: &str, b: &str) -> bool {
    if a.is_empty() || b.is_empty() {
        return false;
    }
    let (shorter, longer) = if a.chars().count() <= b.chars().count() { (a, b) } else { (b, a) };
    let (sl, ll) = (shorter.chars().count(), longer.chars().count());
    sl >= 4 && sl * 2 >= ll && longer.contains(shorter)
}

pub fn one_letter_apart(a: &str, b: &str) -> bool {
    let (a, b): (Vec<char>, Vec<char>) = (a.chars().collect(), b.chars().collect());
    if a.len() < 5 || b.len() < 5 || a.len().abs_diff(b.len()) > 1 {
        return false;
    }
    let (mut i, mut j, mut diff) = (0usize, 0usize, 0usize);
    while i < a.len() && j < b.len() {
        if a[i] == b[j] {
            i += 1;
            j += 1;
            continue;
        }
        diff += 1;
        if diff > 1 {
            return false;
        }
        if a.len() == b.len() {
            i += 1;
            j += 1;
        } else if a.len() > b.len() {
            i += 1;
        } else {
            j += 1;
        }
    }
    diff += (a.len() - i) + (b.len() - j);
    diff <= 1
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_and_strips() {
        assert_eq!(normalize("Ms-Teams.png"), "msteams");
        assert_eq!(normalize("Café App"), "cafeapp");
        assert_eq!(without_noise("Adobe Photoshop 2024"), "photoshop");
        assert_eq!(without_noise("AfterEffects 2024"), "aftereffects");
        assert_eq!(without_noise("Adobe"), "adobe");
    }

    #[test]
    fn phonetics_cross_alphabets() {
        assert_eq!(phonetic("Photoshop"), phonetic("Фотошоп"));
        assert_eq!(phonetic("Discord"), "diskord");
    }

    #[test]
    fn matching_helpers() {
        assert!(one_letter_apart("telegram", "telegran"));
        assert!(!one_letter_apart("telegram", "telegrxx"));
        assert!(contains_match("photoshop", "photo"));
        assert!(!contains_match("ab", "abc"));
        assert!(is_too_generic("app") && is_too_generic("ab") && !is_too_generic("steam"));
        assert_eq!(expand(["msedge", "Edge"]), ["msedge", "edge", "microsoft edge"]);
    }
}
