FloatingDock 1.5.9 reduces animation overhead and fixes Settings, hosted application tracking, search history and application/icon imports.

### Improvements

- Reuse frame callbacks, tiling buffers and application identities instead of creating temporary objects on every animation frame. Keep the existing frame rates, animation curves, durations and glass effects.
- Avoid temporary enumerators in dock layout and rendering. Update the input-language label only when its language changes, retaining the existing polling responsiveness.
- In a local three-run comparison at 144 FPS on a 12-thread PC, moving across the dock used about 23% less CPU on average (1.12% to 0.87% of total machine CPU). This is a workload-specific measurement; idle and menu CPU improvements were not consistently demonstrated.

### Fixes

- Prevent queued theme, language and shortcut-status updates from accessing a closed Settings window.
- Validate hosted windows against their native frame owner, preventing repeated identity resolution and missing dock entries.
- Handle malformed search history, normalize identity comparisons, bound loaded entries and prevent usage-count overflow.
- Preserve names when importing icon packs without a manifest and protect built-in pack names regardless of casing.
- Reset equivalent application icon identities from Settings.
- Report application imports as successful only after saving; clean up copied shortcuts if saving fails and report drag-and-drop failures accurately.

### Downloads

- **FloatingDock-Setup-x64.exe** — installer for the current user.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes for both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; builds are unsigned.

Validation: 340 Core/Windows tests and 20 local desktop suites at 144 FPS, covering native input, tooltips, menus/glass, input languages, search, previews, tiling, workspaces and native minimize destinations. Animation frame loops still stop after transitions settle. Multi-monitor mixed-DPI setups and every third-party application were not exhaustively tested.

[Report a problem](https://github.com/melloghh/FloatingDock/issues/new/choose) · [Full changelog](https://github.com/melloghh/FloatingDock/blob/main/docs/CHANGELOG.md)

---

**По-русски:** уменьшены лишние выделения памяти и повторная работа во время анимаций; FPS, длительности, кривые и стеклянные эффекты сохранены. В локальном сравнении трёх прогонов при 144 FPS нагрузка при движении по доку снизилась примерно на 23%; устойчивое снижение для покоя и неподвижного меню не подтверждено. Исправлены падение при закрытии настроек, определение hosted-приложений, повреждённая история поиска, импорт приложений и пакетов иконок, сброс эквивалентных иконок. Прошли 340 автоматических тестов и 20 локальных GUI-наборов. Для Portable распакуйте весь архив; установщик — FloatingDock-Setup-x64.exe.
