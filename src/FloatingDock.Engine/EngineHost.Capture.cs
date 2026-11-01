using System.Text.Json.Nodes;
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
        _capturePendingUntil = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 2;
        SetCaptureHidden(true);
        _captureTimer?.Change(100, 100);
    }

    private void CaptureTick()
    {
        if (_shuttingDown) return;
        _capture.Scan();
        bool pending = System.Diagnostics.Stopwatch.GetTimestamp() < _capturePendingUntil;
        if (!_capture.IsActive && !pending)
        {
            _captureTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            SetCaptureHidden(false);
        }
    }

    private void SetCaptureHidden(bool hidden)
    {
        if (_captureHidden == hidden) return;
        _captureHidden = hidden;
        _server.Publish("capture.changed", new JsonObject { ["hidden"] = hidden });
    }
}
