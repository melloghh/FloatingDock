//! FloatingDock core: settings, app lists, identity matching, text tables. Pure logic, no Win32.
//! A port of `FloatingDock.Core` (C#); the stored JSON files stay byte-compatible with the C# engine.

pub mod app_list_store;
pub mod app_ref;
pub mod dock_entry_reconciler;
pub mod event;
pub mod hotkey;
pub mod icons;
pub mod interface_text;
pub mod json_file;
pub mod lenient_json;
pub mod paths;
pub mod search;
pub mod taskbar;
pub mod settings;
pub mod settings_store;
pub mod win_path;
pub mod window_filter_rules;
pub mod window_info;

pub use app_list_store::AppListStore;
pub use app_ref::{AppRef, AppSource};
pub use event::Event;
pub use paths::AppPaths;
pub use settings::{DockSettings, SettingsPatchError};
pub use settings_store::SettingsStore;
pub use window_info::{AppMatching, WindowInfo};

/// A string-valued enum that serializes by name, reads names case-insensitively and also accepts the numeric index
/// (what `JsonStringEnumConverter` of the C# engine did), so existing `config.json` files keep working.
#[macro_export]
macro_rules! string_enum {
    ($(#[$m:meta])* $vis:vis enum $name:ident { $($var:ident),+ $(,)? }) => {
        $(#[$m])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
        $vis enum $name { $($var),+ }
        impl $name {
            pub const ALL: &'static [$name] = &[$($name::$var),+];
            pub fn as_str(self) -> &'static str { match self { $($name::$var => stringify!($var)),+ } }
            pub fn parse(text: &str) -> Option<Self> {
                $( if text.eq_ignore_ascii_case(stringify!($var)) { return Some($name::$var); } )+
                None
            }
        }
        impl ::serde::Serialize for $name {
            fn serialize<S: ::serde::Serializer>(&self, s: S) -> Result<S::Ok, S::Error> { s.serialize_str(self.as_str()) }
        }
        impl<'de> ::serde::Deserialize<'de> for $name {
            fn deserialize<D: ::serde::Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
                use ::serde::de::Error;
                let v = ::serde_json::Value::deserialize(d)?;
                match &v {
                    ::serde_json::Value::String(s) => Self::parse(s)
                        .ok_or_else(|| D::Error::custom(format!("'{}' is not a valid {}", s, stringify!($name)))),
                    ::serde_json::Value::Number(n) => n.as_u64().and_then(|i| Self::ALL.get(i as usize).copied())
                        .ok_or_else(|| D::Error::custom(format!("{} is not a valid {}", n, stringify!($name)))),
                    _ => Err(D::Error::custom(format!("{} must be a string", stringify!($name)))),
                }
            }
        }
    };
}
