using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Taskbar;

public sealed record AppBarRequest(MonitorDescriptor Monitor, int HeightPx, bool Top = false, DockEdge Edge = DockEdge.Bottom);

/// <summary>Outcome of <see cref="AppBarReservation.Apply"/>: the monitors that are reserved now and those where the shell refused / an API call failed.</summary>
public sealed record AppBarResult(IReadOnlyList<string> Reserved, IReadOnlyList<string> Failed)
{
    public bool AllReserved => Failed.Count == 0;
}

/// <summary>
/// Reserves a strip at the bottom of each requested monitor with the documented AppBar API so maximised windows stop above the Dock.
/// One small native window per monitor owns the AppBar - the Dock's WinUI windows are never registered, so they keep their transparent shape.
/// Every shell call is checked; when registration fails nothing is left registered (the work area is only changed by a successful ABM_SETPOS).
/// A registration is bound to its window: when the process dies the shell removes the AppBar, so a kill cannot leave a reserved strip behind.
/// </summary>
public sealed class AppBarReservation : IDisposable
{
    private const string ClassName = "FloatingDock.AppBarWindow";
    private const uint AppBarCallback = NativeMethods.WM_APP + 2;
    private const int ABN_POSCHANGED = 1;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private static readonly WndProc Proc = StaticWndProc;
    private static readonly Dictionary<IntPtr, AppBarReservation> Owners = new();
    private static bool _classRegistered;

    private sealed class Slot
    {
        public IntPtr Hwnd;
        public bool Registered;
        public int Height = -1;
        public uint Edge;
        public RectPx Bounds;
    }

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Raised on the UI thread when the shell says the reserved area has to be recalculated (ABN_POSCHANGED).</summary>
    public event Action? PositionChanged;
    public Action<string>? Log { get; set; }

    public bool IsActive => _slots.Values.Any(s => s.Registered);
    public IReadOnlyCollection<string> ActiveMonitors => _slots.Where(kv => kv.Value.Registered).Select(kv => kv.Key).ToList();

