FloatingDock 1.6.0 brings a shared glass interface, dock-directed window motion and a more responsive notification tray.

- Search now has a rounded input capsule, a separate translucent results panel, compact rows and softer selection.
- Quick menus, Launchpad, the top bar and Settings share a cool glass material. Launchpad tiles enter in a short cascade; Settings pages fade and slide into place.
- Recognized native title-bar minimize buttons and dock clicks use the same curved animation toward the dock. Untiled windows restore with the reverse effect, and new windows launched through FloatingDock get a bounded opening transition after initial sizing settles. Reduced motion is respected.
- Remove the Start/Launchpad hover label.
- Add Shut down and Restart to Launchpad and the main menu. A confirmation defaults to Cancel; applications are not forcibly terminated. Diagnostic profiles never execute power commands.
- Show the tray surface immediately during discovery, coalesce reads, cache recent live snapshots and reuse controls. Right-click keeps the tray visible while the icon's context menu is open and waits for the physical button release before forwarding the action.
- Prefer larger matching notification images and stop enlarging small snapshots, preserving notification status imagery. Image detail depends on what Windows/the application provides.

Native motion remains the fallback for custom caption controls, direct system commands, inaccessible windows and unavailable DWM representations. The mouse hook is local to FloatingDock's process; it does not inject into applications or change the global Windows animation/transparency settings.

Performance work reduces repeated tray discovery/decoding and reuses a lighter button template. Local CPU results vary by interaction; this release does not claim that all activity peaks have been eliminated. Multiple physical monitors, mixed DPI and every third-party application's custom controls have not been exhaustively tested.

### Downloads

- **FloatingDock-Setup-x64.exe** — per-user installer.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes of both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; unsigned builds. Settings, pinned apps and personal icons are preserved.

### По-русски

- Переработаны поиск и общий стеклянный стиль: отдельная округлая строка поиска, компактные результаты, мягкое выделение, полупрозрачные меню, Launchpad и настройки.
- Системная кнопка «—», когда она распознаётся как стандартная, и клик в доке используют одну плавную анимацию к значку. Восстановление из дока работает в обратную сторону. Новые окна, запущенные через FloatingDock, плавно появляются после начального изменения размеров. Добавлены каскад появления плиток Launchpad и переходы страниц настроек.
- Убрана подпись «Пуск»/Launchpad при наведении. В Launchpad и главном меню появились выключение и перезагрузка с подтверждением и отменой по умолчанию.
- Трей сразу показывает свою поверхность, повторно использует свежий список и готовые элементы. Правый клик сохраняет трей видимым и надёжнее открывает контекстное меню. Маленькие значки больше не растягиваются с размытием; детализация зависит от исходника Windows или приложения.
- Сохранены настройки, закрепления и личные иконки. Общая прозрачность Windows не включается. Для нестандартных кнопок и недоступных окон предусмотрен системный вариант анимации.

Сокращена повторная работа трея и облегчены шаблоны кнопок. Снижение всех пиков CPU не заявляется: результаты зависят от сценария. Реальное выключение/перезагрузка в диагностике не выполнялись. Сборки пока без цифровой подписи.
