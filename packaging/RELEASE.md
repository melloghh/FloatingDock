FloatingDock 1.6.4 fixes windows returning from the dock in a normal size instead of maximized, and simplifies search.

- Windows that were maximized or full-screen (Photoshop and other applications) now stay maximized after being minimized and restored from the dock. Restoring a minimized window no longer sends a second restore request that un-maximized it.
- Search shows applications and commands only. File and folder search and its settings were removed.
- Search results and Settings sections appear with short cascading animations. They respect the Windows animation preference and the menu animation setting.

Validation includes a maximized-window diagnostic reproducing the bug before the fix and passing after it (dock, window button, preview and relaunch paths), the unit tests and the native, tiling, workspace, search, menu and input desktop suites.

### Downloads

- **FloatingDock-Setup-x64.exe** — per-user installer.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes of both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; unsigned builds. Existing settings, pinned apps and personal icons are preserved.

### По-русски

Окна, которые были развёрнуты или открыты на весь экран (Photoshop и другие программы), после сворачивания и восстановления из дока остаются развёрнутыми. Раньше второй запрос восстановления возвращал их в обычный размер.

Поиск показывает только приложения и команды; поиск файлов и папок и его настройки убраны. Результаты поиска и разделы настроек появляются с короткими каскадными анимациями; системные настройки анимаций учитываются.

Проверено диагностикой развёрнутого окна (баг воспроизведён до исправления и исчез после), юнит-тестами и наборами проверок тайлинга, рабочих областей, поиска, меню и ввода.
