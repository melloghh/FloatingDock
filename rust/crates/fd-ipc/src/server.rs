//! Accepts UI processes on a loopback TCP port (see [`crate::endpoint`]), answers their requests and pushes events to the
//! clients subscribed to the topic. Frames are newline-delimited JSON; see [`crate::protocol`].

use std::collections::{HashMap, HashSet};
use std::future::Future;
use std::io;
use std::net::{Ipv4Addr, SocketAddrV4, TcpListener as StdListener};
use std::pin::Pin;
use std::sync::atomic::{AtomicI64, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock, RwLock};
use std::time::Duration;

use fd_core::Event;
use serde_json::{json, Map, Value};
use tokio::io::{AsyncBufReadExt, AsyncRead, AsyncWriteExt, BufReader};
use tokio::net::{TcpListener, TcpStream};
use tokio::runtime::Runtime;
use tokio::sync::{mpsc, watch};

use crate::{endpoint, protocol};

const HELLO_TIMEOUT: Duration = Duration::from_secs(10);
const OUTBOX_CAPACITY: usize = 2048;

/// A failure the handler wants the client to see as `{"error":{"code","message"}}`.
#[derive(Debug, Clone)]
pub struct IpcError {
    pub code: String,
    pub message: String,
}

impl IpcError {
    pub fn new(code: &str, message: impl Into<String>) -> Self {
        Self { code: code.to_string(), message: message.into() }
    }

    pub fn bad_params(message: impl Into<String>) -> Self {
        Self::new(protocol::errors::BAD_PARAMS, message)
    }

    pub fn failed(message: impl Into<String>) -> Self {
        Self::new(protocol::errors::FAILED, message)
    }
}

pub type IpcResult = Result<Value, IpcError>;
pub type HandlerFuture = Pin<Box<dyn Future<Output = IpcResult> + Send>>;
type Handler = Arc<dyn Fn(IpcRequest) -> HandlerFuture + Send + Sync>;

pub struct IpcRequest {
    pub session: Arc<IpcSession>,
    pub method: String,
    pub params: Map<String, Value>,
}

impl IpcRequest {
    pub fn str(&self, name: &str) -> Result<&str, IpcError> {
        self.params
            .get(name)
            .and_then(Value::as_str)
            .ok_or_else(|| IpcError::bad_params(format!("{name} is required.")))
    }

    pub fn opt_str(&self, name: &str) -> Option<&str> {
        self.params.get(name).and_then(Value::as_str)
    }

    pub fn i64_or(&self, name: &str, default: i64) -> i64 {
        self.params.get(name).and_then(Value::as_i64).unwrap_or(default)
    }

    pub fn bool_or(&self, name: &str, default: bool) -> bool {
        self.params.get(name).and_then(Value::as_bool).unwrap_or(default)
    }

    pub fn object(&self, name: &str) -> Option<&Map<String, Value>> {
        self.params.get(name).and_then(Value::as_object)
    }
}

/// One connected UI process.
pub struct IpcSession {
    id: u64,
    client_name: Mutex<Option<String>>,
    client_pid: AtomicI64,
    topics: RwLock<HashSet<String>>,
    outbox: Mutex<Option<mpsc::Sender<String>>>,
    cancel: watch::Sender<bool>,
}

impl IpcSession {
    pub fn id(&self) -> u64 {
        self.id
    }

    /// Name the client gave in `hello`; `None` until then.
    pub fn client_name(&self) -> Option<String> {
        self.client_name.lock().unwrap().clone()
    }

    pub fn client_pid(&self) -> i64 {
        self.client_pid.load(Ordering::Relaxed)
    }

    fn wants(&self, topic: &str) -> bool {
        let t = self.topics.read().unwrap();
        t.contains("*") || t.contains(topic)
    }

    fn try_enqueue(&self, line: String) -> bool {
        match self.outbox.lock().unwrap().as_ref() {
            Some(tx) => tx.try_send(line).is_ok(),
            None => false,
        }
    }

    /// No more messages are queued; the writer drains what is there and the connection closes.
    fn complete(&self) {
        self.outbox.lock().unwrap().take();
    }
}

struct Core {
    token: String,
    engine_version: String,
    handlers: OnceLock<HashMap<String, Handler>>,
    sessions: Mutex<HashMap<u64, Arc<IpcSession>>>,
    next_session: AtomicU64,
    stop: watch::Sender<bool>,
    session_opened: Event<Arc<IpcSession>>,
    session_closed: Event<Arc<IpcSession>>,
    session_hello: Event<Arc<IpcSession>>,
}

