//! Wire contract between the engine and every UI process. The prose description is `ui_flutter/protocol/PROTOCOL.md`;
//! the mirrors are `src/FloatingDock.Ipc/Protocol.cs` (C#) and `ui_flutter/fd_ipc/lib/protocol.dart` (Dart).

use serde_json::{json, Value};

/// Bumped when a message changes incompatibly; `hello` refuses a different value.
pub const VERSION: i64 = 1;
pub const MAX_FRAME_BYTES: usize = 4 * 1024 * 1024;

pub mod methods {
    pub const HELLO: &str = "hello";
    pub const PING: &str = "ping";
    pub const SUBSCRIBE: &str = "subscribe";
    pub const ENGINE_QUIT: &str = "engine.quit";
    pub const SETTINGS_GET: &str = "settings.get";
    pub const SETTINGS_SET: &str = "settings.set";
    pub const PINNED_GET: &str = "pinned.get";
    pub const WINDOWS_LIST: &str = "windows.list";
    pub const MEDIA_GET: &str = "media.get";
    pub const MEDIA_CONTROL: &str = "media.control";
}

pub mod events {
    pub const SETTINGS_CHANGED: &str = "settings.changed";
    pub const PINNED_CHANGED: &str = "pinned.changed";
    pub const WINDOWS_CHANGED: &str = "windows.changed";
}

pub mod errors {
    pub const BAD_REQUEST: &str = "bad-request";
    pub const UNKNOWN_METHOD: &str = "unknown-method";
    pub const BAD_PARAMS: &str = "bad-params";
    pub const HELLO_REQUIRED: &str = "hello-required";
    pub const UNAUTHORIZED: &str = "unauthorized";
    pub const PROTOCOL_MISMATCH: &str = "protocol-mismatch";
    pub const FAILED: &str = "failed";
}

/// The part of an event name before the first dot: the topic a client subscribes to.
pub fn topic_of(event_name: &str) -> &str {
    event_name.split('.').next().unwrap_or(event_name)
}

pub fn response_line(id: &Value, result: Value) -> String {
    json!({ "id": id, "result": result }).to_string()
}

pub fn error_line(id: &Value, code: &str, message: &str) -> String {
    json!({ "id": id, "error": { "code": code, "message": message } }).to_string()
}

pub fn event_line(name: &str, data: Value) -> String {
    json!({ "event": name, "data": data }).to_string()
}

pub fn request_line(id: i64, method: &str, params: Value) -> String {
    json!({ "id": id, "method": method, "params": params }).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lines_have_the_documented_shape() {
        assert_eq!(response_line(&json!(1), json!({"a": 1})), r#"{"id":1,"result":{"a":1}}"#);
        assert_eq!(response_line(&json!(2), Value::Null), r#"{"id":2,"result":null}"#);
        assert_eq!(error_line(&Value::Null, "bad-request", "x"), r#"{"id":null,"error":{"code":"bad-request","message":"x"}}"#);
        assert_eq!(event_line("windows.changed", json!([])), r#"{"event":"windows.changed","data":[]}"#);
        assert_eq!(topic_of("settings.changed"), "settings");
        assert_eq!(topic_of("plain"), "plain");
    }
}
