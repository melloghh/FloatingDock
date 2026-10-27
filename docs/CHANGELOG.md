# Changelog

## 1.6.0

- Redesign search as a rounded input capsule and separate glass results surface, with compact rows and a soft translucent selection.
- Share a cool translucent material across quick menus, Launchpad, the top bar and Settings; retain app-local blur independently of the Windows transparency switch.
- Route complete clicks on recognized native minimize buttons through the same curved dock animation as dock clicks. Restore untiled windows with the reverse transition, including interrupted minimize/restore sequences. Unsupported/custom controls and direct system commands retain the native fallback.
- Animate new windows launched from the dock, search or Launchpad after their initial geometry settles, with bounded opacity ownership and cleanup. Add staggered Launchpad tile entrances and Settings page motion; respect reduced motion.
- Remove Start/Launchpad hover labels from the dock.
- Add Shut down and Restart to Launchpad and the main menu, with explicit confirmation, Cancel as the default and no forced application termination. Diagnostics never execute power commands.
- Show the tray immediately while discovery runs, reuse recent live snapshots and cached rows, coalesce concurrent reads and reduce temporary discovery polling.
- Keep the tray open while its icon context menu is active; wait for button release before forwarding right-clicks to Explorer, retaining stale-icon validation.
- Prefer the largest matching notification snapshot and display small source images at their real pixel resolution instead of blurring them through enlargement. Notification status imagery is preserved.
- Reuse a lighter button template for search and quick menus instead of the stock WinUI visual tree.
- Cover separate search glass regions, launched-window admission, caption/dock minimize on all dock edges, reverse restoration, repeated real tray context menus, power confirmation cancellation and source-resolution limits.

## 1.5.10

- Decode packed shell animation coordinates without relying on trailing zero padding, including payloads ending at a memory-page boundary.
- Keep a known window's dock destination during hidden transition states and use the nearest dock fallback when process identity cannot be read.
- Verify and commit the first DWM thumbnail frame before minimizing or repositioning the source; abandon failed thumbnail setup instead of animating an empty surface.
- Add shell payload regressions and repeated bottom/left/right minimize checks.

## 1.5.9

- Reuse frame-dispatch callbacks and tiling frame buffers; avoid temporary enumerators in dock layout and rendering without changing animation pacing.
- Reuse immutable application identities when publishing moving dock anchors, while continuing to publish the current icon coordinates to Windows.
- Format the menu bar's input-language label only when its language changes, retaining the existing polling responsiveness.
- Validate resolved windows against their native frame owner instead of a hosted app's logical PID, preventing repeated identity resolution and missing dock entries.
- Ignore invalid search-history entries, normalize key comparisons, bound loaded history and prevent usage-count overflow.
- Preserve archive/folder names when importing icon packs without a manifest, and protect built-in pack identities regardless of Windows path casing.
- Reset equivalent application icon identities from Settings, matching the dock and Launchpad behavior.
- Persist imported applications before reporting success; remove copied shortcuts if saving fails and report failed drag-and-drop additions accurately.
- Stop queued theme, language and hotkey-status updates from accessing a closed Settings window.
- Correct the English/Russian getting-started instructions for the Windows Settings gear.

## 1.5.8

- Answer native minimize/restore destination requests on a dedicated sleeping message thread, so search or a busy WinUI frame cannot delay the shell's response and send a window toward the hidden taskbar.
- Publish immutable current dock geometry to that thread. Match new windows by application identity before the running-window scan completes, and use the dock itself while an unpinned icon is entering.
- Keep native titlebar minimization as a single Windows animation; retain existing browser opening/restore composition and custom dock animation behavior.
- Add a blocked-owner-thread regression test and desktop checks for launching from search, the first untracked minimize, busy UI, left/right dock movement and visible native shrink trajectories.

## 1.5.7

- Open Windows Settings from the menu bar gear. FloatingDock settings remain available in the main menu.
- Lower the dock, menu bar and open quick menu beneath screenshot selection UI; release the transparent bar input surface until selection ends. Preserve panel geometry and restore stacking and input afterward.
- Validate native gear clicks, screenshot overlay hit testing, layout updates during selection and capture recovery alongside existing panel input checks.

## 1.5.6

- Restore the source-opacity/layered-style setup order used before 1.5.5 when preparing tiled window opening and browser restoration. Disable native transitions after establishing the layered source, before publishing its animation surface.
- Keep native-transition failure rollback and the 1.5.5 protection against starting a second minimization image.
- Validate browser dock restoration, rapid reversal, tile opening and both minimization paths with isolated desktop checks.

