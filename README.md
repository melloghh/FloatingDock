<p align="center"><img src="src/FloatingDock.App/Assets/AppIcon.png" width="88" alt="FloatingDock icon"></p>
<h1 align="center">FloatingDock</h1>
<p align="center">A floating dock, Launchpad and glass menu bar for Windows.</p>
<p align="center"><strong>English</strong> · <a href="README.ru.md">Русский</a></p>

FloatingDock brings a compact desktop interface to Windows: animated icon magnification, an app launcher, live window previews and a rounded top bar. It is a standalone C# / WinUI 3 application with configurable placement, custom icons and dark glass surfaces.

## Features

- **A dock that fits your desktop.** Place it at the bottom, left or right; adjust icon size, spacing, alignment and edge distance.
- **Smooth interaction.** Animated magnification, app indicators, reordering, appearance and dismissal. Fullscreen video and screen-capture overlays suppress the panels.
- **Pinned and running apps.** Launch, switch and minimize applications from one place. Steam helper windows are grouped with the main client.
- **Live previews.** Hover for 750 ms, click a thumbnail to open its window, or use its close button. Closing fades the preview before sending a normal window-close request.
- **Launchpad.** Search Windows Start pins and apps you add yourself, with cached tiles and a compact search field.
- **A rounded menu bar.** Active application, network, volume, input language, battery when available, clock and calendar. Optional auto-hide and work-area reservation.
- **Your own icons.** PNG/ICO overrides and imported ZIP packs shared by the dock and Launchpad. Decorative icon packs are not bundled.
- **Local glass effects.** App-owned blur works independently of the Windows transparency toggle; transitions respect the system animation preference.
- **Taskbar controls.** Keep the Windows taskbar, use auto-hide or hide it completely, with recorded state and restoration tools.

## Download and run

Get the latest Windows x64 build from [Releases](https://github.com/melloghh/FloatingDock/releases/latest).

| Package | How to use it |
|---|---|
| `FloatingDock-Setup-x64.exe` | Install for the current user. Desktop shortcut and startup options are available. |
| `FloatingDock-Portable-x64.zip` | Extract the **entire archive**, then run `FloatingDock.exe`. Keep the EXE with its DLLs and resources. |

Release builds include .NET and Windows App SDK runtimes. No separate runtime installation is required. Windows x64 is required; the project targets Windows 10 1809 and later and is tested locally on Windows 11. Builds are currently unsigned, so Windows may show a SmartScreen prompt.

## Quick controls

| Action | Control |
|---|---|
| Open Launchpad | Dock launcher icon or `Ctrl+Alt+Space` |
| Focus the dock | `Ctrl+Alt+D`, then arrows, Enter and Esc |
| Open settings | Dock/tray context menu or the top-bar gear |
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

Do not delete `taskbar-state.json` until restoration succeeds. The **Hide completely** mode also hides Explorer's system tray and clock; choose **Leave** to retain the original taskbar.

Wi-Fi scanning follows Windows permissions. Windows may require location access to list nearby wireless networks; FloatingDock does not bypass that restriction.

## Build from source

Use Windows x64 with the .NET SDK specified in [global.json](global.json): 10.0.401 or a compatible patch. Inno Setup 6.6.1 is needed only to produce the installer; make `ISCC.exe` available on `PATH`.

```powershell
git clone https://github.com/melloghh/FloatingDock.git
cd FloatingDock
dotnet restore FloatingDock.sln -p:Platform=x64
dotnet build FloatingDock.sln -c Release -p:Platform=x64 --no-restore
dotnet test FloatingDock.sln -c Release -p:Platform=x64 --no-build
.\packaging\publish.ps1 -Zip -Installer -SkipTests
```

Output is written to `artifacts/`. Omit `-Installer` if you only need the portable build. The packaging script checks the published runtime and resources and generates SHA-256 checksums. `-SmokeTest` adds a desktop runtime check and requires an interactive Windows session.

GitHub Actions builds and tests the Windows x64 project and uploads the portable and installer packages as workflow artifacts. Desktop-only UI tests run separately; a CI build alone does not verify fullscreen or pointer interactions.

## Project layout

| Directory | Purpose |
|---|---|
| `src/FloatingDock.Core` | Layout, motion, app matching, persistence and icon-pack logic |
| `src/FloatingDock.Windows` | Windows APIs, shell integration, window tracking and taskbar restoration |
| `src/FloatingDock.App` | WinUI interface, dock, Launchpad, settings and menu bar |
| `tests` | Core and Windows integration tests |
| `packaging` | Portable publication and Inno Setup installer |

See [Architecture](docs/ARCHITECTURE.md), [Icon-pack format](docs/ICON-PACKS.md) and [Changelog](docs/CHANGELOG.md).

Version **1.0.17** passed 248 automated tests and five local desktop verification suites covering menus, previews, dock placement, fullscreen, blur and settings. The latest checks verify rounded menu corners and unchanged background pixels after dismissal. Testing across multiple physical monitors and DPI combinations is limited. Launchpad currently has no folders or pages. The appearance of an application's own closing animation is controlled by that application and Windows.

## License and credits

[MIT](LICENSE). Copyright © 2026 Mello.

The original MIT-licensed Windhawk Floating Dock 1.5.0 reference by Mello is preserved with its attribution in [docs/reference](docs/reference/win11-floating-dock-1.5.0.cpp). This project is a standalone application and does not require Windhawk. App names and third-party icons belong to their respective owners; user icon packs are not included in this repository.
