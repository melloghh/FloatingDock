//! Settings -> system: sliders fire many changes per second, so they are applied at most once per ~30 ms, always with the
//! latest values, and only what changed.

use std::sync::Arc;
use std::time::Duration;

use fd_core::DockSettings;

use crate::engine::Engine;

impl Engine {
    pub fn schedule_settings_apply(&self) {
        if let Some(t) = self.settings_timer.get() {
            t.once(Duration::from_millis(30));
        }
    }

    pub fn apply_pending_settings(self: &Arc<Self>) {
        if self.shutting_down.load(std::sync::atomic::Ordering::SeqCst) {
            return;
        }
        let next = self.settings.current();
        let old = std::mem::replace(&mut *self.applied.lock().unwrap(), (*next).clone());
        self.apply_system_settings(Some(&old), &next);
    }

    pub fn apply_system_settings(self: &Arc<Self>, old: Option<&DockSettings>, s: &DockSettings) {
        let icons_changed = old.map_or(true, |o| {
            o.use_icon_pack != s.use_icon_pack || o.active_icon_pack != s.active_icon_pack || o.prefer_dark_variants != s.prefer_dark_variants
        });
        if icons_changed {
            self.resolver.configure(s.use_icon_pack, &s.active_icon_pack, s.prefer_dark_variants);
            if old.is_some() {
                self.invalidate_icons();
            }
        }
        if old.is_some_and(|o| o.launchpad_icon_style != s.launchpad_icon_style) {
            self.invalidate_icons();
        }
        self.apply_system_settings_part(old, s);
    }
}
