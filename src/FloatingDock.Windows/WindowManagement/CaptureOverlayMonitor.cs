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
        if (IsSelectionOverlay(hwnd)) _overlays.Add(hwnd); else _overlays.Remove(hwnd);
    }
    public void Refresh()
    {
        foreach (var hwnd in _overlays.ToArray()) Observe(hwnd);
    }
    public static bool IsSelectionOverlay(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;
        if ((NativeMethods.GetExStyle(hwnd) & NativeMethods.WS_EX_TOPMOST) == 0) return false;
        bool framed = (NativeMethods.GetStyle(hwnd) & NativeMethods.WS_CAPTION) != 0;
        string windowClass = NativeMethods.GetClass(hwnd);
        if (framed && windowClass is not ("XamlWindow" or "SnipOverlayRootWindow")) return false;
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var c, sizeof(int)) == 0 && c != 0) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var bounds)) return false;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;
        bool covers = bounds.Left <= mi.rcMonitor.Left && bounds.Top <= mi.rcMonitor.Top && bounds.Right >= mi.rcMonitor.Right && bounds.Bottom >= mi.rcMonitor.Bottom;
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
