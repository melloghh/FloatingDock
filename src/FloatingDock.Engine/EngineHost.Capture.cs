using System.Text.Json.Nodes;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.Engine;

/// <summary>Screenshot shortcuts (Win+Shift+S, PrtSc): while the selection overlay is up the UI draws nothing, so the bar and dock never end up in a shot.</summary>
internal sealed partial class EngineHost
{
    private readonly CaptureOverlayMonitor _capture = new();
    private CaptureShortcutWatcher? _captureShortcut;
    private System.Threading.Timer? _captureTimer;
    private long _capturePendingUntil;
    private bool _captureHidden;
    private readonly HashSet<string> _captureSeen = new();

    private void StartCaptureWatch()
    {
        if (_diagnostic) return;
        _captureTimer = new System.Threading.Timer(_ => _ui.Post(CaptureTick), null, Timeout.Infinite, Timeout.Infinite);
        // The hook runs on the UI-loop thread; the work is posted so the keystroke is never delayed.
        _captureShortcut = new CaptureShortcutWatcher(() => _ui.Post(BeginCapture));
    }

    private void BeginCapture()
    {
        if (_shuttingDown) return;
        // Nothing is hidden yet: the selection tool freezes the screen first, and the bar and dock must be in that picture.
        // They go away only once its overlay is really on screen (so they never cover it).
        _capturePendingUntil = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 2;
        _captureSeen.Clear();
        // What the selection tool's windows look like on this system (logged once per distinct description).
        _capture.Diagnostics = line => { if (_captureSeen.Add(line)) EngineLog.Info(line); };
        _capture.RelaxTopmost = true;
        _captureTimer?.Change(30, 30);
        WatchOverlayShown(true);
    }

    // The overlay is recognised the moment Windows shows it, not up to 30 ms later: the tool shrinks its window ~60 ms after showing it, and by then the
    // stretching hook must already be in place.
    private IntPtr _shownHook;
    private WinEventProc? _shownProc;

