//! The FloatingDock program: `FloatingDock.Engine.exe` runs the engine and, next to it, the Flutter UI. Started a second time
//! it asks the running one to open the settings. Other forms:
//!
//! * `--quit` stops the running engine and waits for it;
//! * `--restore-taskbar [--quiet]` puts the Windows taskbar back without starting anything;
//! * `--settings` opens the settings window at start; `--enable-autostart` turns on "Start with Windows" (the installer);
//! * `--ui <exe>` uses another UI; `--no-ui` runs the engine alone; `--classic <exe>` where the WinUI application is;
//! * `--diagnostic-profile <absolute dir>` works on a copy of the profile and never touches the desktop.
#![windows_subsystem = "windows"]

mod bar;
mod capture;
mod dock;
mod engine;
mod handlers;
mod icons;
mod launchpad;
mod log;
mod media;
mod settings_apply;
mod supervisor;
mod system;

use std::path::{Path, PathBuf};
use std::process::{Command, ExitCode};
use std::sync::Arc;
use std::time::{Duration, Instant};

use fd_core::{AppPaths, SettingsStore};
use fd_ipc::{endpoint, IpcClient};
use fd_windows::UiLoop;
use sha2::{Digest, Sha256};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{GetLastError, ERROR_ALREADY_EXISTS};
use windows::Win32::System::Threading::CreateMutexW;

use crate::engine::Engine;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match real_main(&args) {
        Ok(code) => ExitCode::from(code),
        Err(message) => {
            log::error(format!("engine could not start: {message}"));
            ExitCode::from(1)
        }
    }
}

fn real_main(args: &[String]) -> Result<u8, String> {
    let mut paths = AppPaths::default_location();
    let profile = args.iter().position(|a| a == "--diagnostic-profile");
    if let Some(i) = profile {
        let dir = args.get(i + 1).ok_or("--diagnostic-profile requires an absolute path.")?;
        let dir = Path::new(dir);
        if !dir.is_absolute() {
            return Err("--diagnostic-profile requires an absolute path.".into());
        }
        let root = PathBuf::from(dir.to_string_lossy().trim_end_matches(['\\', '/']));
        if root.to_string_lossy().eq_ignore_ascii_case(&AppPaths::default_location().root.to_string_lossy()) {
            return Err("Use a separate copy of the user profile for diagnostics.".into());
        }
        paths = AppPaths::new(root);
    }
    if args.iter().any(|a| a == "--restore-taskbar") {
        return Ok(restore_taskbar(&paths, args.iter().any(|a| a == "--quiet")));
    }
    if args.iter().any(|a| a == "--quit") {
        return Ok(quit(&paths));
    }
    run(paths, args, profile.is_some())
}

fn run(paths: AppPaths, args: &[String], diagnostic: bool) -> Result<u8, String> {
    // Used by "Restart": wait until the previous process is gone so the single-instance mutex is free.
    if let Some(i) = args.iter().position(|a| a == "--wait-for-exit") {
        if let Some(pid) = args.get(i + 1).and_then(|p| p.parse::<u32>().ok()) {
            wait_for_exit(pid, Duration::from_secs(10));
        }
    }
    log::open(&paths.logs_dir());

    // The interface is a setting: "Classic" hands over to the WinUI application, which reads the same profile.
    if !diagnostic && SettingsStore::load(&paths.config_file()).ui_mode == "Classic" && start_classic(args) {
        log::info("UiMode is Classic: started the WinUI application and left");
        return Ok(0);
    }

    // Same mutex name as the C# engine: the two can never run on one profile at once.
    let hash = hex::encode_upper(Sha256::digest(paths.root.to_string_lossy().to_uppercase().as_bytes()));
    let name: Vec<u16> = format!("Local\\FloatingDock.Engine.{}", &hash[..16]).encode_utf16().chain(Some(0)).collect();
    let _single = unsafe {
        let handle = CreateMutexW(None, true, PCWSTR(name.as_ptr())).map_err(|e| e.to_string())?;
        if GetLastError() == ERROR_ALREADY_EXISTS {
            // Started again: the running one opens its settings (that is what a second launch has always meant).
            match endpoint::read(&paths.root).ok_or("no endpoint file".to_string()).and_then(|ep| {
                let mut client = IpcClient::connect(&ep, "second-launch", Duration::from_secs(5))?;
                client.request("app.activate", serde_json::json!({}), Duration::from_secs(5))
            }) {
                Ok(_) => {}
                Err(e) => log::info(format!("another engine already serves this profile ({e})")),
            }
            return Ok(0);
        }
        handle
    };

    std::panic::set_hook(Box::new(|info| log::error(format!("panic: {info}"))));

    let ui = UiLoop::new();
    let engine_cell: Arc<std::sync::Mutex<Option<Arc<Engine>>>> = Arc::default();
    let sup_cell: Arc<std::sync::Mutex<Option<Arc<supervisor::UiSupervisor>>>> = Arc::default();
    let no_ui = args.iter().any(|a| a == "--no-ui");
    let settings_flag = args.iter().any(|a| a == "--settings");

    {
        let (ui2, engine_cell, sup_cell) = (ui.clone(), engine_cell.clone(), sup_cell.clone());
        let (paths, args) = (paths.clone(), args.to_vec());
        ui.post(move || {
            let engine = Engine::start(paths.clone(), ui2.clone(), args.clone(), diagnostic);
            let ui_exe = if no_ui { None } else { supervisor::find(&args) };
            let supervisor = match ui_exe {
                Some(exe) => {
                    let root = paths.root.clone();
                    Some(supervisor::UiSupervisor::new(exe, move || endpoint::read(&root).unwrap_or_default(), settings_flag))
                }
                None => {
                    if !diagnostic && !no_ui {
                        log::warn("no UI found next to the engine (ui\\fd_ui.exe); running without one");
                    }
                    None
                }
            };
            let (sup2, ui3) = (supervisor.clone(), ui2.clone());
            engine.quit_requested.subscribe(move |_| {
                if let Some(s) = &sup2 {
                    s.stop();
                }
                ui3.quit();
            });
            if let Some(s) = &supervisor {
                s.start();
            }
            *sup_cell.lock().unwrap() = supervisor;
            *engine_cell.lock().unwrap() = Some(engine);
        });
    }
    ui.run();

    if let Some(s) = sup_cell.lock().unwrap().take() {
        s.stop();
    }
    if let Some(e) = engine_cell.lock().unwrap().take() {
        e.dispose();
    }
    Ok(0)
}