## 1.5.5

- Prevent doubled window images during minimization: titlebar/system minimization keeps the native Windows transition instead of starting a second DWM thumbnail after the minimize event. A transition that already owns a hidden source can still finish through the dock.
- Preserve dock-click and workspace animations. Acquire exclusive animation ownership before hiding the source; fall back to native rendering if Windows rejects it. Restore the original source opacity and layered style after the transition.
- Resolve native shell animation destinations from the current dock icon geometry instead of a stale remembered click rectangle.
- Extend desktop regression checks to both titlebar and dock minimization, intermediate rendered frames, separate-process fixtures, delayed acknowledgements and rapid restore. Verify opaque and already-layered sources recover their original opacity.

## 1.5.4

- Reuse the main menu's controls and measured popup scroll container. Invalidate cached rows when language or the auto-hide selection changes; keep all opening/closing animations and native glass clipping.
- Cache native preview/menu handles and label dimensions, skip unchanged compositor property writes, and validate unchanged preview snapshots on their existing deadline/lifetime timers instead of every animation frame.
- Reuse the exact saturated magnification integral and the shared arrow cursor; skip unused native geometry queries and motion calculations during tile hiding. Preserve configured frame rates, durations, curves and artwork.
- Continue interrupted tile reflows from their last presented bounds, preventing an existing between-frame retarget jump.
- Verify exact saturated-integral results and menu reuse/invalidation; exercise actual language switches, pointer clicks, menu/search reversals, previews, rendering, minimize/restore and workspaces.

## 1.5.3

- Route top-bar pointer input through a transparent native window with compositor hover highlights. Cache button geometry until layout changes, avoiding XAML pointer processing during mouse movement.
- Preserve menu animation timing, native glass clipping, clicks, accessible button providers, keyboard handling, tooltips, search toggling, workspace controls and auto-hide behavior.
- Validate all seven menus with real mouse clicks, tooltip display, workspace switching, capture visibility and hide/reveal alignment. Keep internal input-driver CPU separate from product measurements.
- An external input driver comparing public 1.5.2 against this implementation measured approximately 3.2–3.7% → 0.6–0.8% total-machine CPU while moving across the bar on a 12-thread Windows 11 machine. Results vary with hardware and workload.

## 1.5.2

- Fix the 1.5.1 menu-motion regression: transform, opacity and native glass clipping use one presentation sample at the configured frame rate. Reversals continue from the last presented pose; native region changes request repaint again.
- Give top-bar buttons a small template with local hover/pressed highlights, preserving Button clicks, focus, tooltips and workspace selection while avoiding default background transitions on pointer crossings.
- Add real mouse movement across the bar to the opt-in CPU workload and assert intermediate menu poses and content/glass synchronization. A local A/B workload using native mouse input measured about 2.79% to 1.97% total-machine CPU for stock versus lightweight buttons in one process on a 12-thread machine; hardware and workloads vary.
- Keep the bounded tiling buffer reuse and redundant backdrop-write suppression from 1.5.1.

## 1.5.1

- Run top-bar menu scale, translation and opacity on the compositor. Bound native glass-clip synchronization to 60 Hz while preserving compositor motion at display refresh rate, sampled damped curves, reversal, and reduced motion.
- Avoid duplicate backdrop opacity writes and full XAML invalidation for each animated clip; cache the element visual and preserve the requested glass opacity when reopening.
- Reuse a bounded pool of hidden, transparent tiling viewports; rebind each source thumbnail, release all source opacity leases, retain at most one small viewport when idle and clear the pool on disable/shutdown. Skip unchanged thumbnail geometry/opacity updates.
- Add a repeatable CPU/allocation workload for panel/menu idle, twelve menu cycles and six owned-window openings. Local same-machine comparison reduced menu CPU time from 3266 to 1734 ms and owned tiling workload from 750 to 625 ms; these are scenario measurements, not universal CPU guarantees.
- Verify actual menu glass boundaries, rapid reversal, search toggling, dock transitions, native-buffer reuse and source visibility after completion.
## 1.5.0

- Publish the dock, Launchpad, search, animated menu bar and built-in tiling as a stable Windows x64 release, incorporating the 1.4.x desktop fixes.
- Update English/Russian getting-started documentation and tiling behavior; add a structured bug-report form.
- Package application and dependency license notices, verify clean self-contained downloads, and add English/Russian installer languages and support links.
- Build, test, check package hashes and publish Setup/Portable automatically for matching version tags through GitHub Actions.

## 1.4.12