pub struct IpcServer {
    core: Arc<Core>,
    pending: Mutex<HashMap<String, Handler>>,
    runtime: Mutex<Option<Runtime>>,
    endpoint: OnceLock<String>,
}

impl IpcServer {
    pub fn new(engine_version: &str) -> IpcServer {
        let mut raw = [0u8; 16];
        getrandom::fill(&mut raw).expect("random token");
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .thread_name("ipc")
            .enable_all()
            .build()
            .expect("ipc runtime");
        let server = IpcServer {
            core: Arc::new(Core {
                token: hex::encode(raw).to_uppercase(),
                engine_version: engine_version.to_string(),
                handlers: OnceLock::new(),
                sessions: Mutex::new(HashMap::new()),
                next_session: AtomicU64::new(0),
                stop: watch::channel(false).0,
                session_opened: Event::new(),
                session_closed: Event::new(),
                session_hello: Event::new(),
            }),
            pending: Mutex::new(HashMap::new()),
            runtime: Mutex::new(Some(runtime)),
            endpoint: OnceLock::new(),
        };
        server.register_sync(protocol::methods::PING, |_| {
            let ms = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis() as i64).unwrap_or(0);
            Ok(json!({ "time": ms }))
        });
        server
    }

    /// What clients connect to (known once [`start`](Self::start) ran).
    pub fn endpoint(&self) -> &str {
        self.endpoint.get().map(String::as_str).unwrap_or("")
    }

    pub fn session_count(&self) -> usize {
        self.core.sessions.lock().unwrap().len()
    }

    pub fn session_opened(&self) -> &Event<Arc<IpcSession>> {
        &self.core.session_opened
    }

    pub fn session_closed(&self) -> &Event<Arc<IpcSession>> {
        &self.core.session_closed
    }

    /// Raised when a client has said hello (its name, protocol and pid are known).
    pub fn session_hello(&self) -> &Event<Arc<IpcSession>> {
        &self.core.session_hello
    }

    pub fn register<F, Fut>(&self, method: &str, handler: F)
    where
        F: Fn(IpcRequest) -> Fut + Send + Sync + 'static,
        Fut: Future<Output = IpcResult> + Send + 'static,
    {
        assert!(self.endpoint.get().is_none(), "Register handlers before start().");
        let h: Handler = Arc::new(move |req| Box::pin(handler(req)));
        self.pending.lock().unwrap().insert(method.to_string(), h);
    }

    /// For handlers that answer at once.
    pub fn register_sync<F>(&self, method: &str, handler: F)
    where
        F: Fn(IpcRequest) -> IpcResult + Send + Sync + 'static,
    {
        assert!(self.endpoint.get().is_none(), "Register handlers before start().");
        let h: Handler = Arc::new(move |req| {
            let r = handler(req);
            Box::pin(async move { r })
        });
        self.pending.lock().unwrap().insert(method.to_string(), h);
    }

    pub fn start(&self) -> io::Result<()> {
        let handlers = std::mem::take(&mut *self.pending.lock().unwrap());
        let _ = self.core.handlers.set(handlers);
        let std_listener = StdListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))?;
        std_listener.set_nonblocking(true)?;
        let port = std_listener.local_addr()?.port();
        let _ = self.endpoint.set(endpoint::format(port, &self.core.token));
        let core = self.core.clone();
        let rt = self.runtime.lock().unwrap();
        let rt = rt.as_ref().expect("runtime running");
        rt.spawn(async move {
            let listener = match TcpListener::from_std(std_listener) {
                Ok(l) => l,
                Err(_) => return,
            };
            accept_loop(core, listener).await;
        });
        Ok(())
    }

    /// Sends `event_name` to every session subscribed to its topic.
    pub fn publish(&self, event_name: &str, data: Value) {
        let sessions: Vec<Arc<IpcSession>> = self.core.sessions.lock().unwrap().values().cloned().collect();
        if sessions.is_empty() {
            return;
        }
        let topic = protocol::topic_of(event_name);
        let mut line: Option<String> = None;
        for s in sessions {
            if !s.wants(topic) {
                continue;
            }
            let line = line.get_or_insert_with(|| protocol::event_line(event_name, data.clone())).clone();
            if !s.try_enqueue(line) {
                s.complete(); // a client that stopped reading is dropped, never waited for
            }
        }
    }

    pub fn shutdown(&self) {
        let _ = self.core.stop.send(true);
        for s in self.core.sessions.lock().unwrap().values() {
            s.complete();
        }
        if let Some(rt) = self.runtime.lock().unwrap().take() {
            rt.shutdown_timeout(Duration::from_millis(300));
        }
    }

    /// The runtime the server runs on, for engine tasks that must live next to it.
    pub fn spawn<Fut>(&self, fut: Fut)
    where
        Fut: Future<Output = ()> + Send + 'static,
    {
        if let Some(rt) = self.runtime.lock().unwrap().as_ref() {
            rt.spawn(fut);
        }
    }
}

