# Built-in tiling

FloatingDock 1.3.0 includes an independent C# tiling engine. It runs inside FloatingDock, uses no GlazeWM installation or IPC, and contains no copied GlazeWM source. The project remains MIT licensed. [GlazeWM](https://github.com/glzr-io/glazewm) was studied as a behavioral and architectural reference. In particular, its [window admission](https://github.com/glzr-io/glazewm/blob/5709ad0a3c7c386bbc3e38166a865ffc12937515/packages/wm/src/commands/window/manage_window.rs) and [workspace selection](https://github.com/glzr-io/glazewm/blob/5709ad0a3c7c386bbc3e38166a865ffc12937515/packages/wm/src/commands/workspace/focus_workspace.rs) informed the separation of selected workspace, focus, window state and native synchronization. FloatingDock implements those responsibilities in its own model and finite placement paths.

Enable it in **Settings → Tiling manager → Enable built-in tiling**. It is off by default. Settings cover 1–9 workspaces per monitor, inner/outer gaps, finite window animation duration and FPS, startup settling, centered workspace buttons, dock concealment, native border colors, per-action bindings and excluded executable names. The interface supports English and Russian.

With the default Alt modifier:

| Action | Shortcut |
|---|---|
| Focus left / down / up / right | Alt + H / J / K / L |
| Move / swap a tile | Alt + Shift + H / J / K / L |
| Resize a split | Ctrl + Alt + Shift + H / J / K / L |
| Select workspace | Alt + 1…9 |
| Send focused window to workspace | Alt + Shift + 1…9 |
| Toggle floating | Alt + F |
| Toggle horizontal/vertical split | Alt + V |
| Pause / resume | Alt + P |

Only the configured workspace numbers are registered. The alternative modifier is Ctrl+Alt+Win; resizing defaults to Ctrl+Alt+Shift. Expand **Keyboard shortcuts → Advanced** to edit every action, including previous/next workspace. Empty text disables that action; Reset restores modifier-based defaults. Duplicate bindings and conflicts with dock shortcuts are rejected. If Windows rejects a replacement shortcut, previous bindings are restored and the running tiling session stays intact. A conflict during first activation cancels enablement before placement.

The engine uses FloatingDock's shared WinEvent registry, coalesces notifications, and ignores geometry events caused by its own animation. Binary split trees store order, direction and ratios per monitor/workspace. A new window splits the available tile along a suitable axis; minimum-size checks try other leaves/axes and equal columns/rows before releasing an app to float. Layout accounts for work areas, monitor DPI and invisible DWM borders. Directional focus can cross monitors; moving across monitors transfers the window into that monitor's active workspace. Dragging suspends placement until move/resize ends; dropping over a tile swaps it, and dragging across monitors transfers it.

FloatingDock surfaces, modal dialogs, non-resizable windows and fullscreen windows are excluded. **Tile apps that start maximized** restores a resizable maximized app on admission, waits for its restored startup geometry and places it in the selected workspace. Turn that option off to retain maximized apps outside the layout. Deliberately maximizing an already managed window floats it. Windows whose minimum size cannot fit the available layout also float and stay visible. User-minimized windows retain their place in the tree and temporarily occupy no tile. Exclusions are exact executable names separated by semicolons.

Inactive workspace windows are minimized rather than hidden, so they remain available through the dock and Windows even after an unexpected exit. The selected workspace is authoritative: stale foreground snapshots cannot change it. New windows join its layout. By default, activating an app through the dock brings its existing window into the current workspace. Disable **Bring activated apps into the current workspace** to select an existing app's workspace instead. Workspace buttons restore their own windows; they do not pull windows from other spaces.

The dock hides while tiles are visible on its monitor. Its configured edge reveal and keyboard focus shortcut remain available; an empty workspace, pause or disable releases concealment. Disable **Hide dock while tiled windows are visible** to keep it shown.

Native colored borders use [Windows 11 DWM border attributes](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute). Set focused/inactive colors as RGB hex values. No caption/style replacement, injection or mouse-blocking outline windows are used. Turning the option off, floating a window or disabling tiling releases the override. If Windows exposes the original attribute it is restored; Windows builds that reject reading it return to DWM's system default.

Disable, pause and normal shutdown restore original placements; ordinary user-minimized or floating windows retain their current state. Placement and restoration requests preserve order across a busy GUI thread. The finite 30–144 FPS movement timer runs only during transitions. Client size is applied once per target change, position follows a path with gentle start/arrival and velocity continuity on retarget, and unchanged startup notifications preserve the original deadline. New windows wait briefly for stable startup geometry with a bounded timeout. Native placement is asynchronous and its pending queue is bounded. Motion respects Windows' animation preference. No tiling animation runs while disabled or settled.

This is the initial engine. Workspaces are local to a running session; it does not create Windows virtual desktops. There is no YAML command language, saved split-tree history, stacking/tabbed layout or GlazeWM API compatibility. Elevated applications may reject movement from an unelevated dock. Automated native checks use only owned fixture windows; testing across several physical monitors, mixed DPI, games and unusual application frames remains limited.
