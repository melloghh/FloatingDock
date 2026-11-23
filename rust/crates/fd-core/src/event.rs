use std::sync::{Arc, Mutex};

/// A minimal multicast callback list (the C# `event`). Listeners run on the thread that emits.
pub struct Event<T: ?Sized> {
    listeners: Mutex<Vec<Arc<dyn Fn(&T) + Send + Sync>>>,
}

impl<T: ?Sized> Default for Event<T> {
    fn default() -> Self {
        Self { listeners: Mutex::new(Vec::new()) }
    }
}

impl<T: ?Sized> Event<T> {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn subscribe(&self, f: impl Fn(&T) + Send + Sync + 'static) {
        self.listeners.lock().unwrap().push(Arc::new(f));
    }

    pub fn emit(&self, value: &T) {
        // Copy the list first so a listener may subscribe without deadlocking.
        let list: Vec<_> = self.listeners.lock().unwrap().clone();
        for l in list {
            l(value);
        }
    }
}