fn wait_for_exit(pid: u32, timeout: Duration) {
    use windows::Win32::System::Threading::{OpenProcess, WaitForSingleObject, PROCESS_SYNCHRONIZE};
    unsafe {
        if let Ok(h) = OpenProcess(PROCESS_SYNCHRONIZE, false, pid) {
            WaitForSingleObject(h, timeout.as_millis() as u32);
            let _ = windows::Win32::Foundation::CloseHandle(h);
        }
    }
}

/// The WinUI application: `classic\FloatingDock.exe` next to the engine, or the path given with `--classic`.
fn start_classic(args: &[String]) -> bool {
    let exe = match args.iter().position(|a| a == "--classic").and_then(|i| args.get(i + 1)) {
        Some(p) => PathBuf::from(p),
        None => match std::env::current_exe().ok().and_then(|e| e.parent().map(|d| d.join("classic").join("FloatingDock.exe"))) {
            Some(p) => p,
            None => return false,
        },
    };
    if !exe.is_file() {
        log::warn(format!("UiMode is Classic but {} does not exist; staying with the new interface", exe.display()));
        return false;
    }
    let mut cmd = Command::new(&exe);
    if let Some(dir) = exe.parent() {
        cmd.current_dir(dir);
    }
    cmd.args(args.iter().filter(|a| *a == "--settings" || *a == "--enable-autostart"));
    match cmd.spawn() {
        Ok(_) => true,
        Err(e) => {
            log::error(format!("the WinUI application could not be started: {e}"));
            false
        }
    }
}

fn restore_taskbar(paths: &AppPaths, quiet: bool) -> u8 {
    use fd_core::taskbar::TaskbarOutcome;
    log::open(&paths.logs_dir());
    let taskbar = system::new_taskbar_coordinator(paths);
    let r = fd_windows::restore_flow::run(&taskbar, false, !quiet);
    log::info(format!("--restore-taskbar: {:?} - {}", r.outcome, r.message));
    if !quiet && r.outcome != TaskbarOutcome::NothingToDo {
        if r.success() {
            fd_windows::tray::info_box(&r.message);
        } else {
            fd_windows::tray::error_box(&r.message);
        }
    }
    if r.success() {
        0
    } else if r.outcome == TaskbarOutcome::NeedsConfirmation {
        2
    } else {
        1
    }
}

fn quit(paths: &AppPaths) -> u8 {
    let Some(ep) = endpoint::read(&paths.root) else { return 0 }; // no engine is running
    let sent = IpcClient::connect(&ep, "engine-quit", Duration::from_secs(5))
        .and_then(|mut c| c.request("engine.quit", serde_json::json!({}), Duration::from_secs(5)));
    if sent.is_err() {
        return 0; // nothing is listening: not running
    }
    let deadline = Instant::now() + Duration::from_secs(15);
    while Instant::now() < deadline {
        if endpoint::read(&paths.root).is_none() {
            return 0; // the engine removes its endpoint file when it is gone
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    1
}
