using System.Runtime.InteropServices;
using System.Text;
using FloatingDock.Core.Services;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>Tracks visible Windows capture overlays from window events. The ordinary screenshot editor is not an overlay.</summary>
public sealed class CaptureOverlayMonitor
{
    private readonly HashSet<IntPtr> _overlays = new();
    public Action<string>? Diagnostics { get; set; }
    public bool IsActive => _overlays.Count != 0;
    /// <summary>The window of the selection overlay that was seen last (zero when there is none).</summary>
    public IntPtr Current => _overlays.Count == 0 ? IntPtr.Zero : _overlays.First();
    /// <summary>While a screenshot shortcut was just pressed the selection overlay may not carry the topmost style (it lives in a shell band): it is then recognised without it.</summary>
    public bool RelaxTopmost { get; set; }
    public void Scan()
    {
        _overlays.Clear();
        NativeMethods.EnumWindows((h, _) => { Observe(h); if (Diagnostics is not null) DescribeCaptureHost(h); return true; }, IntPtr.Zero);
    }
    private void DescribeCaptureHost(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return;
        try
        {
            var path = new StringBuilder(1024); int length = path.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(process, 0, path, ref length)) return;
            var exe = Path.GetFileName(path.ToString());
            if (!exe.Contains("Snip", StringComparison.OrdinalIgnoreCase) && !exe.Contains("Clip", StringComparison.OrdinalIgnoreCase) && !exe.Contains("Sketch", StringComparison.OrdinalIgnoreCase)) return;
            NativeMethods.GetWindowRect(hwnd, out var rect);
            NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloak, sizeof(int));
            Diagnostics?.Invoke($"capture host: exe={exe}, class={NativeMethods.GetClass(hwnd)}, visible={NativeMethods.IsWindowVisible(hwnd)}, style={NativeMethods.GetStyle(hwnd):X}, exstyle={NativeMethods.GetExStyle(hwnd):X}, bounds={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}, cloak={cloak}");
        }
        finally { NativeMethods.CloseHandle(process); }
    }
    public void Observe(IntPtr hwnd)
    {
        if (IsSelectionOverlay(hwnd, RelaxTopmost)) _overlays.Add(hwnd); else _overlays.Remove(hwnd);
    }
    /// <summary>The selection overlay is a window that Windows fits to the work area, so with a bar or dock reserving space it shrinks and shows the frozen
    /// screen scaled down inside, with our live bar and dock left outside it. Stretches it over the whole monitor, which is what it gets beside the taskbar.
    /// Returns true when the window was moved.</summary>
    public bool FitOverlayToMonitor()
    {
        var hwnd = Current;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var bounds)) return false;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;
        var m = mi.rcMonitor;
        if (bounds.Left == m.Left && bounds.Top == m.Top && bounds.Right == m.Right && bounds.Bottom == m.Bottom) return false;
        return NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_NOCOPYBITS);
    }
    private const uint SWP_NOCOPYBITS = 0x100, SWP_NOSENDCHANGING = 0x400;

    public void Refresh()
    {
        foreach (var hwnd in _overlays.ToArray()) Observe(hwnd);
    }
    public static bool IsSelectionOverlay(IntPtr hwnd, bool relaxTopmost = false)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;
        bool topmost = (NativeMethods.GetExStyle(hwnd) & NativeMethods.WS_EX_TOPMOST) != 0;
        if (!topmost && !relaxTopmost) return false;
        bool framed = (NativeMethods.GetStyle(hwnd) & NativeMethods.WS_CAPTION) != 0;
        string windowClass = NativeMethods.GetClass(hwnd);
        if (framed && windowClass is not ("XamlWindow" or "SnipOverlayRootWindow")) return false;
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var c, sizeof(int)) == 0 && c != 0) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var bounds)) return false;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;
        // Windows fits the selection window to the work area once the bar or dock reserve space (observed: 0,44,1920,1013 on a 1920x1080 screen),
        // so covering the work area is enough; otherwise the overlay would be "gone" a moment after it appeared.
        const int slack = 2;
        var area = mi.rcWork;
        bool covers = bounds.Left <= area.Left + slack && bounds.Top <= area.Top + slack && bounds.Right >= area.Right - slack && bounds.Bottom >= area.Bottom - slack;
        if (!covers) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            var path = new StringBuilder(1024); int length = path.Capacity;
            return NativeMethods.QueryFullProcessImageName(process, 0, path, ref length) && ScreenCapturePolicy.IsSelectionOverlay(path.ToString(), true, false, framed, true, covers, windowClass);
        }
        finally { NativeMethods.CloseHandle(process); }
    }
}
