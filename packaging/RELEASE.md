FloatingDock 1.5.5 fixes doubled window images during minimization.

### Download

- **FloatingDock-Setup-x64.exe** — installer for the current user.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes for both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64. Builds are unsigned.

### Changes

- Titlebar/system minimization now uses one native Windows animation, directed toward the current dock icon. The dock no longer starts another shrinking copy after Windows has begun minimizing the window.
- Clicking a dock icon preserves the dock's animation. The source is hidden only after Windows accepts disabling its native transition; unsupported windows keep the native fallback. Existing workspace animations remain available.
- Native animation destinations use current icon geometry, avoiding stale click positions.
- Extend regression checks to titlebar and dock minimization, intermediate frames with a separate-process fixture, rapid restore, delayed native notifications and source-opacity restoration.

Settings, icons, dock appearance and menu animations are preserved. Native titlebar transitions follow Windows animation timing; dock-click transitions retain the configured dock timing.

### Validation and support

Core and Windows tests run in GitHub Actions before publication. Desktop regression scenarios use dedicated windows and isolated test profiles. Multiple physical monitors, mixed DPI and unusual application frames have limited coverage.

[Report a problem](https://github.com/melloghh/FloatingDock/issues/new/choose) · [Full changelog](https://github.com/melloghh/FloatingDock/blob/main/docs/CHANGELOG.md)

---

**По-русски:** В 1.5.5 исправлено раздвоение окна при сворачивании. Кнопка «—» использует одну системную анимацию Windows с направлением к текущей иконке дока. При клике по значку сохраняется анимация дока; перед её началом проверяется отключение системного перехода для этого окна. Если Windows не позволяет передать управление, остаётся системная анимация. Настройки, иконки, внешний вид дока и анимации меню сохранены. Для кнопки «—» длительность задаёт Windows, для клика по значку — настройки дока. Setup и Portable без цифровой подписи.
