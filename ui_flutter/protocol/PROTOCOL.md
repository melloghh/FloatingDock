# Engine <-> UI protocol, version 1

The single description of the wire format. The code mirrors are `rust/crates/fd-ipc/src/protocol.rs` (Rust) and
`ui_flutter/fd_ipc/lib/protocol.dart` (Dart). Change the three together and bump `Version` for incompatible changes.

## Transport
TCP on the loopback interface, on a random port chosen at engine start. The engine publishes the endpoint `tcp:<port>:<token>` in
`<profile>\engine.endpoint` (default profile `%LOCALAPPDATA%\FloatingDock`; only the profile's user can read it) and passes it to the UI
as `--socket <endpoint>`. The random token must be sent as `token` in `hello`; any other client is refused (`unauthorized`).
Unix-domain sockets were used first and dropped: on Windows they proved unreliable (connect failing with error 10022, files of
dead sockets that can be neither deleted nor connected to, error 1920).

Frames are UTF-8 JSON objects, one per line (`\n`; JSON strings escape their own newlines), at most 4 MB.
A client that sends a larger frame is disconnected.

## Messages
```
request   {"id": 1, "method": "settings.get", "params": {}}
response  {"id": 1, "result": ...}                      or  {"id": 1, "error": {"code": "bad-params", "message": "..."}}
event     {"event": "settings.changed", "data": ...}    (no id; pushed to clients subscribed to the topic)
```
`id` is chosen by the client; replies may come out of order. The first request on a connection must be `hello`
(only `ping` is allowed before it).

Naming: the envelope and engine-made objects use camelCase. Objects that mirror stored data (settings, app refs)
keep the stored PascalCase names of `config.json` / `pinned.json`, so the UI and the file speak the same names.

## Methods
| method | params | result |
|---|---|---|
| `hello` | `{client: string, protocol: 1}` | `{protocol, engine: "1.6.6", pid}`; error `protocol-mismatch` if versions differ |
| `ping` | - | `{time: unix ms}` |
| `subscribe` | `{topics: ["settings", "pinned", "windows"]}` or `["*"]` | `{topics}`; replaces the previous subscription |
| `engine.quit` | - | `{quitting: true}`, then the engine stops |
| `settings.get` | - | the whole `DockSettings` object |
| `settings.set` | `{patch: {Name: value, ...}}` | the new whole settings. Names are case-insensitive; unknown, read-only (`SchemaVersion`) or wrongly typed values fail the **whole** patch with `bad-params` (`message` starts with the key) and change nothing. Values are clamped like `config.json` values |
| `pinned.get` | - | array of `AppRef` |
| `windows.list` | - | `{foreground: hwnd, windows: [{hwnd, pid, exePath, aumid, title, className, minimized}]}` |
| `media.get` | - | `{title, artist, source, canPrevious, canToggle, canNext, playing, position, duration, canShuffle, shuffle, canRepeat, repeat}` or `null` |
| `media.control` | `{command: previous\|next\|toggle\|shuffle\|repeat}` | `{ok: bool}` |

## Events
Topic = the part of the event name before the first dot.

| event | data |
|---|---|
| `settings.changed` | the whole settings object, after any change from any client or from the settings file |
| `pinned.changed` | array of `AppRef` |
| `windows.changed` | same shape as the `windows.list` result (debounced by the engine) |

Subscribe first, then read the initial state with `*.get` / `windows.list`, so no change is missed.

## Errors
`bad-request` (not an object, no id/method), `unknown-method`, `bad-params`, `hello-required`, `protocol-mismatch`,
`failed` (the handler threw; the message is the exception text).

## Delivery rules
- A client that stops reading is dropped (its queue holds 2048 frames); the engine never waits for a slow UI.
- Closing the socket is the only "unsubscribe".
- Pinned apps and settings are the engine's data: UIs never write `config.json` themselves.
