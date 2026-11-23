//! One dedicated STA thread for every Shell / COM query (icon extraction, AppsFolder, property stores, shortcut resolving).
//! Keeps the UI thread free and gives the shell the apartment model it expects. Work items run strictly in order.

use std::sync::mpsc::{self, Sender};
use std::sync::Mutex;
use std::thread::{self, JoinHandle};
use std::time::Duration;

use tokio::sync::oneshot;
use windows::Win32::System::Com::{CoInitializeEx, CoUninitialize, COINIT_APARTMENTTHREADED};

type Job = Box<dyn FnOnce() + Send>;

pub struct ShellWorker {
    tx: Mutex<Option<Sender<Job>>>,
    thread: Mutex<Option<JoinHandle<()>>>,
}

impl ShellWorker {
    pub fn new() -> ShellWorker {
        let (tx, rx) = mpsc::channel::<Job>();
        let thread = thread::Builder::new()
            .name("FloatingDock.ShellWorker".into())
            .spawn(move || {
                unsafe {
                    let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
                }
                while let Ok(job) = rx.recv() {
                    // individual items report through their channel; a failing one must not stop the worker
                    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(job));
                }
                unsafe { CoUninitialize() };
            })
            .expect("shell worker thread");
        ShellWorker { tx: Mutex::new(Some(tx)), thread: Mutex::new(Some(thread)) }
    }

    /// Runs `f` on the shell thread. The receiver errors when the worker is disposed or `f` panicked.
    pub fn run<T: Send + 'static>(&self, f: impl FnOnce() -> T + Send + 'static) -> oneshot::Receiver<T> {
        let (otx, orx) = oneshot::channel();
        if let Some(tx) = self.tx.lock().unwrap().as_ref() {
            let _ = tx.send(Box::new(move || {
                let _ = otx.send(f());
            }));
        }
        orx
    }

    pub fn dispose(&self) {
        self.tx.lock().unwrap().take();
        if let Some(t) = self.thread.lock().unwrap().take() {
            if t.thread().id() != thread::current().id() {
                // give the worker a moment to finish what it is doing
                let start = std::time::Instant::now();
                while !t.is_finished() && start.elapsed() < Duration::from_secs(2) {
                    thread::sleep(Duration::from_millis(20));
                }
                if t.is_finished() {
                    let _ = t.join();
                }
            }
        }
    }
}

impl Default for ShellWorker {
    fn default() -> Self {
        Self::new()
    }
}

impl Drop for ShellWorker {
    fn drop(&mut self) {
        self.dispose();
    }
}