    private static IntPtr StaticWndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == AppBarCallback && w.ToInt64() == ABN_POSCHANGED && Owners.TryGetValue(h, out var owner))
            try { owner.PositionChanged?.Invoke(); } catch (Exception ex) { owner.Log?.Invoke("AppBar callback failed: " + ex.Message); }
        return NativeMethods.DefWindowProc(h, msg, w, l);
    }

    private static bool EnsureClass()
    {
        if (_classRegistered) return true;
        var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc), hInstance = NativeMethods.GetModuleHandle(null), lpszClassName = ClassName };
        if (NativeMethods.RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS) return false;
        _classRegistered = true;
        return true;
    }

    private IntPtr CreateWindow()
    {
        if (!EnsureClass()) return IntPtr.Zero;
        var h = NativeMethods.CreateWindowEx((uint)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE), ClassName, "FloatingDock reserved area", (uint)NativeMethods.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        if (h != IntPtr.Zero) Owners[h] = this;
        return h;
    }

    private void DestroyWindow(Slot s)
    {
        if (s.Hwnd == IntPtr.Zero) return;
        Owners.Remove(s.Hwnd);
        if (NativeMethods.IsWindow(s.Hwnd) && !NativeMethods.DestroyWindow(s.Hwnd)) Log?.Invoke("AppBar window could not be destroyed.");
        s.Hwnd = IntPtr.Zero;
    }

    /// <summary>Single-monitor convenience wrapper around <see cref="Apply"/>.</summary>
    public bool Reserve(MonitorDescriptor monitor, int heightPx) => Apply(new[] { new AppBarRequest(monitor, heightPx) }).AllReserved;

    /// <summary>
    /// Makes the set of reserved strips equal to <paramref name="requests"/>: new monitors are registered, changed ones (resolution / DPI / height) are updated,
    /// monitors that are no longer requested (unplugged, mode off) are removed. Safe to call again at any time.
    /// </summary>
    public AppBarResult Apply(IReadOnlyList<AppBarRequest> requests)
    {
        var reserved = new List<string>();
        var failed = new List<string>();
        if (_disposed) return new(reserved, requests.Select(r => r.Monitor.DeviceName).ToList());
        var wanted = requests.Where(r => r.HeightPx > 0).GroupBy(r => r.Monitor.DeviceName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var key in _slots.Keys.Where(k => !wanted.ContainsKey(k)).ToList()) RemoveSlot(key);
        foreach (var (key, req) in wanted)
        {
            if (!_slots.TryGetValue(key, out var slot)) _slots[key] = slot = new Slot();
            if (ReserveOne(slot, req)) reserved.Add(key);
            else { failed.Add(key); RemoveSlot(key); }
        }
        return new(reserved, failed);
    }

    private bool ReserveOne(Slot slot, AppBarRequest req)
    {
        // a stale handle (window destroyed behind our back, Explorer restarted) must not be used
        if (slot.Hwnd != IntPtr.Zero && !NativeMethods.IsWindow(slot.Hwnd)) { Owners.Remove(slot.Hwnd); slot.Hwnd = IntPtr.Zero; slot.Registered = false; slot.Height = -1; }
        var bounds = req.Monitor.Bounds;
        uint edge=req.Top?1u:req.Edge switch { DockEdge.Left=>0u,DockEdge.Right=>2u,_=>3u };
        if (bounds.Width <= 0 || bounds.Height <= 0 || (edge is 0 or 2 ? bounds.Width : bounds.Height) <= req.HeightPx) return false;

        if (slot.Hwnd == IntPtr.Zero) slot.Hwnd = CreateWindow();
        if (slot.Hwnd == IntPtr.Zero) return false;

        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = slot.Hwnd, uCallbackMessage = AppBarCallback };
        if (!slot.Registered)
        {
            if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref abd) == UIntPtr.Zero) return false;      // refused: nothing registered, work area untouched
            slot.Registered = true;
        }
        abd.uEdge = edge;
        abd.rc = new RECT { Left=bounds.Left,Right=bounds.Right,Top=bounds.Top,Bottom=bounds.Bottom };
        switch(edge) { case 0:abd.rc.Right=abd.rc.Left+req.HeightPx;break;case 1:abd.rc.Bottom=abd.rc.Top+req.HeightPx;break;case 2:abd.rc.Left=abd.rc.Right-req.HeightPx;break;default:abd.rc.Top=abd.rc.Bottom-req.HeightPx;break; }
        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref abd) == UIntPtr.Zero) { Unregister(slot); return false; }
        switch(edge) { case 0:abd.rc.Right=abd.rc.Left+req.HeightPx;break;case 1:abd.rc.Bottom=abd.rc.Top+req.HeightPx;break;case 2:abd.rc.Left=abd.rc.Right-req.HeightPx;break;default:abd.rc.Top=abd.rc.Bottom-req.HeightPx;break; }
        if (abd.rc.Bottom <= abd.rc.Top || abd.rc.Right <= abd.rc.Left)
        {
            Unregister(slot);
            return false;
        }
        // Avoid a loop of ABN_POSCHANGED notifications from our own ABM_SETPOS, while
        // still re-querying after another AppBar changes the work area.
        if (slot.Edge == edge && slot.Height == req.HeightPx && slot.Bounds == bounds && NativeMethods.GetWindowRect(slot.Hwnd, out var existing) &&
            existing.Left == abd.rc.Left && existing.Top == abd.rc.Top && existing.Right == abd.rc.Right && existing.Bottom == abd.rc.Bottom) return true;
        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref abd) == UIntPtr.Zero) { Unregister(slot); return false; }
        if (!NativeMethods.SetWindowPos(slot.Hwnd, IntPtr.Zero, abd.rc.Left, abd.rc.Top, abd.rc.Right - abd.rc.Left, abd.rc.Bottom - abd.rc.Top, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER))
        {
            Unregister(slot);                       // undo ABM_SETPOS: the work area goes back
            return false;
        }
        slot.Height = req.HeightPx;
        slot.Edge = edge;
        slot.Bounds = bounds;
        return true;
    }

    private void Unregister(Slot slot)
    {
        if (slot.Registered && slot.Hwnd != IntPtr.Zero && NativeMethods.IsWindow(slot.Hwnd))
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = slot.Hwnd };
            if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref abd) == UIntPtr.Zero) Log?.Invoke("The shell did not acknowledge AppBar removal; destroying its owner window.");
        }
        slot.Registered = false;
        slot.Height = -1;
    }

    private void RemoveSlot(string key)
    {
        if (!_slots.Remove(key, out var slot)) return;
        Unregister(slot);
        DestroyWindow(slot);
    }

    /// <summary>Removes every reservation (mode off, dock hidden).</summary>
    public void Release()
    {
        foreach (var key in _slots.Keys.ToList()) RemoveSlot(key);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Release();
        _disposed = true;
        PositionChanged = null;
    }
}
