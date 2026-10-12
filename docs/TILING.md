# Built-in tiling

FloatingDock 1.2.0 includes an independent C# tiling engine. It runs inside FloatingDock, uses no GlazeWM installation or IPC, and contains no copied GlazeWM source. The project remains MIT licensed. [GlazeWM](https://github.com/glzr-io/glazewm) was studied as an architectural reference for event-driven window tracking and split layouts.

Enable it in **Settings → Tiling manager → Enable built-in tiling**. It is off by default. Settings cover 1–9 workspaces per monitor, inner/outer gaps, finite window animation duration, workspace buttons on the top bar, shortcut modifier and excluded executable names.

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

Only the configured workspace numbers are registered. The alternative modifier is Ctrl+Alt+Win; resizing keeps Ctrl+Alt+Shift. A conflict cancels activation and releases all partially registered shortcuts before any window is moved.

The engine uses FloatingDock's shared WinEvent registry, coalesces notifications, and ignores geometry events caused by its own animation. Binary split trees store order, direction and ratios per monitor/workspace. Layout accounts for work areas, monitor DPI and invisible DWM borders. Directional focus can cross monitors; moving across monitors transfers the window into that monitor's active workspace. Dragging suspends placement until move/resize ends; dropping over a tile swaps it, and dragging across monitors transfers it.

FloatingDock surfaces, modal dialogs, non-resizable windows and initially maximized windows are excluded. Maximizing a managed window floats it. Windows whose reported minimum size does not fit their tile also float. User-minimized windows retain their place in the tree and temporarily occupy no tile. Exclusions are exact executable names separated by semicolons.

Inactive workspace windows are minimized rather than hidden, so they remain available through the dock and Windows even after an unexpected exit. Activating one through the dock selects its workspace. Disable, pause and normal shutdown restore original placements; ordinary user-minimized or floating windows retain their current state. A finite 60 Hz transition timer stops when the targets settle. No tiling polling or animation runs while the feature is disabled.

This is the initial engine. Workspaces are local to a running session; it does not create Windows virtual desktops. There is no YAML command language, saved split-tree history, stacking/tabbed layout or GlazeWM API compatibility. Elevated applications may reject movement from an unelevated dock. Automated native checks use only owned fixture windows; testing across several physical monitors, mixed DPI, games and unusual application frames remains limited.
