//! A small blocking client for the engine's own command-line forms (`--quit`, a second launch asking the running engine to open
//! the settings). The UI process uses the Dart client instead.

use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::time::{Duration, Instant};

use serde_json::{json, Value};

use crate::{endpoint, protocol};

pub struct IpcClient {
    reader: BufReader<TcpStream>,
    writer: TcpStream,
    next_id: i64,
}

impl IpcClient {
    pub fn connect(endpoint_text: &str, client_name: &str, timeout: Duration) -> Result<IpcClient, String> {
        let (addr, token) = endpoint::parse(endpoint_text)?;
        let stream = TcpStream::connect_timeout(&addr, timeout).map_err(|e| e.to_string())?;
        let _ = stream.set_nodelay(true);
        let writer = stream.try_clone().map_err(|e| e.to_string())?;
        let mut client = IpcClient { reader: BufReader::new(stream), writer, next_id: 0 };
        client.request(
            protocol::methods::HELLO,
            json!({ "client": client_name, "protocol": protocol::VERSION, "token": token, "pid": std::process::id() }),
            timeout,
        )?;
        Ok(client)
    }

    /// Sends a request and waits for its reply, skipping events.
    pub fn request(&mut self, method: &str, params: Value, timeout: Duration) -> Result<Value, String> {
        self.next_id += 1;
        let id = self.next_id;
        let line = protocol::request_line(id, method, params) + "\n";
        self.writer.write_all(line.as_bytes()).map_err(|e| e.to_string())?;
        let deadline = Instant::now() + timeout;
        loop {
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() {
                return Err("timed out".into());
            }
            self.reader.get_ref().set_read_timeout(Some(left)).map_err(|e| e.to_string())?;
            let mut text = String::new();
            let n = self.reader.read_line(&mut text).map_err(|e| e.to_string())?;
            if n == 0 {
                return Err("connection closed".into());
            }
            let Ok(msg) = serde_json::from_str::<Value>(text.trim()) else { continue };
            if msg.get("id").and_then(Value::as_i64) != Some(id) {
                continue;
            }
            if let Some(err) = msg.get("error") {
                return Err(err.get("message").and_then(Value::as_str).unwrap_or("error").to_string());
            }
            return Ok(msg.get("result").cloned().unwrap_or(Value::Null));
        }
    }
}
