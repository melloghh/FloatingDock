# Architecture

```
src/
  FloatingDock.Core      net10.0, no OS dependency, unit-tested
  FloatingDock.Windows   net10.0-windows, Win32 / COM / Shell, no UI framework
  FloatingDock.App       WinUI 3 (Windows App SDK), code-only UI
tests/FloatingDock.Core.Tests   xUnit (190 tests)
tests/FloatingDock.Windows.Tests   xUnit (13 tests)
```

## Core (pure logic)
* `Configuration` – `DockSettings` (schema v2, clamped by `Normalize`), `SettingsStore` (debounced atomic save, corrupt file → `.corrupt` backup + defaults, migration from v1), `AppPaths`.
* `Geometry` – the magnification maths. `Magnification` (bell curve + its integral), `MagnificationState` (hover envelope, cursor smoothing), `WindowMetrics` (the window size depends only on the *set* of icons and the settings — never on the cursor — so the window and its hover zone never move while icons grow), `DockLayout` (positions, hit-testing), `DockMotion` (appear / leave / reorder animations).
* `Services` – `DockEntryReconciler` (pinned + running + special items → slots), `AppListStore` (pinned apps and Launchpad tiles), `AppSearchIndex`, `HotkeyParser`.
* `Icons` – pack loading, name matching, ZIP import/export with path-traversal checks, overrides, resolver.

## Windows layer
Shell work (icons, AppsFolder, shortcut resolving) runs on **one dedicated STA thread** (`ShellWorker`). Running apps come from a `SetWinEventHook` watcher (create / destroy / show / hide / cloak / foreground / minimize) with a 30 s safety-net rescan — no polling loop. UWP windows are resolved through the AUMID of the real app behind `ApplicationFrameHost`.

### Taskbar safety (`Core/Taskbar`, `Windows/Taskbar`)
`TaskbarCoordinator` (pure, tested with a fake `ITaskbarSystem`) owns the sequence: **record → verify record on disk → change → verify**. `taskbar-state.json` (`TaskbarState`, schema v2) holds the phase (Pending/Applied/Restored), the original AppBar flags, whether we enabled auto-hide, and every taskbar window we hid, including its original native region. If the record cannot be saved and read back, the change is refused (the setting reverts to *Leave*). *Hide completely* falls back to *Auto-hide* when it cannot be applied; failures roll back. `Restore` runs the inverse operations, verifies them, and deletes the record only afterwards – otherwise it is kept for retry. Restore points: exit, `ProcessExit`, next start (`RecoverFromPreviousRun`, before settings are applied), hotkey, tray, Settings, and `--restore-taskbar [--quiet]`. A forced restore (unknown state) always asks first; `--quiet` never forces. Explorer restarts are handled by remapping handles. A hard kill / power loss cannot run any code – the guarantee is *restore on the next start*, not *on kill*; the shortcut/CLI exist for when the dock is not started again.

### AppBar
`AppBarReservation` reserves one slot per dock monitor, checks `ABM_NEW`/`ABM_SETPOS` results, undoes on failure, recreates stale HWNDs and reacts to `ABN_POSCHANGED`; `Release`/`Dispose` remove every slot.

### Persistence
All JSON goes through `JsonFile`: unique temp file → flush → read-back → `File.Replace` with `.bak` → verify. Loading falls back to `.bak`, keeps a `.corrupt` copy, and reports a `LoadStatus`. `AppListStore` (schema v2 with separators, legacy/v1 migration, newer schema refused) swaps in-memory state only after the save succeeded; stores raise `SaveFailed` which the UI surfaces. `IconOverrideStore` validates → copies to temp → verifies → renames → saves JSON → retains files referenced by the JSON backup, removes unused files, and refuses paths outside its folder. Corrupt stores refuse writes; recovery repairs the main JSON while retaining the good backup. Future schemas are not downgraded. Settings saves are serialized and disposal is idempotent.

