FloatingDock 1.5.8 fixes native minimization falling back to the hidden Windows taskbar after launching an application from search.

Windows animation destination requests are now answered by a dedicated message thread using immutable current dock geometry, without waiting for WinUI layout or search work. New windows can match their app icon before the running-window scan finishes; while an unpinned icon is entering, the first minimize still targets the dock. Geometry updates follow dock movement and monitor placement.

Native titlebar minimization remains a single Windows animation. Existing browser opening/restoration composition and custom dock animations are retained. The new thread sleeps between messages; unchanged geometry is not republished.

### Downloads

- **FloatingDock-Setup-x64.exe** — installer for the current user.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes for both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; builds are unsigned.

Validation: 326 Core/Windows tests, including shell replies while the owner thread is blocked. Desktop checks cover a new Zen window launched through search, first-minimize tracking delays, busy UI, left/right dock changes, native shrink trajectories on a solid-color fixture, duplicate-image protection, browser restoration and quick-menu reuse.

[Report a problem](https://github.com/melloghh/FloatingDock/issues/new/choose) · [Full changelog](https://github.com/melloghh/FloatingDock/blob/main/docs/CHANGELOG.md)

---

**По-русски:** исправлен уход анимации сворачивания к скрытой панели задач после запуска из поиска. Ответ Windows с координатами дока больше не ждёт поток интерфейса; первое сворачивание нового окна тоже получает привязку к доку. Сохранены системная анимация без второй копии окна и исправления открытия/восстановления браузера.
