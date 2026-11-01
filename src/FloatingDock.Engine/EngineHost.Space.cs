using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Services;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.Engine;

/// <summary>How the dock shares each monitor with maximized and full-screen windows (DockSpacePolicy), and the fitting of full-screen windows above it.</summary>
internal sealed partial class EngineHost
{
    private readonly Dictionary<IntPtr, int> _fitAttempts = new();
    private System.Threading.Timer? _spaceTimer;

    /// <summary>Per monitor: should the dock hide, and may its edge still reveal it? Computed exactly as the WinUI dock did.</summary>
    private JsonObject SpaceNode()
    {
        var settings = _settings.Current;
        bool gameFullscreen = settings.KeepDockInFullscreen && FullscreenDetector.WindowsReportsFullscreen();
        bool enabled = DockSpacePolicy.HidesForCoveringWindow(settings, gameFullscreen);
        bool fit = DockSpacePolicy.ShouldFitFullscreen(settings, gameFullscreen);
        uint pid = (uint)Environment.ProcessId;
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        // The UI's own overlays must not end the external application's fullscreen session.
        if (IsUiProcess((int)foregroundPid)) foreground = new IntPtr(_running.ForegroundRoot);
        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOTOWNER);
        var list = new JsonArray();
        foreach (var m in MonitorHelper.GetMonitors())
        {
            bool covering = FullscreenDetector.IsWindowFullscreenOn(foreground, m.Handle, pid) || FullscreenDetector.IsWindowFullscreenOn(root, m.Handle, pid);
            // A video (browser or player at full screen) keeps covering the screen and hides the dock even in "keep" mode.
            bool video = covering && DockSpacePolicy.HidesForVideo(settings, FullscreenDetector.IsVideoHost(foreground) || FullscreenDetector.IsVideoHost(root));
            if (fit && !video) FitFullscreenAboveDock(m.Handle, foreground, root, pid);
            bool fullscreen = (enabled && covering) || video;
            bool maximized = enabled && (FullscreenDetector.IsWindowMaximizedOn(foreground, m.Handle, pid) || FullscreenDetector.IsWindowMaximizedOn(root, m.Handle, pid));
            list.Add(new JsonObject
            {
                ["x"] = m.Bounds.Left, ["y"] = m.Bounds.Top, ["fullscreen"] = covering, ["hide"] = fullscreen || maximized, ["blockReveal"] = fullscreen,
            });
        }
        return new JsonObject { ["monitors"] = list };
    }

    /// <summary>A window that covers the whole monitor (F11, borderless full screen) ignores the work area, so move it above the reserved dock strip.
    /// Each window is fitted at most twice: an application that insists on its own bounds is left alone.</summary>
    private void FitFullscreenAboveDock(IntPtr monitor, IntPtr foreground, IntPtr root, uint pid)
    {
        foreach (var window in new[] { foreground, root }.Distinct())
        {
            if (window == IntPtr.Zero || !FullscreenDetector.IsWindowFullscreenOn(window, monitor, pid)) continue;
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) continue;
            _fitAttempts.TryGetValue(window, out int attempts);
            if (attempts >= 2) continue;
            if (FullscreenDetector.FitToArea(window, info.rcWork)) _fitAttempts[window] = attempts + 1;
        }
        if (_fitAttempts.Count > 64) _fitAttempts.Clear();
    }

    private void RegisterSpaceHandlers(Ipc.IpcServer server)
    {
        server.Register("space.get", async _ => await _ui.InvokeAsync(SpaceNode));
        server.RegisterSync("pinned.reorder", request =>
        {
            _pinned.Move(RequireString(request, "key"), request.Params["index"]?.GetValue<int>() ?? 0);
            return null;
        });
        // A slow safety net next to the foreground events: some windows change size without any event.
        _spaceTimer = new System.Threading.Timer(_ => _ui.Post(PublishSpace), null, 2000, 1000);
        _running.Changed += (_, _) => _ui.Post(PublishSpace);
    }

    private string _spaceState = "";

    private void PublishSpace()
    {
        if (_shuttingDown || _diagnostic || _server.SessionCount == 0) return;
        var node = SpaceNode();
        var text = node.ToJsonString();
        if (text == _spaceState) return;
        _spaceState = text;
        _server.Publish("space.changed", node);
    }
}