### Window tracking
`WindowFilter` collects facts, `WindowFilterRules` (Core) decides. `WindowIdentityResolver` only treats a window as identified when the identity is complete (AUMID or process + path); incomplete ones are re-checked on events, at most 10 times. `AppCatalog` runs on an STA and releases every COM object in `finally`; `SHQueryUserNotificationState` is called with the correct signature and its HRESULT is checked.

## UI (code-only WinUI 3)
Views are built in C#. Minimal App.xaml generates the metadata provider and initializes XamlControlsResources. A runnable release requires the Windows XAML compiler and MakePri. SystemCompositionQueue creates and shuts down the Windows.System dispatcher required by the existing transparent composition backdrop, alongside the WinUI dispatcher.

* `DockWindow` – one borderless transparent top-most non-activating window per monitor. `NativeDockWindow` owns its Win32 HWND and delivers mouse, capture, leave, activation and keyboard messages directly; Dock has no XAML input site. `AlphaWindow` sets the transparent native surface and `SystemDockGraphics` attaches one Windows.UI.Composition DesktopWindowTarget to it. `CompositionIcon` uploads bounded decoded images through shared D3D11/D2D resources. `CompositionDockPlate` uses static rounded ends and scalable strips. `CompositionLabel` caches text surfaces rasterized once by `LabelRasterizer`. Per-frame work updates native visual Offset / Scale / Opacity. The sleeping high-resolution `FrameScheduler` defaults to 60 FPS (configurable 30–144), permits one outstanding UI callback, and stops after transitions settle, including a stationary hover. Context menus own the dock HWND, pause the loop, suppress topmost reassertion, and defer entry reconciliation until dismissal. While idle the window is clipped to the plate (`SetWindowRgn`) so transparent margins never swallow clicks. Releasing our own capture suppresses the synchronous cancellation message, preserving completed clicks.
* `DockHost` – monitors, per-monitor foreground fullscreen detection, AppBar reservation. Fullscreen temporarily uses auto-hide with a 2-pixel input strip; revealing at the edge works while the foreground app remains fullscreen. Framed maximized windows are excluded even if hiding Taskbar has expanded the work area.
* `LaunchpadWindow` – fullscreen transparent overlay with an animated dark scrim, virtualised `ItemsRepeater`, Windows 11 Start pins via read-only `Export-StartLayout`, manual `.lnk` / `.exe` tiles, refresh and settings buttons. Show/hide animations carry generations so stale completions cannot close a reopened window. Start pins are cached for two minutes. Normal startup invisibly realizes and renders the first page and prepares up to 48 icons without activating the window. A neutral focus target avoids an idle search caret; typing focuses search and retains the first character.
* `SettingsWindow` – `NavigationView`, every control writes to `SettingsStore`; the dock reacts live.
* `DockApp` – composition root, hotkeys, tray, autostart, taskbar mode, crash safety.

## Performance notes
Periodic wake-ups: the 30 s window rescan, a 1 s fullscreen check **only while** "Hide while an app is fullscreen" is on, and a 750 ms taskbar-region check only in managed taskbar modes. The taskbar check does not save JSON or reapply settings while its state is unchanged. Icons are decoded once per size and cached; system icons are cached on disk.


Release publications enable ReadyToRun and disable background tiered JIT to reduce the first-hover compilation spike. CPU measurements include the complete app process; DWM/GPU work is reported separately only if measured. See PERFORMANCE-1.0.4.md for measurement scope and raw data.

Window activation verifies HWND/PID/visibility/cloaking, restores asynchronously, selects the usable owned popup, and confirms both foreground and non-minimized state. A compatibility system-switch path restores Task Manager when an ordinary ShowWindowAsync call gives it focus but leaves it minimized. Hosted packaged apps validate the child process identity. A missing window triggers a rescan and relaunch; a denied foreground request is reported instead of silently doing nothing. Separators persist as explicit user pins with distinct GUID keys and do not match running windows.
