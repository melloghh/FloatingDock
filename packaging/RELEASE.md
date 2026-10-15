FloatingDock 1.5.3 reduces CPU usage when moving the pointer over the top bar while preserving menu animations.

### Download

- **FloatingDock-Setup-x64.exe** — install for the current user; optional desktop shortcut and startup.
- **FloatingDock-Portable-x64.zip** — extract the whole archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes for both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; local desktop validation was performed on Windows 11. Builds are unsigned, so Windows may show a SmartScreen prompt.

### Highlights

- Native pointer handling and compositor hover highlights avoid XAML input processing while moving over the bar. Button geometry is cached until layout changes.
- Menu animation timing and glass clipping are preserved, including reversal and reduced motion. Real mouse checks cover all seven menus, second-click dismissal, tooltips, workspace switching and search toggling.
- An external input driver comparing public 1.5.2 with this implementation measured approximately 3.2–3.7% → 0.6–0.8% total-machine CPU while moving across the bar on a 12-thread Windows 11 machine. Results vary with hardware and workload.
- Tiling buffer reuse is retained. Settings and custom icons are preserved.

### Getting started

Open settings from the top-bar gear. The interface defaults to English; Russian is available in Appearance. Tiling is off by default: enable it in Tiling manager. Ctrl+Alt+Space opens Launchpad; Ctrl+Alt+K opens search. Updates preserve settings and icons in %LOCALAPPDATA%\FloatingDock.

If the Windows taskbar stays hidden after forced termination, use the restoration shortcut or run `FloatingDock.exe --restore-taskbar`.

### Validation and support

Core and native Windows tests run in GitHub Actions before tagged releases are published. Local desktop checks also cover window restoration, workspaces, menu animation and actual Zen restoration. Multiple physical monitors, mixed DPI and unusual application frames have limited test coverage. FloatingDock workspaces are local to the current session, rather than Windows virtual desktops.

[Report a problem](https://github.com/melloghh/FloatingDock/issues/new/choose) · [Tiling controls](https://github.com/melloghh/FloatingDock/blob/main/docs/TILING.md) · [Full changelog](https://github.com/melloghh/FloatingDock/blob/main/docs/CHANGELOG.md)

---

**По-русски:** В 1.5.3 обработка движения мыши по верхней панели перенесена в нативный слой. Анимации меню сохранены. В локальном сравнении с публичной 1.5.2 нагрузка при движении курсора снизилась примерно с 3,2–3,7% до 0,6–0,8%; ввод генерировался отдельным процессом. Результат зависит от устройства и действий. Настройки и иконки сохраняются. Setup и Portable пока без цифровой подписи.
