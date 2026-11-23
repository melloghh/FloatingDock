use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::time::Duration;

use serde_json::{json, Value};

use crate::server::IpcError;
use crate::{endpoint, IpcClient, IpcServer};

struct Raw {
    r: BufReader<TcpStream>,
    w: TcpStream,
}

impl Raw {
    fn connect(server: &IpcServer) -> Raw {
        let (addr, _) = endpoint::parse(server.endpoint()).unwrap();
        let s = TcpStream::connect(addr).unwrap();
        s.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
        Raw { r: BufReader::new(s.try_clone().unwrap()), w: s }
    }

    fn send(&mut self, v: Value) {
        self.w.write_all(format!("{v}\n").as_bytes()).unwrap();
    }

    fn recv(&mut self) -> Value {
        let mut line = String::new();
        self.r.read_line(&mut line).unwrap();
        serde_json::from_str(line.trim()).unwrap()
    }

    fn call(&mut self, id: i64, method: &str, params: Value) -> Value {
        self.send(json!({"id": id, "method": method, "params": params}));
        self.recv()
    }
}

fn server() -> IpcServer {
    let s = IpcServer::new("2.1.0");
    s.register_sync("echo", |r| Ok(Value::Object(r.params)));
    s.register("slow", |_| async {
        tokio::time::sleep(Duration::from_millis(30)).await;
        Ok(json!("done"))
    });
    s.register_sync("boom", |_| Err(IpcError::bad_params("nope")));
    s.register_sync("panic", |_| -> Result<Value, IpcError> { panic!("handler bug") });
    s.start().unwrap();
    s
}

fn token(server: &IpcServer) -> String {
    server.endpoint().rsplit(':').next().unwrap().to_string()
}

fn hello(c: &mut Raw, server: &IpcServer) -> Value {
    c.call(1, "hello", json!({"client": "test", "protocol": 1, "token": token(server)}))
}

#[test]
fn hello_is_required_and_checked() {
    let s = server();
    let mut c = Raw::connect(&s);
    assert_eq!(c.call(1, "echo", json!({}))["error"]["code"], "hello-required");
    assert_eq!(c.call(2, "ping", json!({}))["result"]["time"].is_i64(), true, "ping works before hello");
    assert_eq!(c.call(3, "hello", json!({"client": "t", "protocol": 2, "token": token(&s)}))["error"]["code"], "protocol-mismatch");
    assert_eq!(c.call(4, "hello", json!({"client": "t", "protocol": 1, "token": "WRONG"}))["error"]["code"], "unauthorized");
    let ok = hello(&mut c, &s);
    assert_eq!(ok["result"]["protocol"], 1);
    assert_eq!(ok["result"]["engine"], "2.1.0");
    assert_eq!(c.call(5, "nothing", json!({}))["error"]["code"], "unknown-method");
}

#[test]
fn requests_answer_with_their_ids_and_errors() {
    let s = server();
    let mut c = Raw::connect(&s);
    hello(&mut c, &s);
    assert_eq!(c.call(7, "echo", json!({"a": 1})), json!({"id": 7, "result": {"a": 1}}));
    assert_eq!(c.call(6, "slow", json!({}))["result"], "done");
    let e = c.call(8, "boom", json!({}));
    assert_eq!(e["error"], json!({"code": "bad-params", "message": "nope"}));
    let p = c.call(9, "panic", json!({}));
    assert_eq!(p["error"]["code"], "failed");
    assert_eq!(c.call(10, "echo", json!({"still": "alive"}))["result"]["still"], "alive");
    c.send(json!({"method": "echo"}));
    assert_eq!(c.recv()["error"]["code"], "bad-request");
    c.w.write_all(b"not json\n").unwrap();
    assert_eq!(c.recv()["error"]["code"], "bad-request");
}

#[test]
fn events_reach_only_subscribers_of_the_topic() {
    let s = server();
    let mut a = Raw::connect(&s);
    let mut b = Raw::connect(&s);
    hello(&mut a, &s);
    hello(&mut b, &s);
    assert_eq!(a.call(2, "subscribe", json!({"topics": ["settings"]}))["result"]["topics"], json!(["settings"]));
    assert_eq!(b.call(2, "subscribe", json!({"topics": ["*"]}))["result"]["topics"], json!(["*"]));
    s.publish("windows.changed", json!({"n": 1}));
    s.publish("settings.changed", json!({"n": 2}));
    assert_eq!(a.recv(), json!({"event": "settings.changed", "data": {"n": 2}}));
    assert_eq!(b.recv()["event"], "windows.changed");
    assert_eq!(b.recv()["event"], "settings.changed");
    assert_eq!(a.call(3, "subscribe", json!({"topics": 5}))["error"]["code"], "bad-params");
}

#[test]
fn blocking_client_and_session_counting() {
    let s = server();
    let mut c = IpcClient::connect(s.endpoint(), "cli", Duration::from_secs(5)).unwrap();
    assert_eq!(c.request("echo", json!({"k": "v"}), Duration::from_secs(5)).unwrap()["k"], "v");
    assert!(c.request("boom", json!({}), Duration::from_secs(5)).is_err());
    assert_eq!(s.session_count(), 1);
    drop(c);
    for _ in 0..50 {
        if s.session_count() == 0 {
            return;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    panic!("the session was not released");
}

#[test]
fn oversized_frame_drops_the_client() {
    let s = server();
    let mut c = Raw::connect(&s);
    hello(&mut c, &s);
    let big = vec![b'a'; 5 * 1024 * 1024];
    let _ = c.w.write_all(&big);
    let mut line = String::new();
    let n = c.r.read_line(&mut line).unwrap_or(0);
    assert_eq!(n, 0, "the server closed the connection");
}