- Reuse the source opacity lease acquired before dock restore. Keep browser composition stable instead of removing and immediately readding the layered style during workspace preparation.
- Let a successful foreground request complete asynchronously before the existing activation checks. Avoid immediately attaching input queues just because GetForegroundWindow has not caught up with the accepted request.
- Exercise three restore cycles on an existing browser, continuous intermediate frames, rapid minimize/restore reversal, a single composition style change during preparation, and preservation of the original native placement.

## 1.4.11

- Keep exactly two visible tiled windows in left and right columns, with equal default widths. Normalize retained top/bottom splits after closing or minimizing other windows; a direction toggle cannot stack the pair again.
- Preserve nested layout behavior for three or more visible windows and explicit width adjustments. Respect native minimum sizes; an incompatible second window stays floating rather than switching to stacked tiles.
- Cover the 480x600 chat/browser scenario, a pair inside a retained three-window tree, and incompatible column constraints.

## 1.4.10

- Pick one live window and decide repeat-click minimization before preparing its workspace. Restoring or focusing a target during preparation cannot immediately minimize it again; other windows of an app remain in their workspaces.
- Separate pending user minimization from workspace hiding. Preserve user intent while async native operations are queued, reassert workspace visibility after late operations, and ignore minimize acknowledgements for windows that are already restored.
- Explicit activation restores a user-minimized tile and clears its startup-placement wait. Include held returning windows during tracker gaps, clear completed restore bookkeeping, prune forgotten focus references, and avoid treating our own full-monitor zero-gap placement as application fullscreen.
- Start reflow representations without waiting for the application's final native placement, use a minimum 220 ms reflow, and retain bounded momentum when reversing direction. Snapshot minimized state from the actual HWND rather than a delayed event's name.
- Add regressions for queued workspace restore, stale native acknowledgements, pending user minimize, cross-workspace activation, zero-gap placement, grouped app selection, both activation policies, rapid switching, tile drop and reversed reflow. Two native regressions were confirmed failing before the fixes.

## 1.4.9

- Give tiled minimize and restore a curved, finite dock path with coordinated scaling. Restore unfolds the actual application image with a fast damped settling curve; visibility transitions use the dock animation duration (300/336 ms with the current 240 ms icon duration) instead of a short reflow fade.
- Acquire the invisible source before dock activation restores it, and reuse a moving presentation when reversing minimization. Neighboring tiles reflow over the unfolding duration; untiled minimization retains its existing path.
- Animate the complete search plate and glass boundary on opening and button/Escape dismissal. Ignore the search button's focus handoff until its click toggles the palette, preventing a close/reopen flash; cancelled searches and old close completions cannot reopen or hide a replacement.
- Check actual pixels and multiple native intermediate frames during tiled minimize/restore, rapid surface reuse, workspace transitions, and repeated real mouse clicks on the search button including a long press. Use a stable owned backdrop for the viewport check on animated desktops.

## 1.4.8

- Unfold the entire top-bar menu towards its button, including the glass boundary, rounded frame and content. Use a fast damped opening and an accelerating fold on dismissal, with staggered content entry.
- Keep the HWND and XAML layout at their final size; a finite frame clock synchronizes the native glass clip with the plate transform and sleeps after completion. Start the first opening without waiting for Loaded.
- Reverse same-button transitions from their current position, cancel stale completions when replacing a menu, animate tray-button dismissal, and honor disabled animations.
- Verify all seven menu types with actual native region measurements and desktop pixels outside the moving glass, plus close callbacks without a synchronization context, interrupted reopens and reduced motion.

## 1.4.7

- Bundle the blue folder artwork as File Explorer's default icon across the dock, Launchpad, search and app pickers. The image ships with the application and needs no external file or profile setup.
- Share Explorer icon overrides between its AppID-only pins and executable-only running windows. Explicit custom icons and active icon packs retain priority over the default.

## 1.4.6

- Replace Explorer suppression via DWMWA_CLOAK, which returned E_ACCESSDENIED on its foreign-process HWNDs, with reversible opacity/style leases. Read the existing live overflow tree first and stop moving discovery windows off screen.
- Delegate tray context clicks using verified mouse input at the current physical pointer, without changing its position, focusing virtual buttons or sending keyboard-menu keys. A one-step alpha input surface permits Win32 hit testing; source bounds, regions, visibility and opacity are restored after the action.
- Re-resolve unique live notification tooltips when Explorer rebuilds UIA RuntimeIds, and wait for a starting icon's bounds before clicking. Ambiguous matches and input targeting another HWND are rejected.
- Add repeated actual-screen pixel checks for stock-tray flashes and continuous cursor checks during opening and context actions, including the existing Wallpaper Engine menu and command clicks in an owned notification fixture.

