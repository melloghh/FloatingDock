using FloatingDock.Core.Configuration;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Taskbar;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Dispatching;

namespace FloatingDock.UI.Dock;

/// <summary>
/// Owns one <see cref="DockWindow"/> per target monitor, keeps them in sync with the monitor layout, the settings and the
/// running windows, hides them for fullscreen apps, and reserves the work area when asked to.
/// </summary>
internal sealed class DockHost : IDisposable
{
    private readonly DockApp _app;
    private readonly DockViewModel _vm = new();
    private readonly List<DockWindow> _windows = new();
    private readonly AppBarReservation _appBar = new();
    private readonly DispatcherQueueTimer _fullscreenTimer;
    private readonly CaptureOverlayMonitor _capture = new();
    private readonly CaptureShortcutWatcher _captureShortcut;
    private readonly DispatcherQueueTimer _captureStartup;
    private long _capturePendingUntil;
    private readonly Dictionary<long, (FloatingDock.Core.Models.WindowInfo Window, FloatingDock.Windows.Native.RECT Rect, long Until)> _animationOrigins = new();
    private readonly List<(FloatingDock.Core.Models.AppRef App, FloatingDock.Windows.Native.RECT Rect, long Until)> _launchOrigins = new();
    private bool _userHidden, _suspended;
    private string _lastAppBarFailure = "";
    private bool _disposed;
    internal bool HoldFullscreenStateForDiagnostics { get; set; }
    internal bool HoldWindowListForDiagnostics { get; set; }

    public DockHost(DockApp app)
    {
        _app = app;
        _captureStartup=app.UiQueue.CreateTimer();_captureStartup.Interval=TimeSpan.FromMilliseconds(100);
        _captureStartup.Tick+=(_,_)=>
        {
            _capture.Scan();
            if(_capture.IsActive||System.Diagnostics.Stopwatch.GetTimestamp()>=_capturePendingUntil){_capturePendingUntil=0;_captureStartup.Stop();}
            UpdateCaptureVisibility();
        };
        _captureShortcut=new(BeginCaptureShortcut);
        _appBar.Log = app.Log.Warn;
        if (Environment.GetCommandLineArgs().Contains("--diagnostic-input")) _capture.Diagnostics = app.Log.Info;
        _fullscreenTimer = app.UiQueue.CreateTimer();
        _fullscreenTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        _appBar.PositionChanged += () => _app.UiQueue.TryEnqueue(() => { if (!_disposed) UpdateAppBar(); });
    }

    public DockViewModel ViewModel => _vm;
    internal IReadOnlyList<IntPtr> WindowHandles => _windows.Select(w => w.Handle).ToList();
    internal DockWindow? PrimaryWindowForDiagnostics => _windows.FirstOrDefault();
    internal void OnExplorerRestarted() { _appBar.Release(); Reconcile(); }

    public void Start()
    {
        _app.Msg.AnimationRectProvider = AnimationRectFor;
        _app.Pinned.Changed += OnPinnedChanged;
        _app.Running.Changed += OnRunningChanged;
        _app.Running.NativeWindowChanged += OnNativeWindowChanged;
        _app.Icons.IconsChanged += OnIconsChanged;
        _app.Tiling.StateChanged+=UpdateTilingVisibility;
        Reconcile();
        _capture.Scan();
        CheckFullscreen();
        _vm.SetWindows(_app.Running.Snapshot(), _app.Running.ForegroundRoot);
        ApplySettings();
    }

