<p align="center"><img src="assets/AppIcon.png" width="88" alt="FloatingDock icon"></p>
<h1 align="center">FloatingDock</h1>
<p align="center">A floating dock, Launchpad and glass menu bar for Windows.</p>
<p align="center"><strong>English</strong> · <a href="README.ru.md">Русский</a></p>

FloatingDock brings a compact desktop interface to Windows: animated icon magnification, an app launcher, live window previews and a rounded top bar. The system part is a small native engine written in Rust; the interface is built with Flutter (Material 3). Placement, icons and colours are configurable.

## Features

- **A dock that fits your desktop.** Place it at the bottom, left or right; adjust icon size, spacing, alignment and edge distance.
- **Smooth interaction.** Animated magnification, app indicators, reordering, appearance and dismissal. Fullscreen video hides the panels; screen capture keeps them available.
- **Pinned and running apps.** Launch, switch and minimize applications from one place. Steam helper windows are grouped with the main client.
- **Live previews.** Hover after a configurable delay, click a thumbnail to open its window, or use its close button. Closing fades the preview before sending a normal window-close request.
- **Launchpad.** Windows Start pins and apps you add with the + button or by dropping a shortcut. Three built-in launcher icon styles.
- **A rounded menu bar.** Active application, network, volume, input language, battery when available, clock and calendar. Media playback controls, a FloatingDock menu of real Windows notification icons and search. Configurable auto-hide FPS, timing and work-area reservation.
- **Search palette.** `Ctrl+Alt+K` searches apps and familiar personal file types in Desktop and Downloads. An empty query starts blank and later suggests apps frequently opened through search. Windows system clutter is filtered. A bounded 30-second filename cache avoids scanning on every keystroke; Documents and build noise are excluded. Configure scopes and depth in Settings → Search.
- **Interface language.** English by default; switch to Russian in Settings → Appearance.
- **Your own icons.** PNG/ICO overrides and imported ZIP packs shared by the dock and Launchpad. Decorative icon packs are not bundled.
- **Local glass effects.** App-owned blur works independently of the Windows transparency toggle; transitions respect the system animation preference.
- **Taskbar controls.** Keep the Windows taskbar, use auto-hide or hide it completely, with recorded state and restoration tools.

## Download and run

