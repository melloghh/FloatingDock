FloatingDock 1.6.2 fixes stalling and skipped window animations while keeping the original neutral translucent top bar.

- Native thumbnail motion now runs on a separate sleeping frame clock, so UI startup work does not freeze the opening, minimize or restore transition.
- Opening follows an application's live window size, removing the wait at stale startup geometry.
- Retry matching after asynchronous application identification; preserve bounded launch tracking across splash/main-window hand-offs.
- Animate restoration of windows that were already minimized when FloatingDock started. Search and Launchpad prepare matching minimized windows too.
- Synchronize reversals and thumbnail disposal; native layout changes and cleanup stay on the UI thread.

Validated with regression fixtures for startup resizing, packaged identity and busy dispatchers, pixel-based minimize/restore checks, and repeated restores of available real applications. These checks do not guarantee compatibility with every custom/elevated window. Native fallback and reduced motion remain available. Global Windows animation/transparency settings are unchanged.

### Downloads

- **FloatingDock-Setup-x64.exe** — per-user installer.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes of both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; unsigned builds. Existing settings, pinned apps and personal icons are preserved.

### По-русски

Исправлены зависания и пропуски анимаций окон. Отрисовка перехода выполняется отдельно от потока интерфейса; изменение размеров приложения при запуске больше не удерживает изображение на старом месте. Добавлена повторная попытка сопоставления после определения приложения и восстановление окон, свёрнутых ещё до запуска FloatingDock. Поиск и Launchpad также подготавливают свёрнутое окно к анимации восстановления.

Верхняя панель сохраняет прежние нейтральную прозрачность и размытие. Настройки, закрепления и личные иконки сохраняются. Для неподдерживаемых окон остаётся системный вариант; абсолютная совместимость со всеми приложениями не заявляется.
