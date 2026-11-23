//! Delayed work for the UI-loop thread: one scheduler thread keeps a queue of deadlines and posts jobs when they are due.
//! [`UiTimer`] is the cancelable, re-armable kind (the C# `System.Threading.Timer` the engine used everywhere).

use std::cmp::Reverse;
use std::collections::BinaryHeap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, OnceLock};
use std::thread;
use std::time::{Duration, Instant};

use crate::ui_loop::UiLoop;

type Job = Box<dyn FnOnce() + Send>;

struct Item {
    due: Instant,
    seq: u64,
    job: Job,
}

impl PartialEq for Item {
    fn eq(&self, o: &Self) -> bool {
        (self.due, self.seq) == (o.due, o.seq)
    }
}
impl Eq for Item {}
impl PartialOrd for Item {
    fn partial_cmp(&self, o: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(o))
    }
}
impl Ord for Item {
    fn cmp(&self, o: &Self) -> std::cmp::Ordering {
        (self.due, self.seq).cmp(&(o.due, o.seq))
    }
}

struct Scheduler {
    heap: Mutex<BinaryHeap<Reverse<Item>>>,
    cv: Condvar,
    seq: AtomicU64,
}

fn scheduler() -> &'static Arc<Scheduler> {
    static S: OnceLock<Arc<Scheduler>> = OnceLock::new();
    S.get_or_init(|| {
        let s = Arc::new(Scheduler { heap: Mutex::new(BinaryHeap::new()), cv: Condvar::new(), seq: AtomicU64::new(0) });
        let worker = s.clone();
        thread::Builder::new().name("fd-timers".into()).spawn(move || worker.run()).expect("timer thread");
        s
    })
}

impl Scheduler {
    fn run(&self) {
        let mut heap = self.heap.lock().unwrap();
        loop {
            let now = Instant::now();
            match heap.peek() {
                None => heap = self.cv.wait(heap).unwrap(),
                Some(Reverse(item)) if item.due > now => {
                    let wait = item.due - now;
                    heap = self.cv.wait_timeout(heap, wait).unwrap().0;
                }
                Some(_) => {
                    let Reverse(item) = heap.pop().unwrap();
                    drop(heap);
                    (item.job)();
                    heap = self.heap.lock().unwrap();
                }
            }
        }
    }

    fn add(&self, delay: Duration, job: Job) {
        let item = Item { due: Instant::now() + delay, seq: self.seq.fetch_add(1, Ordering::Relaxed), job };
        self.heap.lock().unwrap().push(Reverse(item));
        self.cv.notify_one();
    }
}

impl UiLoop {
    /// Runs `job` on the loop thread after `delay`.
    pub fn post_after(self: &Arc<Self>, delay: Duration, job: impl FnOnce() + Send + 'static) {
        let ui = self.clone();
        scheduler().add(delay, Box::new(move || ui.post(job)));
    }
}

/// A timer whose callback runs on the UI-loop thread. `once`/`repeat` re-arm it; `stop` cancels what is pending.
pub struct UiTimer {
    ui: Arc<UiLoop>,
    callback: Arc<dyn Fn() + Send + Sync>,
    generation: Arc<AtomicU64>,
}

impl UiTimer {
    pub fn new(ui: &Arc<UiLoop>, callback: impl Fn() + Send + Sync + 'static) -> UiTimer {
        UiTimer { ui: ui.clone(), callback: Arc::new(callback), generation: Arc::new(AtomicU64::new(0)) }
    }

    /// Fires once after `delay`; replaces anything pending.
    pub fn once(&self, delay: Duration) {
        let gen = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        self.schedule(gen, delay, None);
    }

    /// Fires after `first`, then every `period`; replaces anything pending.
    pub fn repeat(&self, first: Duration, period: Duration) {
        let gen = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        self.schedule(gen, first, Some(period));
    }

    pub fn stop(&self) {
        self.generation.fetch_add(1, Ordering::SeqCst);
    }

    fn schedule(&self, gen: u64, delay: Duration, period: Option<Duration>) {
        let (ui, cb, generation) = (self.ui.clone(), self.callback.clone(), self.generation.clone());
        let ui_for_next = ui.clone();
        scheduler().add(
            delay,
            Box::new(move || {
                if generation.load(Ordering::SeqCst) != gen {
                    return;
                }
                let (cb2, generation2) = (cb.clone(), generation.clone());
                ui.post(move || {
                    if generation2.load(Ordering::SeqCst) == gen {
                        cb2();
                    }
                });
                if let Some(p) = period {
                    let timer = UiTimer { ui: ui_for_next, callback: cb, generation };
                    timer.schedule(gen, p, Some(p));
                }
            }),
        );
    }
}
