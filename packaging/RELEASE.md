FloatingDock 2.0.1 fixes the dock and the window previews, adds two options, and moves the engine to Rust.

- Fixed: the dock stopped reacting to the cursor while a window preview was open; it stayed hidden after a full-screen or maximized window was minimized; previews of minimized windows were narrow and empty and opened with a lag; the player progress jumped back to zero a few seconds after play.
- New: search can open in the middle of the screen, growing out of the dock (Settings > Search); the top bar comes in three styles: separate pills, one floating panel, or a panel flush with the screen edge (Settings > Menu Bar).

- Changed: the engine (`FloatingDock.exe`) is rewritten in Rust, with the same protocol and the same settings and data files. The portable build shrinks from 131 MB to 32 MB (zip 13 MB), the installer from 73 MB to 11.5 MB; the engine idles at about 0.05 % CPU and 40 MB of memory. Not ported yet: the right-click menu of a tray icon in the bar's hidden-icons menu.

Validation: Rust test suites (core, IPC, Windows layer); the fixes and both options above were checked live on Windows 11 (build 26300) with the previous engine; the Flutter interface was run on the Rust engine on Windows 11 (build 26300): bar, dock, icons, Launchpad, search, hidden tray icons, media, hiding and restoring the Windows taskbar, a clean quit; the installer was checked with a silent install and uninstall. Not checked live yet: the screenshot overlay (Win+Shift+S) with the new engine and the minimize animation target.

FloatingDock 2.0.0 brought a new interface: Material 3, rebuilt in Flutter, running next to a windowless C# engine.

- Top bar as pill islands with a now-playing capsule, wavy player progress, workspaces and a clock pill; palette from a seed colour (mint or graphite), light and dark theme.
- Own motion for menus, search, Launchpad and settings; the dock is drawn in one pass with magnification and window previews.
- New: blur behind translucent surfaces (Settings > Appearance), the dock reappears at the screen edge when a maximized window hides it, and screenshots (Win+Shift+S) keep the bar and dock in place under the selection overlay.
- FloatingDock.exe is now the engine and starts the interface from the ui folder. The WinUI application of 1.x is not included; settings, pinned apps and icon packs are kept.

Validation: IPC, Core and Flutter test suites; the screenshot overlay was checked live on Windows 11 (build 26300).

Previously, in 1.6.6: full-screen video hides the dock even when "Keep the dock visible" is on.

- A browser or media player (Chrome, Edge, Firefox, Zen, Brave, Opera, Vivaldi, VLC, mpv, PotPlayer, MPC and others) at full screen covers the whole monitor and hides the dock. Other applications still open above the dock; exclusive full-screen games still hide it.
- Note: F11 in a browser is treated the same way as a video.

Validation includes policy unit tests and the existing Core and Windows test suites.

### Downloads

- **FloatingDock-Setup-x64.exe** — per-user installer.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes of both downloads.

No .NET or other runtime is needed. Windows 10 1809+ / Windows 11 x64; unsigned builds. Existing settings, pinned apps and personal icons are preserved.

### По-русски

Полноэкранное видео в браузере или плеере (Chrome, Edge, Firefox, Zen, Brave, Opera, Vivaldi, VLC, mpv, PotPlayer, MPC и др.) снова скрывает док, даже когда включён режим «Не скрывать док». Остальные приложения по-прежнему открываются над доком; игры в эксклюзивном полноэкранном режиме тоже скрывают док.

F11 в браузере обрабатывается так же, как видео.
