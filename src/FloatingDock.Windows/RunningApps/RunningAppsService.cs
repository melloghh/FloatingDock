using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.Shell;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.Windows.RunningApps;

/// <summary>
/// Keeps the set of taskbar-eligible windows up to date from WinEvent notifications. A window whose identity is not ready yet is looked at again a limited
/// number of times (0.1 / 0.3 / 0.8 / 2 s), never forever. A slow safety-net scan (every 30 s) drops windows whose destroy event was missed.
/// All state lives on the UI thread; the Shell queries run on the <see cref="ShellWorker"/>.
/// </summary>
public sealed class RunningAppsService : IDisposable
{
    private static readonly int[] RetryDelaysMs = { 100, 300, 800, 2000 };
    /// <summary>Upper bound of re-resolutions of one window whose identity stays incomplete (timer retries + event-triggered ones). After that the window keeps its provisional identity.</summary>
    private const int MaxIncompleteChecks = 10;

    private readonly ShellWorker _worker;
    private readonly Action<Action> _post;               // marshals to the UI thread
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly Dictionary<long, WindowInfo> _windows = new();
    private readonly HashSet<IntPtr> _includedOwnWindows = new();
    private readonly HashSet<long> _inFlight = new();
    private readonly HashSet<long> _incomplete = new();
    private readonly Dictionary<long, int> _incompleteChecks = new();
    private readonly System.Threading.Timer _safetyNet;
    private readonly System.Threading.Timer _debounce;
    private WinEventWatcher? _watcher;
    private WinEventWatcher? _ownWatcher;
    private long _foreground;
    private bool _disposed;
    private int _changePending;

