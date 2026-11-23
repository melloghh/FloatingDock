using System.Text.Json.Nodes;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.Engine;

/// <summary>The last pictures of windows (see WindowSnapshotCache): what the dock's preview shows for a window that is minimized.</summary>
internal sealed partial class EngineHost
{
    private readonly WindowSnapshotCache _snapshots = new();
    private IntPtr _lastForeground;

    private void RegisterSnapshotHandlers(Ipc.IpcServer server)
    {
        server.RegisterSync("window.snapshot", request =>
        {
            long hwnd = request.Params["hwnd"]?.GetValue<long>() ?? throw BadParams("hwnd is required.");
            var png = _snapshots.Get(hwnd);
            return new JsonObject { ["image"] = png is null ? null : Convert.ToBase64String(png) };
        });
        _running.NativeWindowChanged += OnSnapshotEvent;
    }

    private bool IsDockWindow(IntPtr hwnd)
    {
        long id = hwnd.ToInt64();
        foreach (var w in _running.Snapshot()) if (w.Hwnd == id) return true;
        return false;
    }

    private void OnSnapshotEvent(WindowEventKind kind, IntPtr hwnd)
    {
        if (_shuttingDown || hwnd == IntPtr.Zero) return;
        switch (kind)
        {
            case WindowEventKind.MinimizeStart:
                // Still on screen for a moment: this is the last chance for a picture of it.
                if (IsDockWindow(hwnd)) _snapshots.Remember(hwnd, force: true);
                break;
            case WindowEventKind.Foreground:
                var left = _lastForeground;
                _lastForeground = hwnd;
                // The window that has just lost the focus may be minimized next (by Win+D, by another program): keep a fresh picture of it.
                if (left != IntPtr.Zero && left != hwnd && IsDockWindow(left)) _ = Task.Run(() => _snapshots.Remember(left, force: false));
                break;
            case WindowEventKind.Destroyed:
                _snapshots.Forget(hwnd.ToInt64());
                break;
        }
    }
}
