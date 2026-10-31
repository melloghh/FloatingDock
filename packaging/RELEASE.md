FloatingDock 1.6.5 adds a mode where maximized and full-screen windows open above the dock instead of covering it.

- New option "Keep the dock visible: fullscreen windows open above it" (Settings → Dock → Hiding, off by default). The dock's strip stays free, maximized windows end above it and the dock is not hidden for them.
- Windows that cover the whole monitor (F11, borderless full screen) are moved above the strip once, without stealing focus; applications that insist on their own bounds are left alone. Exclusive full-screen games still hide the dock.
- Needs the Windows taskbar auto-hidden or hidden and the dock not set to auto-hide.

Validation includes policy unit tests, a diagnostic that moves a monitor-sized window above a reserved strip, a live check that a maximized window ends above the reserved dock area, and the existing maximized-state, menu and input desktop suites.

### Downloads

- **FloatingDock-Setup-x64.exe** — per-user installer.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes of both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; unsigned builds. Existing settings, pinned apps and personal icons are preserved.

### По-русски

Новый режим «Не скрывать док: полноэкранные окна открываются над ним» (Настройки → Док → Скрытие, по умолчанию выключен). Место дока остаётся свободным: развёрнутые окна заканчиваются над ним, док для них не скрывается.

Окна на весь монитор (F11, полноэкранные без рамки) один раз сдвигаются выше дока без перехвата фокуса; приложения, которые настаивают на своих размерах, не трогаются. Игры в эксклюзивном полноэкранном режиме по-прежнему скрывают док.

Нужна скрытая или автоскрытая панель задач Windows и док без автоскрытия.
