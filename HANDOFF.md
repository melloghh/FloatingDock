# HANDOFF: Flutter-интерфейс FloatingDock

Ветка `flutter-ui`. WinUI-версия остаётся рабочим запасным вариантом (`classic\`), ничего из неё не удалено.

## Архитектура (решения)

- **Движок** `src/FloatingDock.Engine` (net10, win-x64, self-contained, WinExe): вся системная логика (трей, горячие клавиши, панель задач, список окон, настройки, иконки, медиа, сеть, звук). Без окон.
- **UI** `ui_flutter/app` (Flutter, только Windows): один процесс, один движок Flutter, много нативных HWND (`FlutterDesktopViewControllerCreate`), в Dart `runWidget` → `ViewCollection`. Дополнительные окна не уничтожаются, а прячутся и переиспользуются (ленивое создание).
- **IPC**: Unix-domain socket `<профиль>\engine.sock`, JSON по строкам (вместо named pipe: в Dart он доступен без FFI). Протокол: `ui_flutter/protocol/PROTOCOL.md`; зеркала: `src/FloatingDock.Ipc/Protocol.cs` и `ui_flutter/fd_ipc/lib/protocol.dart`.
- **Прозрачность**: только `DwmExtendFrameIntoClientArea(-1)`, WS_POPUP, topmost, tool window, NOACTIVATE; поля 28 DIP вокруг контента, без регионов окна и без blur рабочего стола в анимируемых окнах (ничего не обрезается).
- **Состояние**: `AppState` (настройки, закрепления, окна, тексты, иконки, тема), оптимистичные обновления с коалесцированием 60 мс.
- **Тема**: `ColorScheme.fromSeed` (мята 4E9F75 по умолчанию, графит 5F6368), светлая и тёмная; верхняя панель всегда тёмная.
- **Движение**: `PopSurface`, `Reveal`, `GlideList`, `GlideScope/GlideRow`, `WavyProgress` (`lib/ui`). Анимации WinUI не менялись.
- **Док**: один `CustomPainter`, математика (`dock_math.dart`) — порт Magnification/DockMotion/DockLayout, поддержаны Bottom/Left/Right.
- **Выбор интерфейса**: настройка `UiMode` ("Flutter" по умолчанию / "Classic"), переключение в Настройки → О программе → Интерфейс; движок и WinUI передают управление друг другу.
- **Раскладка сборки**: `<dist>\FloatingDock.Engine.exe` + `ui\fd_ui.exe` + (необязательно) `classic\FloatingDock.exe`.
- **Сборка Flutter**: только `flutter build windows --release --no-tree-shake-icons` (иначе tree-shaking режет шрифт иконок Material).
- **Диагностический профиль** (`--diagnostic-profile <папка>`): без трея, горячих клавиш, автозапуска, скрытия панели задач и прогрева закреплений «Пуска». Нужен для безопасных проверок на копии профиля.

## Как собрать и запустить

```powershell
# движок
dotnet build src\FloatingDock.Engine\FloatingDock.Engine.csproj -c Release -o .validation\engine1
# UI (из ui_flutter\app)
flutter build windows --release --no-tree-shake-icons
# сборка папки (ui_flutter\tools)
.\make-dist.ps1 -SkipClassic            # без WinUI; без флага собирает и classic\
.\.validation\dist1\FloatingDock.Engine.exe    # запуск: движок сам поднимает UI
```

Проверки:

- `flutter test` в `ui_flutter\app` — виджет- и golden-тесты (шрифты берутся из Windows).
- `ui_flutter\tools\with-engine.ps1` — IPC-проверки `fd_ipc\tool\ipc_check*.dart` против живого движка на диагностическом профиле.
- `ui_flutter\tools\live-check.ps1 -Shots <папка> [-Quick]` — живой прогон на копии профиля: скриншоты, холостая нагрузка, чистое завершение.

Результат последнего живого прогона: простой UI 0.40 % CPU / 121 МБ, движок 0.27 % / 102 МБ, `--quit` завершает оба процесса и удаляет сокет.

## Что не проверено / не сделано

- Превью окон (DWM-миниатюры) при наведении на док: ни одно запущенное приложение не было наведено.
- Перетаскивание файлов на Launchpad (путь OLE drop).
- Реальные трей, горячие клавиши, скрытие панели задач: в недиагностическом режиме не запускались.
- Launchpad на холодном диагностическом профиле дольше 6 с показывает «Загрузка закреплений»: в обычном режиме закрепления прогреваются при старте, но это не проверялось вживую.
- Менеджер тайлинга (острова рабочих пространств пусты), подсказки верхней панели, скрытие поверх оверлеев захвата экрана не перенесены.
- Установщик и релиз не делались (по указанию). WinUI не удалялся. Ничего не пушилось и не тегировалось.
