FloatingDock 1.5.10 fixes intermittent window animation handoffs and incorrect native minimize destinations.

- Decode packed shell coordinates without depending on trailing zero bytes; handle short payloads at a memory-page boundary.
- Keep dock destinations available for hidden transition frames and fall back to the nearest dock when process identity is unavailable.
- Check the first DWM thumbnail update and commit its initial frame before minimizing or repositioning the source. Failed thumbnail setup falls back to the native window path.

Existing animation durations, frame rates and effects are retained.

### Downloads

- **FloatingDock-Setup-x64.exe** — per-user installer.
- **FloatingDock-Portable-x64.zip** — extract the entire archive and run FloatingDock.exe.
- **SHA256SUMS.txt** — SHA-256 hashes of both downloads.

.NET and Windows App SDK are included. Windows 10 1809+ / Windows 11 x64; unsigned builds.

**По-русски:** исправлены ошибки чтения координат Windows, из-за которых сворачивание могло уходить к прежнему месту панели задач. Сохранена привязка к доку для скрытых переходных состояний и недоступной информации о процессе. Перед сворачиванием проверяется готовность первого кадра DWM; неудачная подготовка изображения не используется для анимации. FPS, длительности и эффекты сохранены.
