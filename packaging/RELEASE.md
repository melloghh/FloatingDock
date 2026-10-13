FloatingDock 1.5.0 brings the floating dock, Launchpad, search, glass menu bar and built-in window tiling together in a stable Windows x64 release.

### Download

- **FloatingDock-Setup-x64.exe** — install for the current user; optional desktop shortcut and startup.
- **FloatingDock-Portable-x64.zip** — extract the whole archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes for both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; local desktop validation was performed on Windows 11. Builds are unsigned, so Windows may show a SmartScreen prompt.

### Highlights

- Smooth dock magnification, app switching, live previews, custom icons and Launchpad.
- Animated top-bar menus, media controls, input-language selection and a custom Windows tray menu.
- Search that starts empty, learns frequently opened apps and filters Windows system clutter; repeat clicks close it reliably.
- Optional tiling with workspaces, equal default left/right columns for two visible windows, nested layouts for more windows, native minimum sizes and animated dock restore.
- Fixes for minimized windows across workspaces, stale window events, tray context-menu placement, screen capture and browser restoration.
- A built-in blue File Explorer icon, shared across the dock and launcher.

### Getting started

Open settings from the top-bar gear. The interface defaults to English; Russian is available in Appearance. Tiling is off by default: enable it in Tiling manager. Ctrl+Alt+Space opens Launchpad; Ctrl+Alt+K opens search. Updates preserve settings and icons in %LOCALAPPDATA%\FloatingDock.

If the Windows taskbar stays hidden after forced termination, use the restoration shortcut or run `FloatingDock.exe --restore-taskbar`.

### Validation and support

Core and native Windows tests run in GitHub Actions before tagged releases are published. Local desktop checks also cover window restoration, workspaces, menu animation and actual Zen restoration. Multiple physical monitors, mixed DPI and unusual application frames have limited test coverage. FloatingDock workspaces are local to the current session, rather than Windows virtual desktops.

[Report a problem](https://github.com/melloghh/FloatingDock/issues/new/choose) · [Tiling controls](https://github.com/melloghh/FloatingDock/blob/main/docs/TILING.md) · [Full changelog](https://github.com/melloghh/FloatingDock/blob/main/docs/CHANGELOG.md)

---

**По-русски:** Setup устанавливает приложение для текущего пользователя; Portable нужно распаковать целиком. Дополнительные runtime не нужны. Русский язык выбирается в Appearance, тайлинг включается отдельно в Tiling manager. Настройки и иконки сохраняются при обновлении. Сборка пока без цифровой подписи. Подробности — в [русской инструкции](https://github.com/melloghh/FloatingDock/blob/main/README.ru.md).