    public RunningAppsService(ShellWorker worker, Action<Action> postToUiThread)
    {
        _worker = worker;
        _post = postToUiThread;
        _safetyNet = new System.Threading.Timer(_ => _post(FullScan), null, Timeout.Infinite, Timeout.Infinite);
        _debounce = new System.Threading.Timer(_ => _post(RaiseChanged), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Windows (z-order is not guaranteed) and the root window that is in the foreground. Raised on the UI thread.</summary>
    public event Action<IReadOnlyList<WindowInfo>, long>? Changed;
    // Capture overlays are often tool windows and intentionally absent from the app list.
    public event Action<WindowEventKind, IntPtr>? NativeWindowChanged;

    /// <summary>Raised on the UI thread when only the foreground window changed.</summary>
    public IReadOnlyList<WindowInfo> Snapshot() => _windows.Values.ToList();
    public long ForegroundRoot => _foreground;

    public void IncludeOwnWindow(IntPtr hwnd)
    {
        _includedOwnWindows.Add(hwnd);
        if (_ownWatcher is null)
        {
            _ownWatcher = new WinEventWatcher(_ownPid, includeOwn: true);
            _ownWatcher.WindowEvent += (kind, h) => { if (_includedOwnWindows.Contains(h)) OnWindowEvent(kind, h); };
        }
        Evaluate(hwnd, 0);
        if (UpdateForeground()) ScheduleChanged();
    }
    public void ExcludeOwnWindow(IntPtr hwnd)
    {
        _includedOwnWindows.Remove(hwnd);
        if (_windows.Remove(hwnd.ToInt64())) { Forget(hwnd.ToInt64()); ScheduleChanged(); }
        if (_includedOwnWindows.Count == 0) { _ownWatcher?.Dispose(); _ownWatcher = null; }
    }
    private bool IsCandidate(IntPtr hwnd) => WindowFilter.IsCandidate(hwnd, _includedOwnWindows.Contains(hwnd) ? 0 : _ownPid);

    public void Start()
    {
        _watcher = new WinEventWatcher();
        _watcher.WindowEvent += OnWindowEvent;
        FullScan();
        _safetyNet.Change(30_000, 30_000);
    }

    public void FullScan()
    {
        if (_disposed) return;
        var current = WindowFilter.EnumerateCandidates(_ownPid);
        current.AddRange(_includedOwnWindows.Where(IsCandidate));
        var currentSet = new HashSet<long>(current.Select(h => h.ToInt64()));
        bool removed = false;
        foreach (var h in _windows.Keys.ToList())
            if (!currentSet.Contains(h)) { _windows.Remove(h); Forget(h); removed = true; }
        foreach (var h in current) if (!_windows.ContainsKey(h.ToInt64())) Evaluate(h, 0);
        if (UpdateForeground() || removed) ScheduleChanged();
    }

    private void OnWindowEvent(WindowEventKind kind, IntPtr hwnd)
    {
        if (_disposed) return;
        long id = hwnd.ToInt64();
        NativeWindowChanged?.Invoke(kind, hwnd);
        switch (kind)
        {
            case WindowEventKind.LocationChanged:
            case WindowEventKind.MoveStart:
            case WindowEventKind.MoveEnd:
                return; // Only overlay/fullscreen tracking needs geometry events, never redo shell identity for a moving window.
            case WindowEventKind.Destroyed:
            case WindowEventKind.Cloaked:
                if (_windows.Remove(id)) { Forget(id); ScheduleChanged(); }
                break;
            case WindowEventKind.Foreground:
                if (UpdateForeground()) ScheduleChanged();
                if (!TryReidentify(hwnd)) Evaluate(hwnd, 0);      // a window that became foreground may be new to us, or may have been unidentified until now
                break;
            case WindowEventKind.MinimizeStart:
            case WindowEventKind.MinimizeEnd:
                if (_windows.TryGetValue(id, out var w)) { _windows[id] = w with { IsMinimized = NativeMethods.IsIconic(hwnd) }; ScheduleChanged(); }
                break;
            case WindowEventKind.Renamed:
                if (_windows.TryGetValue(id,out var named) && !TryReidentify(hwnd))
                {
                    // Browser/media titles change often. Identity is already resolved:
                    // avoid reopening the process and querying Shell/property stores.
                    var title=NativeMethods.GetTitle(hwnd);
                    if(named.Title!=title){_windows[id]=named with{Title=title};ScheduleChanged();}
                }
                break;
            default:                                                              // created / shown / hidden / uncloaked
                if (!TryReidentify(hwnd)) Evaluate(hwnd, 0);
                break;
        }
    }

    private void Forget(long id) { _incomplete.Remove(id); _incompleteChecks.Remove(id); }

    /// <summary>An event for a window whose identity is still incomplete (e.g. a Settings window whose hosted CoreWindow did not exist yet) triggers one more resolution - bounded per window.</summary>
    private bool TryReidentify(IntPtr hwnd)
    {
        long id = hwnd.ToInt64();
        if (!_incomplete.Contains(id)) return false;
        if (_incompleteChecks.GetValueOrDefault(id) >= MaxIncompleteChecks) return true;      // gave up: keep what we have, do not poll
        Evaluate(hwnd, RetryDelaysMs.Length, refresh: true);                                   // attempt == Length: no timer chain, the event flow drives further tries
        return true;
    }

    private bool UpdateForeground()
    {
        var fg = NativeMethods.GetForegroundWindow();
        long root = 0;
        if (fg != IntPtr.Zero)
        {
            var r = NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOTOWNER);
            root = (r == IntPtr.Zero ? fg : r).ToInt64();
            // the foreground window may be an owned dialog of a known window
            if (!_windows.ContainsKey(root) && _windows.ContainsKey(fg.ToInt64())) root = fg.ToInt64();
        }
        if (root == _foreground) return false;
        _foreground = root;
        return true;
    }

    /// <param name="refresh">Re-resolve even if the window is already known (title change, retry of an incomplete identity).</param>
    private void Evaluate(IntPtr hwnd, int attempt, bool refresh = false)
    {
        long id = hwnd.ToInt64();
        if (!IsCandidate(hwnd))
        {
            if (_windows.Remove(id)) { Forget(id); ScheduleChanged(); }
            return;
        }
        if (!refresh && _windows.ContainsKey(id)) return;                        // already known
        if (!_inFlight.Add(id)) return;
        _ = _worker.Run(() => WindowIdentityResolver.Resolve(hwnd)).ContinueWith(t =>
        {
            if (t.IsFaulted) _ = t.Exception; // observe failures even when the UI queue has already stopped
            _post(() =>
            {
                _inFlight.Remove(id);
                if (_disposed || t.IsFaulted || t.IsCanceled) return;
                var res = t.Result;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var currentPid);
                if (currentPid != (uint)res.Info.ProcessId) { Evaluate(hwnd, 0, refresh: true); return; }
                if (!IsCandidate(hwnd) || WindowFilter.IsIgnoredExe(res.Info.ExePath))
                {
                    if (_windows.Remove(id)) { Forget(id); ScheduleChanged(); }
                    return;
                }
                if (!_windows.TryGetValue(id, out var old) || old != res.Info) { _windows[id] = res.Info; ScheduleChanged(); }
                if (res.Complete) Forget(id);
                else
                {
                    _incomplete.Add(id);
                    _incompleteChecks[id] = _incompleteChecks.GetValueOrDefault(id) + 1;
                    if (attempt < RetryDelaysMs.Length && _incompleteChecks[id] < MaxIncompleteChecks)
                    {
                        int next = attempt + 1;
                        _ = Task.Delay(RetryDelaysMs[attempt]).ContinueWith(_ =>
                            _post(() => { if (!_disposed && _incomplete.Contains(id)) Evaluate(hwnd, next, refresh: true); }), TaskScheduler.Default);
                    }
                }
            });
        }, TaskScheduler.Default);
    }

    private void ScheduleChanged()
    {
        if (Interlocked.Exchange(ref _changePending, 1) == 0) _debounce.Change(40, Timeout.Infinite);
    }

    private void RaiseChanged()
    {
        Interlocked.Exchange(ref _changePending, 0);
        if (_disposed) return;
        Changed?.Invoke(_windows.Values.ToList(), _foreground);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _safetyNet.Dispose();
        _debounce.Dispose();
        _watcher?.Dispose();
        _ownWatcher?.Dispose();
        Changed = null;
    }
}
