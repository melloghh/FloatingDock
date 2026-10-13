FloatingDock 1.5.1 reduces CPU work in top-bar menu animations and avoids repeated native buffer creation during tiling transitions.

### Download

- **FloatingDock-Setup-x64.exe** — install for the current user; optional desktop shortcut and startup.
- **FloatingDock-Portable-x64.zip** — extract the whole archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes for both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; local desktop validation was performed on Windows 11. Builds are unsigned, so Windows may show a SmartScreen prompt.

### Highlights

- Menu scale, slide and fade run on the compositor; native glass boundaries use a bounded synchronization clock. Animations, rapid reversal and reduced motion are preserved.
- Duplicate glass updates and full-window repaint requests are removed from the animation loop.
- Tiling reuses transparent destination buffers, skips identical thumbnail updates and releases its cache on disable/shutdown.
- In a repeatable local workload on a 12-thread machine, twelve menu cycles used about 47% less process CPU time (3266 → 1734 ms); six owned-window openings used about 17% less (750 → 625 ms). Results depend on hardware and workload; the window benchmark includes fixture rendering.
- Desktop checks cover menu clipping, search toggling, tiled minimize/restore, rapid reversal, bounded buffer reuse and restored source visibility.

### Getting started

Open settings from the top-bar gear. The interface defaults to English; Russian is available in Appearance. Tiling is off by default: enable it in Tiling manager. Ctrl+Alt+Space opens Launchpad; Ctrl+Alt+K opens search. Updates preserve settings and icons in %LOCALAPPDATA%\FloatingDock.

If the Windows taskbar stays hidden after forced termination, use the restoration shortcut or run `FloatingDock.exe --restore-taskbar`.

### Validation and support

Core and native Windows tests run in GitHub Actions before tagged releases are published. Local desktop checks also cover window restoration, workspaces, menu animation and actual Zen restoration. Multiple physical monitors, mixed DPI and unusual application frames have limited test coverage. FloatingDock workspaces are local to the current session, rather than Windows virtual desktops.

[Report a problem](https://github.com/melloghh/FloatingDock/issues/new/choose) · [Tiling controls](https://github.com/melloghh/FloatingDock/blob/main/docs/TILING.md) · [Full changelog](https://github.com/melloghh/FloatingDock/blob/main/docs/CHANGELOG.md)

---

**По-русски:** В 1.5.1 снижена нагрузка анимаций верхней панели и повторное создание буферов тайлинга. В локальном сравнении расход CPU уменьшился примерно на 47% для меню и на 17% для тестового сценария окон; результат зависит от устройства и действий. Setup устанавливает приложение для текущего пользователя; Portable нужно распаковать целиком. Настройки и иконки сохраняются. Сборка пока без цифровой подписи.
