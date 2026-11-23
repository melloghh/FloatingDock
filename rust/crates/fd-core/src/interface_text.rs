//! Explicit product strings. Application names, paths and OS-provided names are never translated.
//! The Russian table is extracted from the C# source by `rust/tools/gen_texts.py` into `texts_ru.json`.

use std::collections::HashMap;
use std::sync::OnceLock;

use crate::settings::InterfaceLanguage;

struct Tables {
    /// English -> Russian in the order of the original dictionary (first position, last value).
    #[allow(dead_code)]
    russian: Vec<(String, String)>,
    /// Russian -> English: the first English text of every Russian text.
    english: HashMap<String, String>,
    /// Case-insensitive English -> Russian lookup: the first pair of every case-folded key.
    aliases: HashMap<String, String>,
    /// The `aliases` pairs in order (what the UI receives as the table).
    alias_pairs: Vec<(String, String)>,
}

fn tables() -> &'static Tables {
    static T: OnceLock<Tables> = OnceLock::new();
    T.get_or_init(|| {
        let russian: Vec<(String, String)> =
            serde_json::from_str(include_str!("texts_ru.json")).expect("texts_ru.json is valid");
        let mut english = HashMap::new();
        for (en, ru) in &russian {
            english.entry(ru.clone()).or_insert_with(|| en.clone());
        }
        let mut aliases = HashMap::new();
        let mut alias_pairs = Vec::new();
        for (en, ru) in &russian {
            // the first pair of a case-folded group wins
            if let std::collections::hash_map::Entry::Vacant(slot) = aliases.entry(en.to_lowercase()) {
                slot.insert(ru.clone());
                alias_pairs.push((en.clone(), ru.clone()));
            }
        }
        Tables { russian, english, aliases, alias_pairs }
    })
}

/// English text to the text of `language` (empty for English), for UIs that translate on their own side.
pub fn table(language: InterfaceLanguage) -> Vec<(String, String)> {
    match language {
        InterfaceLanguage::Russian => tables().alias_pairs.clone(),
        InterfaceLanguage::English => Vec::new(),
    }
}

/// `text` translated for `language`; a Russian text is first mapped back to its English key.
pub fn get(text: &str, language: InterfaceLanguage) -> String {
    let t = tables();
    let key = t.english.get(text).map(String::as_str).unwrap_or(text);
    match language {
        InterfaceLanguage::Russian => t.aliases.get(&key.to_lowercase()).cloned().unwrap_or_else(|| key.to_string()),
        InterfaceLanguage::English => key.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn translates_both_ways() {
        assert_eq!(get("Restart", InterfaceLanguage::Russian), "Перезагрузить");
        assert_eq!(get("Перезагрузить", InterfaceLanguage::English), "Restart");
        assert_eq!(get("Unknown text", InterfaceLanguage::Russian), "Unknown text");
        assert!(table(InterfaceLanguage::English).is_empty());
        assert!(table(InterfaceLanguage::Russian).len() > 300);
        assert!(!tables().russian.is_empty());
    }
}
