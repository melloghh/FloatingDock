using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.RunningApps;

public enum WindowEventKind { Created, Destroyed, Shown, Hidden, Renamed, Cloaked, Uncloaked, Foreground, MinimizeStart, MinimizeEnd, LocationChanged }

/// <summary>Event-driven window notifications (SetWinEventHook). Must be created on a thread with a message loop (the UI thread); no polling.</summary>
public sealed class WinEventWatcher : IDisposable
{
    private readonly List<IntPtr> _hooks = new();
    private readonly WinEventProc _proc;      // kept alive for the native hook
    private bool _disposed;
    private readonly uint _processId;
    private readonly bool _includeOwn;

    public event Action<WindowEventKind, IntPtr>? WindowEvent;

    public WinEventWatcher(uint processId = 0, bool includeOwn = false)
    {
        _processId = processId; _includeOwn = includeOwn;
        _proc = OnEvent;
        Hook(NativeMethods.EVENT_OBJECT_CREATE, NativeMethods.EVENT_OBJECT_HIDE);      // create, destroy, show, hide
        if (!includeOwn) Hook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_NAMECHANGE);
        Hook(NativeMethods.EVENT_OBJECT_CLOAKED, NativeMethods.EVENT_OBJECT_UNCLOAKED);
        Hook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
        Hook(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND);
    }

    private void Hook(uint min, uint max)
    {
        var h = NativeMethods.SetWinEventHook(min, max, IntPtr.Zero, _proc, _processId, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | (_includeOwn ? 0u : NativeMethods.WINEVENT_SKIPOWNPROCESS));
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;
        if (evt != NativeMethods.EVENT_SYSTEM_FOREGROUND && evt != NativeMethods.EVENT_SYSTEM_MINIMIZESTART && evt != NativeMethods.EVENT_SYSTEM_MINIMIZEEND)
            if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;      // only whole-window events
        var kind = evt switch
        {
            NativeMethods.EVENT_OBJECT_CREATE => WindowEventKind.Created,
            NativeMethods.EVENT_OBJECT_DESTROY => WindowEventKind.Destroyed,
            NativeMethods.EVENT_OBJECT_SHOW => WindowEventKind.Shown,
            NativeMethods.EVENT_OBJECT_HIDE => WindowEventKind.Hidden,
            NativeMethods.EVENT_OBJECT_NAMECHANGE => WindowEventKind.Renamed,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE => WindowEventKind.LocationChanged,
            NativeMethods.EVENT_OBJECT_CLOAKED => WindowEventKind.Cloaked,
            NativeMethods.EVENT_OBJECT_UNCLOAKED => WindowEventKind.Uncloaked,
            NativeMethods.EVENT_SYSTEM_FOREGROUND => WindowEventKind.Foreground,
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART => WindowEventKind.MinimizeStart,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND => WindowEventKind.MinimizeEnd,
            _ => (WindowEventKind?)null,
        };
        if (kind is { } k) WindowEvent?.Invoke(k, hwnd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var h in _hooks) NativeMethods.UnhookWinEvent(h);
        _hooks.Clear();
        WindowEvent = null;
    }
}
