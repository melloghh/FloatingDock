FloatingDock 1.6.3 fixes the normal window-animation path and removes the blue tint from glass menus.

- Menus, search, Launchpad and Settings now use neutral translucent gray surfaces, highlights and borders. Blur remains enabled locally, and the top bar keeps its original appearance.
- Ordinary application windows use Windows DWM startup/minimize/restore transitions. They are no longer made transparent and replaced by a live thumbnail while their content is still initializing or repainting.
- Native caption minimize commands and dock actions retain the dock icon as their shell animation destination. Icon pulses, menu/panel animation and opt-in tiling transitions remain available.
- The caption mouse hook is installed only when needed by active tiling, reducing background work in the normal desktop mode.

Validation includes real fresh Explorer windows and an isolated Chrome profile, captured startup/restore frames, source-composition checks, native caption system commands, dock actions, tiling/workspaces and UI interaction checks. Browser fixture content is local; captures/profiles are private and are not distributed. Windows' existing animation preferences are respected; system animation/transparency settings are unchanged.

### Downloads

- **FloatingDock-Setup-x64.exe** — per-user installer.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes of both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; unsigned builds. Existing settings, pinned apps and personal icons are preserved.

### По-русски

Убран принудительный синий оттенок меню, поиска, Launchpad и настроек: стекло теперь нейтрально-серое, с прежним размытием. Верхняя панель сохраняет исходный вид.

Для обычных окон открытие, сворачивание и восстановление выполняет Windows DWM. FloatingDock больше не скрывает запускающееся окно и не подменяет его миниатюрой, которая могла содержать ещё неготовый кадр. Сворачивание к значку дока, анимации иконок и меню сохранены. Собственные переходы тайлинга остаются в режиме тайлинга.

Проверены реальные новые окна Проводника и отдельного профиля Chrome, запуск и восстановление с записью кадров, команды кнопок окна и дока, тайлинг и рабочие области. Системные настройки анимаций/прозрачности и пользовательские данные не меняются.