    private void WatchOverlayShown(bool on)
    {
        if (_shownHook != IntPtr.Zero) { UnhookWinEvent(_shownHook); _shownHook = IntPtr.Zero; }
        _shownProc = null;
        if (!on || _shuttingDown) return;
        _shownProc = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject != 0 || _captureHidden) return;
            var cls = NativeMethods.GetClass(hwnd);
            if (cls is "XamlWindow" or "SnipOverlayRootWindow") CaptureTick();
        };
        _shownHook = SetWinEventHook(0x8002 /* EVENT_OBJECT_SHOW */, 0x8002, IntPtr.Zero, _shownProc, 0, 0, 0);
    }

    private void CaptureTick()
    {
        if (_shuttingDown) return;
        _capture.Scan();
        bool pending = System.Diagnostics.Stopwatch.GetTimestamp() < _capturePendingUntil;
        SetCaptureHidden(_capture.IsActive);
        _capture.RelaxTopmost = pending;
        if (!_capture.IsActive && !pending)
        {
            _capture.Diagnostics = null;
            WatchOverlayShown(false);
            _captureTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        else if (_capture.IsActive) _captureTimer?.Change(100, 100);
    }

    // ---- keeping the selection overlay over the whole monitor
    // Snipping Tool keeps shrinking its window to the work area (twice in the first 400 ms: it reacts to every move). A timer would let each shrink show
    // for a few frames; a WinEvent hook runs right after the move, so the window is back before the next frame is composed.
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    private IntPtr _overlayHook, _overlayWatched;
    private WinEventProc? _overlayProc;
    private int _overlayFixes;

    private void WatchOverlaySize(bool on)
    {
        if (_overlayHook != IntPtr.Zero) { UnhookWinEvent(_overlayHook); _overlayHook = IntPtr.Zero; EngineLog.Info($"capture overlay had to be stretched {_overlayFixes} times"); }
        _overlayProc = null;
        if (!on) return;
        _overlayWatched = _capture.Current;
        if (_overlayWatched == IntPtr.Zero) return;
        NativeMethods.GetWindowThreadProcessId(_overlayWatched, out var pid);
        _overlayFixes = 0;
        FitOverlay();
        _overlayProc = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (hwnd != _overlayWatched || idObject != 0 || _overlayFixes > 400) return;   // OBJID_WINDOW only; never fight forever
            FitOverlay();
        };
        // EVENT_OBJECT_LOCATIONCHANGE = 0x800B, out of context: delivered on this thread through its message loop.
        _overlayHook = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _overlayProc, pid, 0, 0);
    }

    private void FitOverlay()
    {
        if (_capture.FitOverlayToMonitor()) { _overlayFixes++; EngineLog.Info($"capture overlay stretched over the whole monitor (#{_overlayFixes})"); }
    }

    private readonly List<IntPtr> _loweredUi = new();
    /// <summary>Every window that was ever put under an overlay: if one of them is found below the other windows outside a capture, it is raised again.</summary>
    private readonly HashSet<IntPtr> _topUi = new();
    private System.Threading.Timer? _healTimer;

    private void HealUiWindows()
    {
        if (_shuttingDown || _captureHidden || _topUi.Count == 0) return;
        foreach (var h in _topUi.ToArray())
        {
            if (!NativeMethods.IsWindow(h)) { _topUi.Remove(h); continue; }
            if ((NativeMethods.GetExStyle(h) & NativeMethods.WS_EX_TOPMOST) != 0) continue;
            NativeMethods.GetWindowRect(h, out var r);
            EngineLog.Info($"window {h:X} ({r.Left},{r.Top},{r.Right},{r.Bottom}, visible={NativeMethods.IsWindowVisible(h)}) lost its always-on-top state: raised again");
            MakeTopmost(h, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>A window that was put under the overlay with HWND_NOTOPMOST is not always taken back by a plain HWND_TOPMOST (the call succeeds and nothing
    /// changes; seen with the dock window). Dropping the style once more first makes it stick.</summary>
    private static void MakeTopmost(IntPtr h, uint flags)
    {
        NativeMethods.SetWindowPos(h, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
    }

    /// <summary>Puts the always-on-top windows of the UI process under the selection overlay (and back). Done here, in the engine, so it
    /// does not depend on the UI process getting the event in time: the overlay would otherwise have our bar and dock above it.</summary>
    private void PlaceUiWindows(bool underOverlay)
    {
        const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        if (underOverlay)
        {
            _loweredUi.Clear();
            NativeMethods.EnumWindows((h, _) =>
            {
                if (NativeMethods.GetClass(h) == "FloatingDock.UiWindow" && (NativeMethods.GetExStyle(h) & NativeMethods.WS_EX_TOPMOST) != 0)
                    _loweredUi.Add(h);
                return true;
            }, IntPtr.Zero);
            foreach (var h in _loweredUi) { _topUi.Add(h); NativeMethods.SetWindowPos(h, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags); }
            _healTimer ??= new System.Threading.Timer(_ => _ui.Post(HealUiWindows), null, 700, 700);
            LogZOrder();
        }
        else
        {
            var raised = _loweredUi.ToArray();
            _loweredUi.Clear();
            RaiseUiWindows(raised, flags);
            // A window sometimes does not take the style at once (the dock window, while the overlay is still closing): try again every 30 ms until all are up.
            RetryRaise(raised, flags, 1);
        }
    }

    private void RetryRaise(IntPtr[] windows, uint flags, int attempt)
    {
        if (_captureHidden) return;                       // another capture started: the windows belong under it again
        bool allUp = windows.All(h => !NativeMethods.IsWindow(h) || (NativeMethods.GetExStyle(h) & NativeMethods.WS_EX_TOPMOST) != 0);
        if (allUp || attempt > 20) { if (attempt > 1) EngineLog.Info($"raised the windows again after {attempt - 1} retries"); return; }
        RaiseUiWindows(windows, flags, log: true);
        new System.Threading.Timer(_ => _ui.Post(() => RetryRaise(windows, flags, attempt + 1)), null, 30, Timeout.Infinite);
    }

    private static void RaiseUiWindows(IntPtr[] windows, uint flags, bool log = false)
    {
        foreach (var h in windows)
        {
            if (!NativeMethods.IsWindow(h)) continue;
            if (log && (NativeMethods.GetExStyle(h) & NativeMethods.WS_EX_TOPMOST) != 0) continue;
            if (log) EngineLog.Info($"raising window {h:X} again: it was not on top after the capture");
            MakeTopmost(h, flags);
        }
    }

    private void LogZOrder()
    {
        var overlay = _capture.Current;
        var order = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) => { order.Add(h); return true; }, IntPtr.Zero);   // top of the z-order first
        int overlayAt = order.IndexOf(overlay);
        var ours = _loweredUi.Select(h => order.IndexOf(h)).ToArray();
        EngineLog.Info($"z-order: overlay at {overlayAt}, lowered windows at [{string.Join(",", ours)}] ({(ours.Length > 0 && ours.All(i => i > overlayAt) ? "all below the overlay" : "NOT all below the overlay")})");
    }

    private void SetCaptureHidden(bool hidden)
    {
        if (_captureHidden == hidden) return;
        _captureHidden = hidden;
        EngineLog.Info(hidden ? "capture overlay detected: bar and dock go below it" : "capture overlay gone");
        PlaceUiWindows(hidden);
        WatchOverlaySize(hidden);
        _server.Publish("capture.changed", new JsonObject { ["hidden"] = hidden });
    }
}
