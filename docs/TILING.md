# Tiling integration proposal

FloatingDock 1.1.0 does not move application windows into a tiling layout. This document proposes a separate, optional integration.

## Recommended first implementation

Use GlazeWM as the window-management engine and add a C# IPC adapter to FloatingDock. GlazeWM's [official client documentation](https://github.com/glzr-io/glazewm-js) demonstrates querying monitors, workspaces and windows, subscribing to focus/workspace events, and sending commands. A C# adapter can implement the same protocol; FloatingDock does not need a JavaScript runtime.

Expose workspace buttons on the top bar and commands in the search palette: focus a workspace, move the active window, switch between tiling and floating, and change split orientation. Show connection state and disable unavailable actions when GlazeWM is stopped. Reconnect with backoff; do not continuously poll every window.

Keep one owner of window geometry. GlazeWM arranges applications; FloatingDock owns its dock, menus and search surfaces. Add explicit exclusion rules for FloatingDock's native window classes and titles so overlays, previews, settings and the Launchpad never become tiles. Account for reserved top-bar/dock space and monitor DPI without having both applications repeatedly change the same work area.

Make the integration opt-in. Keep GlazeWM configuration backups, validate shortcut conflicts and provide an immediate disable action. Installation and startup of a separate window manager should be an explicit later step.

## Building an independent engine

A self-contained engine would require substantially more work:

- A stable window registry from WinEvent create/destroy/focus/move events, with HWND/PID validation and exclusions for dialogs, transient surfaces, cloaked windows and fullscreen applications.
- A layout tree per monitor/workspace: horizontal and vertical splits, ratios, minimum sizes, gaps, floating exceptions and focus history.
- A native placement layer using batched window-position updates, correct DPI/work-area conversion, and guards against feedback from its own moves. Suspend placement during a user's drag/resize and choose a deterministic insertion point afterward.
- Normal close/minimize/restore handling, saved pre-tiling bounds, a recovery/disable path, and graceful failure for windows that reject geometry changes.
- Dedicated checks for monitor removal, DPI changes, modal dialogs, fullscreen transitions and applications running at a different integrity level.

For this project, the IPC integration is the smaller and more controllable first step. The engine itself should remain an independent feature with its own tests and rollout.

Primary references: [GlazeWM](https://github.com/glzr-io/glazewm), [official IPC client](https://github.com/glzr-io/glazewm-js).
