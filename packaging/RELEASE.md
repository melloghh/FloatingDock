FloatingDock 1.5.2 fixes the menu animation regression in 1.5.1 and reduces CPU usage when moving the pointer across top-bar buttons.

### Download

- **FloatingDock-Setup-x64.exe** — install for the current user; optional desktop shortcut and startup.
- **FloatingDock-Portable-x64.zip** — extract the whole archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes for both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; local desktop validation was performed on Windows 11. Builds are unsigned, so Windows may show a SmartScreen prompt.

### Highlights

- Menu content and native glass boundaries share one animation clock again, including rapid reversal and reduced motion.
- Top-bar buttons use lightweight local hover and pressed highlights, retaining clicks, keyboard focus, tooltips and workspace selection.
- A local A/B workload with native mouse input measured about 2.79% → 1.97% total-machine CPU for stock versus lightweight buttons in one process on a 12-thread machine. Results depend on hardware and workload.
- Tiling buffer reuse from 1.5.1 is retained. Desktop checks now include actual pointer movement and intermediate menu poses as well as search toggling and native glass clipping.

### Getting started

Open settings from the top-bar gear. The interface defaults to English; Russian is available in Appearance. Tiling is off by default: enable it in Tiling manager. Ctrl+Alt+Space opens Launchpad; Ctrl+Alt+K opens search. Updates preserve settings and icons in %LOCALAPPDATA%\FloatingDock.

If the Windows taskbar stays hidden after forced termination, use the restoration shortcut or run `FloatingDock.exe --restore-taskbar`.

### Validation and support

Core and native Windows tests run in GitHub Actions before tagged releases are published. Local desktop checks also cover window restoration, workspaces, menu animation and actual Zen restoration. Multiple physical monitors, mixed DPI and unusual application frames have limited test coverage. FloatingDock workspaces are local to the current session, rather than Windows virtual desktops.

[Report a problem](https://github.com/melloghh/FloatingDock/issues/new/choose) · [Tiling controls](https://github.com/melloghh/FloatingDock/blob/main/docs/TILING.md) · [Full changelog](https://github.com/melloghh/FloatingDock/blob/main/docs/CHANGELOG.md)

---

**По-русски:** В 1.5.2 исправлена регрессия анимаций меню из 1.5.1 и снижена нагрузка при движении курсора по кнопкам верхней панели. В локальном A/B-сравнении CPU при активном движении мыши уменьшился с 2,79% до 1,97%; результат зависит от устройства и действий. Настройки и иконки сохраняются. Setup и Portable пока без цифровой подписи.