impl Drop for IpcServer {
    fn drop(&mut self) {
        self.shutdown();
    }
}

async fn accept_loop(core: Arc<Core>, listener: TcpListener) {
    let mut stop = core.stop.subscribe();
    loop {
        tokio::select! {
            accepted = listener.accept() => match accepted {
                Ok((stream, _)) => { tokio::spawn(serve(core.clone(), stream)); }
                Err(_) => { if *stop.borrow() { return; } }
            },
            _ = stop.changed() => return,
        }
    }
}

async fn cancelled(rx: &mut watch::Receiver<bool>) {
    loop {
        if *rx.borrow() {
            return;
        }
        if rx.changed().await.is_err() {
            return;
        }
    }
}

async fn serve(core: Arc<Core>, stream: TcpStream) {
    let _ = stream.set_nodelay(true);
    let (tx, mut rx) = mpsc::channel::<String>(OUTBOX_CAPACITY);
    let (cancel_tx, mut cancel_rx) = watch::channel(false);
    let session = Arc::new(IpcSession {
        id: core.next_session.fetch_add(1, Ordering::Relaxed) + 1,
        client_name: Mutex::new(None),
        client_pid: AtomicI64::new(0),
        topics: RwLock::new(HashSet::new()),
        outbox: Mutex::new(Some(tx)),
        cancel: cancel_tx,
    });
    core.sessions.lock().unwrap().insert(session.id, session.clone());
    core.session_opened.emit(&session);

    // A connection that never says hello (a stray local process) must not hold a session open for ever.
    {
        let s = session.clone();
        let mut c = cancel_rx.clone();
        tokio::spawn(async move {
            tokio::select! {
                _ = tokio::time::sleep(HELLO_TIMEOUT) => {
                    if s.client_name().is_none() { let _ = s.cancel.send(true); }
                }
                _ = cancelled(&mut c) => {}
            }
        });
    }

    let (read_half, mut write_half) = stream.into_split();
    let writer = {
        let s = session.clone();
        let mut c = cancel_rx.clone();
        tokio::spawn(async move {
            loop {
                tokio::select! {
                    next = rx.recv() => match next {
                        Some(line) => {
                            let mut bytes = line.into_bytes();
                            bytes.push(b'\n');
                            if write_half.write_all(&bytes).await.is_err() { break; }
                        }
                        None => break,
                    },
                    _ = cancelled(&mut c) => break,
                }
            }
            let _ = write_half.shutdown().await;
            let _ = s.cancel.send(true);
        })
    };

    let mut reader = BufReader::new(read_half);
    loop {
        tokio::select! {
            frame = read_frame(&mut reader, protocol::MAX_FRAME_BYTES) => match frame {
                Ok(Some(text)) => {
                    let (core, session) = (core.clone(), session.clone());
                    tokio::spawn(async move { handle(core, session, text).await });
                }
                _ => break, // closed, or a frame that was too large: drop the client
            },
            _ = cancelled(&mut cancel_rx) => break,
        }
    }

    session.complete();
    let _ = session.cancel.send(true);
    let _ = writer.await;
    core.sessions.lock().unwrap().remove(&session.id);
    core.session_closed.emit(&session);
}

/// The next non-empty frame, or `None` when the peer closed the stream.
async fn read_frame<R: AsyncRead + Unpin>(r: &mut BufReader<R>, max: usize) -> io::Result<Option<String>> {
    loop {
        let mut buf: Vec<u8> = Vec::new();
        loop {
            let available = r.fill_buf().await?;
            if available.is_empty() {
                return Ok(None);
            }
            match available.iter().position(|b| *b == b'\n') {
                Some(i) => {
                    buf.extend_from_slice(&available[..i]);
                    r.consume(i + 1);
                    break;
                }
                None => {
                    let n = available.len();
                    buf.extend_from_slice(available);
                    r.consume(n);
                }
            }
            if buf.len() > max {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "frame too large"));
            }
        }
        while buf.last() == Some(&b'\r') {
            buf.pop();
        }
        if buf.is_empty() {
            continue;
        }
        return Ok(Some(String::from_utf8_lossy(&buf).into_owned()));
    }
}

