# FloatingDock engine in Rust

The windowless engine of FloatingDock (settings, pinned apps, windows, icons, tray, hotkeys, Windows taskbar, media, search),
a port of `src/FloatingDock.{Core,Windows,Ipc,Engine}` (C#). The Flutter UI (`../ui_flutter`) talks to it over the same IPC
protocol (`../ui_flutter/protocol/PROTOCOL.md`), so either engine can run behind the same UI. The stored files (`config.json`,
`pinned.json`, `icon-overrides.json`, `taskbar-state.json`, ...) keep their format.

| crate | what it holds |
|---|---|
| `fd-core` | platform-neutral logic: settings (+patch, migration, clamping), JSON persistence with `.bak`/`.corrupt`, app lists, window/app matching, dock-entry reconciler, icon packs (folders, ZIP), overrides, search, taskbar safety logic (`TaskbarCoordinator`) |
| `fd-ipc` | newline-delimited JSON over loopback TCP with a token: server (tokio), blocking client, protocol constants |
| `fd-windows` | Win32: message loop and timers, WinEvent hooks, running apps, activation, shell worker (STA), icons, app catalogue, launcher, hotkeys/tray/message window, taskbar, media (GSMTC), Wi-Fi, audio, monitors, tray icons (UI Automation), capture overlay, shell animation target |
| `fd-engine` | the program: composition root, IPC handlers, UI supervisor, CLI (`--quit`, `--restore-taskbar`, `--diagnostic-profile`, `--ui`, `--no-ui`) |

```powershell
cargo test --workspace                     # unit tests (no desktop is touched)
cargo build --release -p fd-engine         # target\release\fd-engine.exe, rename to FloatingDock.exe next to ui\fd_ui.exe
python tools/ipc_probe.py <profile> <method> [json]   # talk to a running engine
```

`--diagnostic-profile <dir>` works on a copy of the profile and never touches the desktop (no taskbar changes, hotkeys or tray).
The Russian UI strings are generated from the C# source: `python tools/gen_texts.py` (run from `source-v3`).

Not ported yet: the right-click menu of a tray icon (`tray.activate` with `context: true` answers `ok: false`).
