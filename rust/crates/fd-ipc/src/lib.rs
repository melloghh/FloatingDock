//! Engine <-> UI protocol (version 1): framing, server and a small blocking client. Prose: `ui_flutter/protocol/PROTOCOL.md`.

pub mod client;
pub mod endpoint;
pub mod protocol;
pub mod server;

pub use client::IpcClient;
pub use server::{IpcError, IpcRequest, IpcResult, IpcServer, IpcSession};

#[cfg(test)]
mod tests;
