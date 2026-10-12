# Changelog

## 1.3.0

- Keep the selected workspace authoritative. Late tracker focus no longer switches spaces or minimizes a newly opened app; activating an existing app can bring it into the current space.
- Choose splits from the available tile, try layouts against app minimum sizes, and use equal rows/columns when a local split cannot fit tall apps.
- Wait for startup geometry to settle, handle apps launching maximized, and use gentle finite motion with continuous position/velocity on retarget. Bound queued movement for busy apps.
- Add individual shortcut editing/disable/reset, duplicate validation and rollback to previous bindings on registration failure without rebuilding the tiling session.
- Add native focused/inactive window border colors, dock concealment with edge/keyboard reveal, animation FPS, startup timing and app-activation controls.
- Center workspace buttons against the whole menu bar. Preserve native owner identity for hosted apps and restore positions/border overrides on disable.
- Add isolated sequential-opening, minimum-size, workspace race, rendered border/centering, binding rollback and hosted-owner regression checks.

## 1.2.1

- Fix jerky app opening under tiling: apply client size once, animate position, and preserve each unchanged transition's deadline during startup notification bursts.
- Cache DWM frame insets by window DPI so compositor lag cannot shift animation targets. Use asynchronous placement across UI threads to avoid blocking the dock on a busy application.
- Reassert final bounds after late startup sizing, retain zero-duration placement and restore original window positions on disable.
- Add isolated native opening, notification-burst and late-resize regression checks.
- Queue restoration after pending moves; verify placement and disable against a deliberately busy owned GUI thread.

## 1.2.0

- Replace per-keystroke recursive file scans with a bounded 30-second filename cache. Remove Documents from effective scopes, restrict personal file types and top-level folders, and add depth/refresh controls.
- Cache application search, allocate Launchpad on demand, avoid Shell identity resolution on title changes, and reduce language polling and redundant compositor writes.
- Refine the palette with explicit keyboard selection, lightweight result rows and clearer input/footer styling.
- Render live Explorer notification buttons in FloatingDock's tray menu with Windows icon snapshots and stable UI Automation runtime IDs. Route invocation/context requests to Explorer without injection.
- Remove media source identifiers and start finite content translations after XAML loads, keeping native blur and borders aligned.
- Add independent opt-in C# tiling: per-monitor workspaces, split ratios/direction, focus/move/resize, floating/exclusion rules, minimum-size guards, drag handling, workspace buttons and finite window transitions. Restore positions on pause/disable/exit; inactive windows remain accessible through the dock.
- Add owned-window native checks, real tray callback tests, rendered-selection checks and cache/layout regression tests.

## 1.1.0

- Dismiss previews during dock-icon activation, retain suppression until the pointer leaves, and rebind DWM thumbnails after minimize/restore. Hide app labels while a preview is visible.
- Exclude label-only headroom from hover retention and watch pointer departure even after the frame loop settles.
- Add English/Russian interface selection, preview controls, finite menu transitions and configurable top-bar animation FPS, durations and hide delay. Remove the ineffective experimental blur control and group advanced dock geometry controls.
- Replace Launchpad search with an Add application picker and offer white squares, colored squares on dark, and colored squares on white launcher icons.
- Add a separate Ctrl+Alt+K command palette for applications, filenames and folders. Default file scopes are Desktop, Documents and Downloads; Windows Search queries and bounded fallback scans stay off the UI thread and support cancellation.
- Add Windows media-session transport controls and a chevron for Explorer's real system-tray overflow, including Windows 11 XAML overflow and temporary taskbar visibility cleanup.
- Document an optional GlazeWM IPC integration approach without enabling a tiling engine.

## 1.0.19

- Keep the dock HWND stable when unpinned applications appear or disappear; animate the visible plate and slots within it.
- Weight spacing by visible slots so adding/removing a zero-size ghost does not snap the plate width.
- Bound expanded pointer input to the dock area and hide new visuals until their first layout.
- Add native application-lifecycle checks and layout-continuity regression tests.

## 1.0.18

- Anchor side docks to the physical monitor edge so work-area changes do not shift them vertically during auto-hide.
- Exclude separators from pinned-app and individual-icon settings and icon-pack application.
- Reorder visible apps while preserving stored separator positions; retain divider rendering and drag support in the dock.
- Add repeated native hide/reveal checks with changing work areas and app-reordering persistence tests.

## 1.0.17

- Clip quick-menu windows and their blur to the same rounded bounds, including asynchronous resizing.
- Keep menu borders aligned with the backdrop during opening and closing fades.
- Reset backdrop animations and extended DWM glass when dismissing a surface.
- Use an opaque settings background and page fades without scaling to avoid stale blurred content.
- Add native corner and pixel-restoration diagnostics for all five quick menus and settings lifecycle checks.

## 1.0.16

- Fully dismiss quick-menu windows and their blur surfaces after closing.
- Keep top-bar menus above the side dock without stealing focus.
- Bound Launchpad hover highlights to their cells; animate only the icon and highlight opacity.
- Fade and uniformly shrink previews before sending a normal close request.
- Restore a preview if its application remains open, and preserve close requests after leaving the preview.
- Finish menu and Launchpad transitions on their UI dispatcher, including interrupted transitions.

## 1.0.15

- Suppress edge reveal during true fullscreen video.
- Toggle the circle menu with a repeated click.
- Compact the Launchpad search field and reuse prepared tiles.
- Preserve native thumbnail aspect ratios after fullscreen restoration.
- Retain dock-icon origins for newly launched window animations.

## 1.0.14

- Reduce the top bar height and move it closer to the screen edge.

## 1.0.13

- Add bottom, left and right dock placement.
- Round window previews and add close buttons.
- Give Launchpad and the top bar a shared dark interface.
- Extend the preview hover delay and refresh input-language indicators faster.

## 1.0.12

- Remove bundled decorative icon packs.
- Correct icon transparency and minification artifacts while preserving original artwork.
