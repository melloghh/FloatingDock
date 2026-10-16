FloatingDock 1.5.4 reduces repeated work in dock rendering, previews and menus, preserving appearance, configured frame rates and animations.

### Download

- **FloatingDock-Setup-x64.exe** — install for the current user; optional desktop shortcut and startup.
- **FloatingDock-Portable-x64.zip** — extract the whole archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes for both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; local desktop validation was performed on Windows 11. Builds are unsigned, so Windows may show a SmartScreen prompt.

### Highlights

- Cache native window handles, label dimensions and compositor properties; skip unchanged graphics updates and repeated preview-window checks during animation frames.
- Reuse main-menu controls, refresh them when language or auto-hide selection changes, and reset scrolling on every opening. Menu timing, glass clipping, reversal and reduced motion are preserved.
- Reuse the exact saturated magnification integral and skip unused native geometry and motion calculations during tile hiding. Interrupted tile reflows continue from their last presented bounds.
- In three local comparisons with 1.5.3, an external input driver measured average total-machine CPU of approximately 0.99% → 0.75% while moving across the dock on a 12-thread Windows 11 machine, about 23% less. Results vary with hardware and workload; this is not a universal CPU limit. A large repeatable reduction during menu opening or top-bar pointer movement was not established.
- Settings, custom icons, configured FPS, durations and animation curves are preserved.

### Getting started

Open settings from the top-bar gear. The interface defaults to English; Russian is available in Appearance. Tiling is off by default: enable it in Tiling manager. Ctrl+Alt+Space opens Launchpad; Ctrl+Alt+K opens search. Updates preserve settings and icons in %LOCALAPPDATA%\FloatingDock.

If the Windows taskbar stays hidden after forced termination, use the restoration shortcut or run `FloatingDock.exe --restore-taskbar`.

### Validation and support

Core and native Windows tests run in GitHub Actions before tagged releases are published. Local validation passed 323 unit tests and desktop scenarios for real mouse input, keyboard-layout switching in an owned input fixture, all seven menus, search cancellation, previews, rendering, window restoration and workspaces. Reopened-menu scrolling and cache invalidation have dedicated checks. These desktop scenarios use diagnostic-owned windows; multiple physical monitors, mixed DPI and unusual application frames have limited coverage. FloatingDock workspaces are local to the current session, rather than Windows virtual desktops.

[Report a problem](https://github.com/melloghh/FloatingDock/issues/new/choose) · [Tiling controls](https://github.com/melloghh/FloatingDock/blob/main/docs/TILING.md) · [Full changelog](https://github.com/melloghh/FloatingDock/blob/main/docs/CHANGELOG.md)

---

**По-русски:** В 1.5.4 уменьшена повторная работа при отрисовке дока, превью и меню. Внешний вид, анимации, FPS, настройки и иконки сохранены. В трёх локальных сравнительных прогонах с 1.5.3 средний CPU при движении мыши по доку снизился примерно с 0,99% до 0,75% — около 23%. Результат зависит от устройства и сценария; большого устойчивого снижения при открытии меню и движении по верхней панели не подтверждено. Исправлен скачок при прерывании перестроения плиток, проверен сброс прокрутки при повторном открытии меню. Setup и Portable пока без цифровой подписи.
