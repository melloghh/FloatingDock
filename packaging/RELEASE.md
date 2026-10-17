FloatingDock 1.5.6 fixes the tiled-window opening/browser restoration regression introduced in 1.5.5.

The window source now establishes its layered style and opacity before changing the DWM transition policy, restoring the setup order used before 1.5.5. Native-transition failure rollback and protection against a second minimization image remain in place. Dock appearance, configured animation timing, tiling settings and custom icons are preserved.

### Downloads

- **FloatingDock-Setup-x64.exe** — installer for the current user.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes for both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; builds are unsigned.

Validation includes real-browser dock restore cycles and rapid reversal with tiling enabled, tile opening, source-opacity restoration and titlebar/dock minimization. Automated Core/Windows tests run before tagged publication.

[Report a problem](https://github.com/melloghh/FloatingDock/issues/new/choose) · [Full changelog](https://github.com/melloghh/FloatingDock/blob/main/docs/CHANGELOG.md)

---

**По-русски:** 1.5.6 исправляет регрессию открытия окон и восстановления браузера при включённом тайлинге после 1.5.5. Возвращён прежний порядок подготовки прозрачности и оконной композиции. Исправление раздвоения при системном сворачивании сохранено. Внешний вид, длительности анимаций, настройки тайлинга и иконки не меняются.
