# Engine <-> UI protocol, version 1

The single description of the wire format. The code mirrors are `src/FloatingDock.Ipc/Protocol.cs` (C#) and
`ui_flutter/fd_ipc/lib/protocol.dart` (Dart). Change the three together and bump `Version` for incompatible changes.

## Transport
A Unix-domain stream socket at `<profile>\engine.sock` (default profile `%LOCALAPPDATA%\FloatingDock`), so only the
profile's user can connect. A named pipe was the first idea; Dart has no asynchronous named-pipe API, while Unix sockets
work in both .NET and `dart:io` on Windows 10 1803+. The path may not exceed 100 characters (`sun_path` is 108 bytes).

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
