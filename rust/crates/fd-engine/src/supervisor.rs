//! Starts the Flutter UI process and keeps it alive: if it crashes it is started again (not more than a few times a minute,
//! so a UI that cannot start does not spin the machine). The engine never depends on it: without a UI it is just quiet.

use std::path::{Path, PathBuf};
use std::process::{Child, Command};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use crate::log;

pub struct UiSupervisor {
    exe: PathBuf,
    endpoint: Box<dyn Fn() -> String + Send + Sync>,
    open_settings: bool,
    starts: Mutex<Vec<Instant>>,
    child: Mutex<Option<Child>>,
    stopping: AtomicBool,
}

/// The UI next to the engine: `ui\fd_ui.exe`, or the path given with `--ui`. `None` when there is none.
pub fn find(args: &[String]) -> Option<PathBuf> {
    if let Some(i) = args.iter().position(|a| a == "--ui") {
        let p = args.get(i + 1).map(Path::new)?;
        return p.is_file().then(|| p.canonicalize().unwrap_or_else(|_| p.to_path_buf()));
    }
    let local = std::env::current_exe().ok()?.parent()?.join("ui").join("fd_ui.exe");
    local.is_file().then_some(local)
}

impl UiSupervisor {
    pub fn new(exe: PathBuf, endpoint: impl Fn() -> String + Send + Sync + 'static, open_settings: bool) -> Arc<Self> {
        Arc::new(Self {
            exe,
            endpoint: Box::new(endpoint),
            open_settings,
            starts: Mutex::new(Vec::new()),
            child: Mutex::new(None),
            stopping: AtomicBool::new(false),
        })
    }

    pub fn start(self: &Arc<Self>) {
        if self.stopping.load(Ordering::SeqCst) {
            return;
        }
        let first = {
            let mut starts = self.starts.lock().unwrap();
            starts.retain(|t| t.elapsed() <= Duration::from_secs(60));
            if starts.len() >= 4 {
                log::error("the UI keeps stopping; it will not be started again until the engine restarts");
                return;
            }
            let first = starts.is_empty();
            starts.push(Instant::now());
            first
        };
        let mut cmd = Command::new(&self.exe);
        if let Some(dir) = self.exe.parent() {
            cmd.current_dir(dir);
        }
        cmd.arg("--socket").arg((self.endpoint)());
        if self.open_settings && first {
            cmd.arg("--open-settings");
        }
        match cmd.spawn() {
            Ok(child) => {
                let pid = child.id();
                *self.child.lock().unwrap() = Some(child);
                log::info(format!("UI started: {} (pid {pid})", self.exe.display()));
                let me = self.clone();
                thread::spawn(move || me.watch(pid));
            }
            Err(e) => log::error(format!("the UI could not be started: {e}")),
        }
    }

    /// Waits for the process `pid` to end and starts it again unless the engine is stopping.
    fn watch(self: Arc<Self>, pid: u32) {
        loop {
            thread::sleep(Duration::from_millis(250));
            let status = {
                let mut guard = self.child.lock().unwrap();
                match guard.as_mut() {
                    Some(c) if c.id() == pid => match c.try_wait() {
                        Ok(Some(s)) => Some(s.code().map_or("?".to_string(), |c| c.to_string())),
                        Ok(None) => None,
                        Err(_) => Some("?".into()),
                    },
                    _ => return, // replaced or stopped
                }
            };
            if let Some(code) = status {
                if self.stopping.load(Ordering::SeqCst) {
                    return;
                }
                log::warn(format!("the UI stopped (exit code {code}); starting it again"));
                thread::sleep(Duration::from_millis(500));
                self.start();
                return;
            }
        }
    }

    /// Gives the UI a moment to leave by itself (it does, on `engine.stopping`), then ends it.
    pub fn stop(&self) {
        self.stopping.store(true, Ordering::SeqCst);
        let child = self.child.lock().unwrap().take();
        let Some(mut child) = child else { return };
        let deadline = Instant::now() + Duration::from_millis(2500);
        while Instant::now() < deadline {
            if matches!(child.try_wait(), Ok(Some(_))) {
                return;
            }
            thread::sleep(Duration::from_millis(50));
        }
        let _ = child.kill();
        let _ = child.wait();
    }
}
