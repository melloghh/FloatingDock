# Flutter spike (1 Nov 2026)

Flutter 3.47.9 stable (Dart 3.13.7), installed in `C:\dev\flutter`. Project: `ui_flutter/spike`
(`flutter build windows --release`; no plugins, so Windows developer mode is not needed).

## Transparent borderless window
`CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ..., WS_POPUP)`, shown with
`SW_SHOWNOACTIVATE`, then `DwmExtendFrameIntoClientArea(hwnd, {-1,-1,-1,-1})`. Nothing else.
Per-pixel alpha from Flutter reaches the desktop (checked with a 50 % magenta fill over the whole
window), edges and shadow are antialiased, foreground window unchanged on launch.
Rejected: `DwmEnableBlurBehindWindow` with an empty region (makes the panel itself translucent),
`SetWindowCompositionAttribute` accent (works, but a private API and no benefit over the frame trick).
No window region is used anywhere.

## Pop animation
Scale 0.84 -> 1.04 -> 1, rise 14 dip, fade, 400 ms, drawn inside a 28 dip transparent margin.
Frame cost (release, 1 popping window): max build 1.1 ms, max raster 3-9 ms, max total 5-11 ms.
Six windows popping at the same time: max total 27 ms (not a realistic case).

## One process or many
| setup | working set | private | idle CPU (12 logical cpus) |
|---|---|---|---|
| 1 window | 100 MB | 116 MB | 0.01-0.08 % |
| 6 windows, one shared engine | 106 MB | 128 MB | 0.10 % |

Extra windows on a shared engine cost about 2 MB each. Separate processes would cost ~100 MB each,
so that is rejected. The framework's own windowing API (`RegularWindow`...) is still behind
`isWindowingEnabled` / experimental in 3.47, and it cannot set no-activate or transparency.

**Decision: one Flutter process, one engine, many native HWNDs.** The C++ runner creates each window
(styles, DWM frame, AppBar) and attaches a view with `FlutterDesktopViewControllerCreate(w, h, engine)`;
Dart renders each view with `runWidget(ViewCollection(...))` and routes by `view.viewId`.

## Known limits of the spike / to do in the real runner
- The spike reaches the engine through `#define private public` on the wrapper. The real runner must
  create the engine itself (`FlutterDesktopEngineCreate/Run`) and keep the wrapper's messenger for channels.
- `FlutterDesktopViewControllerDestroy` on a secondary view would destroy the shared engine. Windows are
  therefore pooled: hidden, never destroyed, until the process exits.
- Windows are opened at fixed coordinates here; real placement comes from the engine over IPC.

## Measuring
`measure-spike.ps1` style: launch once, wait, read memory/CPU deltas over 10 s of true idle, screenshot
the region once. Environment switches in the spike: `FD_EXTRA=N` (extra views), `FD_BG=1` (magenta fill).
