//! Win32 integration of the FloatingDock engine: the message loop, tray, hotkeys, windows, taskbar, shell, media.
//! Windows only; every other crate of the workspace is platform neutral.

pub mod activator;
pub mod capture;
pub mod catalog;
pub mod display;
pub mod icon_extractor;
pub mod identity;
pub mod launcher;
pub mod media;
pub mod message_window;
pub mod native;
pub mod restore_flow;
pub mod running_apps;
pub mod shell;
pub mod shell_animation;
pub mod shell_worker;
pub mod status;
pub mod tile_import;
pub mod taskbar_win32;
pub mod snapshot;
pub mod timer;
pub mod tray;
pub mod tray_items;
pub mod ui_loop;
pub mod win_event;
pub mod wifi;
pub mod window_filter;

pub use running_apps::RunningApps;
pub use shell_worker::ShellWorker;
pub use timer::UiTimer;
pub use ui_loop::UiLoop;
pub use win_event::{WinEventWatcher, WindowEventKind};