## 1.4.5

- Anchor the actual native notification context menu to the clicked custom-tray icon. Applications such as Wallpaper Engine can ignore Explorer's source coordinates; correcting only that source rectangle does not position their menu.
- Match new root menus to the notification application's executable, keep them inside the anchor monitor's work area, and leave submenus/unrelated menus alone. The event hooks and fallback discovery exist only during that context action.
- Retain tray executable metadata even when Explorer provides no icon image. Verify actual popup bounds and real command clicks using a notification app that deliberately opens its menu at (0, 0), plus an opt-in check with an existing Wallpaper Engine instance.

## 1.4.4

- Refresh the visible input-language indicator every 75 ms. Resolve the application's focused input site and close the language menu immediately before requesting a layout, so delayed dismissal cannot steal focus back.
- Run tray queries on a separate, lazy STA worker so icon/catalog work cannot delay opening. Suppress Explorer's native tray presentation during discovery and activation. Align the native notification button with the custom icon before a context callback, including menus owned by another application; restore source positions and acquired cloak bits afterwards.
- Animate menu content from its upper anchor with a small scale/slide and staggered item fades. Interrupted closes cannot hide a replacement menu; Windows' reduced-motion preference is respected.
- Preserve position and size momentum when compositor tile reflow is retargeted. Allow the application's native close transition to finish before expanding neighboring tiles; the removed application's pixels are not captured or replayed after its HWND dies.
- Show Launchpad on the first click without awaiting catalog export, an icon batch or invisible GPU warm-up. Preload Start pins after the dock's first paint and keep an optional local catalog cache for subsequent starts.
- Add desktop checks for a real Win+Space change, two mouse-selected layouts on a separate input thread, tray callback coordinates, cold Launchpad latency, interrupted menus, repeated tile resizing and close reflow.

## 1.4.3

- Keep a minimize cycle active until the window actually restores. Late duplicate native acknowledgements no longer start another compositor image after the first one disappears; late restore acknowledgements do not cancel a currently minimized image.
- Use the dock FPS and a gentle starting curve for untiled minimization. Ignore minimize-time geometry changes and target the current dock icon instead of a cached earlier click position.
- Add untiled titlebar/dock-command cycles, delayed native event replay, post-animation rendered-pixel checks and rapid restore checks.

## 1.4.2

- Keep the dock, menu bar and custom tray visible and stationary during screenshot selection. Freeze hover/autohide and topmost reassertion while capture owns input; continue passing screenshot shortcuts to Windows.
- Give windows reporting zero native minimums a useful DPI-scaled 480×320 fallback. Reflow mixed windows instead of compressing an unknown browser into a narrow strip.
- Preserve window proportions during dock-directed minimization, use a curved center path and fade only near the destination. Use the dock animation duration for tiling concealment and fit restoring representations into the dock icon.

## 1.4.1

- Animate user minimization towards dock icons both with and without tiling, including a dock hidden under fullscreen apps. Remove minimized windows from active tiles and restore them from the dock.
- Observe PrintScreen and Win+Shift+S without consuming input; dismiss tray surfaces immediately and release Explorer menu mode before capture. Stop transition surfaces during screenshot selection.
- Separate preferred split ratios from temporary minimum-size constraints: equal halves return when they fit, while explicit user resizing is retained.
- Show an empty search palette on first use. Filter Windows utilities and uninstallers in the palette; learn frequent suggestions only from successful app launches and persist them locally.
- Avoid repeated monitor enumeration and repeated per-window layout calculation during reconciliation; use native clearing for transparent compositor viewports.

## 1.4.0

- Reflow mixed minimum sizes with adjustable split ratios and compact slicing layouts. An 800×600 player joins three existing tiles instead of covering two of them; restored members and split edits revalidate their constraints.
- Place application windows directly at final tile bounds. Use finite DWM representations for opening, layout resize/movement and workspace animations towards resting dock icons, without per-frame client resizing or Explorer injection.
- Preserve presented position and opacity when reversing transitions. Restore temporary styles/opacity on cancellation and shutdown; repair late application self-resizes and bound refused placements.
- Use the dock's high-resolution, sleeping frame scheduler for tiling and menu-bar motion. Keep callbacks bounded and respect configured FPS and reduced motion.
- Add mixed-window, actual Spotify opt-in, rendered minimized-thumbnail, workspace cancellation, startup privacy, late-resize and busy-owner restoration checks.

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