async fn handle(core: Arc<Core>, session: Arc<IpcSession>, frame: String) {
    let mut id = Value::Null;
    let reply = match serde_json::from_str::<Value>(&frame) {
        Ok(Value::Object(msg)) => {
            id = msg.get("id").cloned().unwrap_or(Value::Null);
            if id.is_null() {
                protocol::error_line(&id, protocol::errors::BAD_REQUEST, "A request needs an id.")
            } else {
                match msg.get("method").and_then(Value::as_str) {
                    None => protocol::error_line(&id, protocol::errors::BAD_REQUEST, "A request needs a method."),
                    Some(method) => {
                        let params = msg.get("params").and_then(Value::as_object).cloned().unwrap_or_default();
                        // A handler that panics must answer "failed", not take the connection or the engine down.
                        let (c, s, m) = (core.clone(), session.clone(), method.to_string());
                        let joined = tokio::spawn(async move { dispatch(&c, s, m, params).await }).await;
                        match joined {
                            Ok(Ok(result)) => protocol::response_line(&id, result),
                            Ok(Err(e)) => protocol::error_line(&id, &e.code, &e.message),
                            Err(_) => protocol::error_line(&id, protocol::errors::FAILED, "The handler stopped unexpectedly."),
                        }
                    }
                }
            }
        }
        Ok(_) => protocol::error_line(&id, protocol::errors::BAD_REQUEST, "A request must be a JSON object."),
        Err(e) => protocol::error_line(&id, protocol::errors::BAD_REQUEST, &e.to_string()),
    };
    session.try_enqueue(reply);
}

async fn dispatch(core: &Core, session: Arc<IpcSession>, method: String, params: Map<String, Value>) -> IpcResult {
    if method == protocol::methods::HELLO {
        return hello(core, &session, &params);
    }
    if session.client_name().is_none() && method != protocol::methods::PING {
        return Err(IpcError::new(protocol::errors::HELLO_REQUIRED, "Send hello first."));
    }
    if method == protocol::methods::SUBSCRIBE {
        return subscribe(&session, &params);
    }
    let handler = core
        .handlers
        .get()
        .and_then(|h| h.get(&method))
        .cloned()
        .ok_or_else(|| IpcError::new(protocol::errors::UNKNOWN_METHOD, format!("Unknown method: {method}")))?;
    handler(IpcRequest { session, method, params }).await
}

fn hello(core: &Core, session: &Arc<IpcSession>, p: &Map<String, Value>) -> IpcResult {
    let version = p.get("protocol").and_then(Value::as_i64).unwrap_or(0);
    if version != protocol::VERSION {
        return Err(IpcError::new(
            protocol::errors::PROTOCOL_MISMATCH,
            format!("Engine speaks protocol {}, client asked for {version}.", protocol::VERSION),
        ));
    }
    // Any local process can reach a loopback port: only a client that read the endpoint file knows the token.
    if p.get("token").and_then(Value::as_str) != Some(core.token.as_str()) {
        return Err(IpcError::new(protocol::errors::UNAUTHORIZED, "Wrong or missing token."));
    }
    *session.client_name.lock().unwrap() = Some(p.get("client").and_then(Value::as_str).unwrap_or("unknown").to_string());
    session.client_pid.store(p.get("pid").and_then(Value::as_i64).unwrap_or(0), Ordering::Relaxed);
    core.session_hello.emit(session);
    Ok(json!({ "protocol": protocol::VERSION, "engine": core.engine_version, "pid": std::process::id() }))
}

fn subscribe(session: &IpcSession, p: &Map<String, Value>) -> IpcResult {
    let topics: Option<Vec<String>> = p
        .get("topics")
        .and_then(Value::as_array)
        .map(|a| a.iter().filter_map(|t| t.as_str().map(str::to_string)).filter(|t| !t.is_empty()).collect());
    let topics = topics.ok_or_else(|| IpcError::bad_params("topics must be an array of strings."))?;
    *session.topics.write().unwrap() = topics.iter().cloned().collect();
    Ok(json!({ "topics": topics }))
}