Get the latest Windows x64 build from [Releases](https://github.com/melloghh/FloatingDock/releases/latest).

| Package | How to use it |
|---|---|
| [FloatingDock-Setup-x64.exe](https://github.com/melloghh/FloatingDock/releases/latest/download/FloatingDock-Setup-x64.exe) | Install for the current user. Desktop shortcut and startup options are available. |
| [FloatingDock-Portable-x64.zip](https://github.com/melloghh/FloatingDock/releases/latest/download/FloatingDock-Portable-x64.zip) | Extract the **entire archive**, then run `FloatingDock.exe`. Keep the EXE with its DLLs and resources. |

Since 2.0.1 the engine (`FloatingDock.exe`) is a single native program written in Rust and the interface is Flutter (`ui\`), so no runtime installation is required (the download is about 13 MB, the installer 11 MB). Windows x64 is required; the project targets Windows 10 1809 and later and is tested locally on Windows 11. Builds are currently unsigned, so Windows may show a SmartScreen prompt.

Open FloatingDock settings from the main menu or the dock/tray context menu. The top-bar gear opens Windows Settings. The release includes `SHA256SUMS.txt` for checking downloads and license notices for bundled components.

## Quick controls

Search, quick menus, Launchpad and Settings share a translucent glass material. Launchpad and the main menu include **Shut down** and **Restart**, each with a confirmation whose default is Cancel. Recognized native title-bar minimize buttons and dock clicks use the same dock-directed animation; restoring from the dock reverses it. Custom caption controls, direct system commands and inaccessible windows retain a native fallback. New windows launched through FloatingDock get a finite opening transition after initial sizing settles. Motion respects the animation settings.

The tray opens its surface immediately while Explorer discovery continues. Recent live snapshots are reused for five seconds; each click still revalidates its target. Small notification images are shown at their real resolution instead of enlarged, preserving their status imagery. Their available detail depends on the image supplied by Windows/the application.

| Action | Control |
|---|---|
| Open Launchpad | Dock launcher icon or `Ctrl+Alt+Space` |
| Search apps and files | `Ctrl+Alt+K` or the top-bar search icon |
| Focus the dock | `Ctrl+Alt+D`, then arrows, Enter and Esc |
| Open FloatingDock settings | Main menu or dock/tray context menu |
| Open Windows Settings | Top-bar gear |
| Change dock placement | Settings → Dock |
| Configure the top bar | Settings → Menu Bar |
| Change an icon | Right-click an app → Change icon |
| Import an icon pack | Settings → Icon Packs → Import ZIP |
| Restore the Windows taskbar | `Ctrl+Alt+Shift+T` or the tray menu |

Settings, pins, icons and logs stay in `%LOCALAPPDATA%\FloatingDock`, including when using the portable build. Updates preserve these files.

If FloatingDock was forcibly terminated and the taskbar remains hidden, use the installed taskbar-restoration shortcut or run:

```powershell
.\FloatingDock.exe --restore-taskbar
```

Do not delete `taskbar-state.json` until restoration succeeds. The **Hide completely** mode hides the original taskbar. The top-bar chevron opens FloatingDock's menu of Windows notification icons; choose **Leave** to retain the original taskbar. Media transport controls require an application that exposes a Windows media session.

Wi-Fi scanning follows Windows permissions. Windows may require location access to list nearby wireless networks; FloatingDock does not bypass that restriction.

## Build from source

Needs Windows x64, Rust (stable, MSVC toolchain), and for the interface Flutter. Inno Setup 6 is needed only for the installer.

```powershell
git clone https://github.com/melloghh/FloatingDock.git
cd FloatingDock\rust
cargo test --workspace
cargo build --release -p fd-engine          # rust\target\release\fd-engine.exe
cd ..\ui_flutter\app
flutter build windows --release --no-tree-shake-icons
cd ..\..
.\packaging\release-rust.ps1 -Out ..\release -Ui ui_flutter\app\build\windows\x64\runner\Release
```

`release-rust.ps1` assembles the portable folder (`FloatingDock.exe` is the engine and starts `ui\fd_ui.exe` itself), the zip, the installer and `SHA256SUMS.txt`. GitHub Actions runs the Rust tests and builds the engine; a release is assembled and published by hand. Releases up to 1.6.6 were a C# / WinUI 3 application: see the tag `v1.6.6`.

## Project layout

| Directory | Purpose |
|---|---|
| `rust` | The engine: `fd-core` (settings, apps, icons, search, taskbar safety), `fd-ipc` (protocol), `fd-windows` (Win32), `fd-engine` (the program) |
| `ui_flutter` | The interface (Flutter) and the Dart client of the engine protocol |
| `assets` | Application icon and images |
| `packaging` | Release script, Inno Setup installer, portable extras |

See [Architecture](docs/ARCHITECTURE.md), [Icon-pack format](docs/ICON-PACKS.md) and [Changelog](docs/CHANGELOG.md).

The version history and individual fixes are recorded in [the changelog](docs/CHANGELOG.md). Local desktop suites cover previews, language changes, Launchpad, search cancellation, and actual Zen restoration. File search caches personal filenames; bounded snapshots may return partial results. Tray compatibility depends on Explorer accessibility and Windows icon snapshots, which may lag a state change. Testing across multiple physical monitors and DPI combinations is limited. Launchpad has no folders or pages.

Report reproducible problems through [GitHub Issues](https://github.com/melloghh/FloatingDock/issues/new/choose), including the app version, Windows version, display scaling and steps to reproduce.

## License and credits

[MIT](LICENSE). Copyright © 2026 Mello.

The original MIT-licensed Windhawk Floating Dock 1.5.0 reference by Mello is preserved with its attribution in [docs/reference](docs/reference/win11-floating-dock-1.5.0.cpp). This project is a standalone application and does not require Windhawk. App names and third-party icons belong to their respective owners; user icon packs are not included in this repository.
