//! Icons: limits and header probing, name matching, icon packs (folders and ZIPs), per-app overrides, PNG encoding.

pub mod icon_pack;
pub mod image_probe;
pub mod name_matcher;
pub mod override_store;
pub mod pack_service;
pub mod packaged_identity;
pub mod png_writer;

pub use icon_pack::{IconMatch, IconPack};
pub use name_matcher::MatchRule;
pub use override_store::IconOverrideStore;
pub use pack_service::{IconPackError, IconPackService, IconResolver, ZipPreview};
