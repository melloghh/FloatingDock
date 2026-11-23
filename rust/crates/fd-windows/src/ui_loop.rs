//! The engine's "UI thread": a plain Win32 message loop. WinEvent hooks and the shell services need a thread that pumps
//! messages and the running-apps service wants all its state touched from one thread, so IPC handlers hop onto this one.

use std::collections::VecDeque;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Arc, Mutex};

use tokio::sync::oneshot;
use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::UI::WindowsAndMessaging::{
    DispatchMessageW, GetMessageW, PeekMessageW, PostThreadMessageW, TranslateMessage, MSG, PM_NOREMOVE, WM_APP, WM_QUIT,
};

type Job = Box<dyn FnOnce() + Send>;

#[derive(Default)]
pub struct UiLoop {
    queue: Mutex<VecDeque<Job>>,
    thread_id: AtomicU32,
}

impl UiLoop {
    pub fn new() -> Arc<UiLoop> {
        Arc::new(UiLoop::default())
    }

    /// Runs `job` on the loop thread (soon). Safe to call from any thread, also before [`run`](Self::run) started.
    pub fn post(&self, job: impl FnOnce() + Send + 'static) {
        self.queue.lock().unwrap().push_back(Box::new(job));
        let id = self.thread_id.load(Ordering::Acquire);
        if id != 0 {
            unsafe {
                let _ = PostThreadMessageW(id, WM_APP, WPARAM(0), LPARAM(0));
            }
        }
    }

    /// Runs `job` on the loop thread and gives its result back.
    pub fn invoke<T: Send + 'static>(&self, job: impl FnOnce() -> T + Send + 'static) -> oneshot::Receiver<T> {
        let (tx, rx) = oneshot::channel();
        self.post(move || {
            let _ = tx.send(job());
        });
        rx
    }

    pub fn quit(&self) {
        let id = self.thread_id.load(Ordering::Acquire);
        if id != 0 {
            unsafe {
                let _ = PostThreadMessageW(id, WM_QUIT, WPARAM(0), LPARAM(0));
            }
        }
    }

    /// Runs until [`quit`](Self::quit). Must be called on the thread that owns the hooks and windows.
    pub fn run(&self) {
        unsafe {
            let mut msg = MSG::default();
            let _ = PeekMessageW(&mut msg, None, 0, 0, PM_NOREMOVE); // creates this thread's message queue
            self.thread_id.store(GetCurrentThreadId(), Ordering::Release);
            self.drain();
            while GetMessageW(&mut msg, None, 0, 0).as_bool() {
                if msg.message == WM_APP && msg.hwnd == HWND::default() {
                    self.drain();
                    continue;
                }
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
    }

    fn drain(&self) {
        loop {
            let job = self.queue.lock().unwrap().pop_front();
            let Some(job) = job else { break };
            // A failing job must not stop the loop.
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(job));
        }
    }
}