    internal FloatingDock.Windows.Native.RECT? AnimationRectFor(IntPtr hwnd)
    {
        if (_disposed) return null;
        var root = FloatingDock.Windows.Native.NativeMethods.GetAncestor(hwnd, FloatingDock.Windows.Native.NativeMethods.GA_ROOTOWNER);
        long id = (root == IntPtr.Zero ? hwnd : root).ToInt64();
        if (_animationOrigins.TryGetValue(id, out var origin))
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() <= origin.Until && WindowActivator.IsLive(origin.Window)) return origin.Rect;
            _animationOrigins.Remove(id);
        }
        var monitor = FloatingDock.Windows.Native.NativeMethods.MonitorFromWindow(hwnd, FloatingDock.Windows.Native.NativeMethods.MONITOR_DEFAULTTONEAREST);
        foreach (var window in _windows.OrderByDescending(w => w.Monitor.Handle == monitor))
            if (window.AnimationRectFor(hwnd) is { } rect) return rect;
        long now=System.Diagnostics.Stopwatch.GetTimestamp();
        _launchOrigins.RemoveAll(p=>p.Until<now);
        if(_launchOrigins.Count>0)
        {
            // A newly launched HWND can request its animation origin before the window tracker sees it.
            // Query identity only on this shell request and while a recent launch is pending.
            var target=root==IntPtr.Zero?hwnd:root;
            FloatingDock.Windows.Native.NativeMethods.GetWindowThreadProcessId(target,out var pid);
            var exe=FloatingDock.Windows.RunningApps.WindowIdentityResolver.GetExePath(pid);
            var aumid=_launchOrigins.Any(p=>!string.IsNullOrEmpty(p.App.Aumid))?FloatingDock.Windows.RunningApps.WindowIdentityResolver.GetProcessAumid(pid):null;
            var info=new FloatingDock.Core.Models.WindowInfo(target.ToInt64(),(int)pid,exe,aumid,null,null,false);
            foreach(var launch in _launchOrigins.AsEnumerable().Reverse())
                if(FloatingDock.Core.Models.AppMatching.Matches(launch.App,info)&&WindowActivator.IsLive(info))return launch.Rect;
        }
        return null;
    }
    internal void RememberLaunchOrigin(FloatingDock.Core.Models.AppRef app,FloatingDock.Windows.Native.RECT rect)
    {
        long now=System.Diagnostics.Stopwatch.GetTimestamp();
        _launchOrigins.RemoveAll(p=>p.Until<now||p.App.Key==app.Key);
        if(_launchOrigins.Count>=8)_launchOrigins.RemoveAt(0);
        _launchOrigins.Add((app.Clone(),rect,now+System.Diagnostics.Stopwatch.Frequency*8));
    }

    internal void RememberAnimationOrigin(IReadOnlyList<FloatingDock.Core.Models.WindowInfo> windows, FloatingDock.Windows.Native.RECT rect)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var id in _animationOrigins.Where(p => p.Value.Until < now).Select(p => p.Key).ToArray()) _animationOrigins.Remove(id);
        foreach (var window in windows)
            if (WindowActivator.IsLive(window)) _animationOrigins[window.Hwnd] = (window, rect, now + System.Diagnostics.Stopwatch.Frequency);
    }

    private void OnPinnedChanged() => _app.UiQueue.TryEnqueue(() => { if (!_disposed) _vm.Rebuild(); });

    private void OnRunningChanged(IReadOnlyList<FloatingDock.Core.Models.WindowInfo> windows, long fg)
    {
        if (_disposed) return;
        if (!HoldWindowListForDiagnostics) _vm.SetWindows(windows, fg);
        CheckFullscreen();
        foreach (var w in _windows) w.Reassert();
    }

    private void OnNativeWindowChanged(FloatingDock.Windows.RunningApps.WindowEventKind kind, IntPtr hwnd)
    {
        if (_disposed) return;
        if(kind is FloatingDock.Windows.RunningApps.WindowEventKind.Renamed or FloatingDock.Windows.RunningApps.WindowEventKind.MoveStart or FloatingDock.Windows.RunningApps.WindowEventKind.MinimizeStart or FloatingDock.Windows.RunningApps.WindowEventKind.MinimizeEnd)return;
        _capture.Observe(hwnd);
        _capture.Refresh();
        UpdateCaptureVisibility();
        if (kind == FloatingDock.Windows.RunningApps.WindowEventKind.Foreground ||
            kind == FloatingDock.Windows.RunningApps.WindowEventKind.LocationChanged && hwnd == FloatingDock.Windows.Native.NativeMethods.GetForegroundWindow()) CheckFullscreen();
    }
    internal void BeginCaptureShortcut()
    {
        if(_disposed)return;
        _capturePendingUntil=System.Diagnostics.Stopwatch.GetTimestamp()+System.Diagnostics.Stopwatch.Frequency*2;
        FloatingDock.Windows.Integration.SystemTrayOverflow.Dismiss();
        _app.Tiling.HidePresentationsForCapture();
        UpdateCaptureVisibility();_captureStartup.Start();
    }
    private void UpdateCaptureVisibility()
    {
        bool hidden=_capture.IsActive||System.Diagnostics.Stopwatch.GetTimestamp()<_capturePendingUntil;
        _app.TopBar?.SetCaptureHidden(hidden);
        foreach(var w in _windows)w.SetCaptureHidden(hidden);
        if(hidden)_app.Tiling.HidePresentationsForCapture();
    }

    private void OnIconsChanged()
    {
        foreach (var w in _windows) w.ReloadIcons();
    }
    private void UpdateTilingVisibility()
    {foreach(var window in _windows)window.SetTilingHidden(_app.Settings.Current.TilingHideDock&&_app.Tiling.Session?.HasTiles(window.Monitor.DeviceName)==true);}

    /// <summary>Creates / removes / moves dock windows after a monitor change (hot-plug, resolution, DPI) or a MonitorMode change.</summary>
    public void Reconcile()
    {
        if (_disposed) return;
        var monitors = MonitorHelper.GetMonitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        var wanted = _app.Settings.Current.MonitorMode == MonitorMode.All ? monitors.ToList() : primary is null ? new() : new() { primary };

        foreach (var w in _windows.Where(w => wanted.All(m => m.DeviceName != w.Monitor.DeviceName)).ToList())
        {
            _windows.Remove(w);
            w.Dispose();
        }
        foreach (var m in wanted)
        {
            var existing = _windows.FirstOrDefault(w => w.Monitor.DeviceName == m.DeviceName);
            if (existing is not null) existing.SetMonitor(m);
            else
            {
                var w = new DockWindow(m, _vm);
                w.SetUserHidden(_userHidden);
                w.SetSuspended(_suspended);
                _windows.Add(w);
                w.SetFullscreenHidden(false);
            }
        }
        UpdateAppBar();
    }

    /// <summary>Called when the settings change.</summary>
    public void ApplySettings()
    {
        if (_disposed) return;
        Reconcile();
        _vm.Rebuild();
        foreach (var w in _windows) w.ApplySettings();
        UpdateTilingVisibility();
        _fullscreenTimer.IsRepeating = true;
        if (!_fullscreenTimer.IsRunning) _fullscreenTimer.Start();
        CheckFullscreen();
        UpdateAppBar();
    }

    /// <summary>Work area changed (taskbar moved / hid): recompute window positions.</summary>
    public void RefreshMonitors() => Reconcile();

    /// <summary>Reserves a strip on EVERY monitor that shows a dock (the AppBar API is per monitor); reservations of monitors that went away or when the option is off are removed.</summary>
    private void UpdateAppBar()
    {
        var s = _app.Settings.Current;
        bool reserve = s.ReserveWorkArea && s.TaskbarMode != TaskbarMode.Leave && !s.AutoHide && !_userHidden;
        if (!reserve) { _appBar.Release(); return; }
        var requests = _windows
            .Select(w => new AppBarRequest(w.Monitor, (int)Math.Ceiling((s.BottomOffset + s.DockHeight + 6) * w.Monitor.Scale), Edge:s.Edge))
            .ToList();
        var result = _appBar.Apply(requests);
        if (!result.AllReserved)
        {
            var key = string.Join(",", result.Failed);
            if (key != _lastAppBarFailure) _app.Log.Warn("work area could not be reserved on: " + key);
            _lastAppBarFailure = key;
        }
        else _lastAppBarFailure = "";
    }

    private void CheckFullscreen()
    {
        if (_disposed || HoldFullscreenStateForDiagnostics) return;
        _capture.Refresh();
        UpdateCaptureVisibility();
        if(_capture.IsActive||System.Diagnostics.Stopwatch.GetTimestamp()<_capturePendingUntil)return;
        if (_windows.Any(w => w.IsContextMenuOpen)) return;
        bool enabled = _app.Settings.Current.HideOnFullscreen;
        uint pid = (uint)Environment.ProcessId;
        var foreground = FloatingDock.Windows.Native.NativeMethods.GetForegroundWindow();
        FloatingDock.Windows.Native.NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        // Overlay focus must not end the external application's fullscreen session.
        if (foregroundPid == pid) foreground = new IntPtr(_app.Running.ForegroundRoot);
        var root = FloatingDock.Windows.Native.NativeMethods.GetAncestor(foreground, FloatingDock.Windows.Native.NativeMethods.GA_ROOTOWNER);
        foreach (var w in _windows)
        {
            bool fullscreen = enabled && (FullscreenDetector.IsWindowFullscreenOn(foreground, w.Monitor.Handle, pid) || FullscreenDetector.IsWindowFullscreenOn(root, w.Monitor.Handle, pid));
            bool maximized = enabled && (FullscreenDetector.IsWindowMaximizedOn(foreground, w.Monitor.Handle, pid) || FullscreenDetector.IsWindowMaximizedOn(root, w.Monitor.Handle, pid));
            w.SetFullscreenHidden(fullscreen || maximized, blockEdgeReveal: fullscreen);
        }
    }

    public void SetUserHidden(bool hidden)
    {
        _userHidden = hidden;
        foreach (var w in _windows) w.SetUserHidden(hidden);
        UpdateAppBar();
    }

    public bool UserHidden => _userHidden;

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        _app.TopBar?.SetSuspended(suspended);
        foreach (var w in _windows) w.SetSuspended(suspended);
    }

    /// <summary>Focus the dock on the monitor under the cursor (keyboard navigation).</summary>
    public void FocusDock()
    {
        if (_userHidden) SetUserHidden(false);
        var cursorMonitor = MonitorHelper.FromCursor();
        var w = _windows.FirstOrDefault(x => cursorMonitor is not null && x.Monitor.DeviceName == cursorMonitor.DeviceName) ?? _windows.FirstOrDefault();
        w?.EnterKeyboardMode();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _captureShortcut.Dispose();_captureStartup.Stop();
        _app.Msg.AnimationRectProvider = null;
        _animationOrigins.Clear();
        _fullscreenTimer.Stop();
        _app.Pinned.Changed -= OnPinnedChanged;
        _app.Running.Changed -= OnRunningChanged;
        _app.Running.NativeWindowChanged -= OnNativeWindowChanged;
        _app.Icons.IconsChanged -= OnIconsChanged;
        _app.Tiling.StateChanged-=UpdateTilingVisibility;
        _appBar.Dispose();
        foreach (var w in _windows) w.Dispose();
        _windows.Clear();
    }
}
