using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

public static class FullscreenDetector
{
    /// <summary>Includes ordinary maximized apps covering the monitor's work area, where the dock also lives.</summary>
    public static bool IsForegroundCoveringMonitorOn(IntPtr monitor, uint ownPid)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        return IsWindowFullscreenOn(foreground, monitor, ownPid) || IsWindowMaximizedOn(foreground, monitor, ownPid);
    }

    public static bool IsWindowMaximizedOn(IntPtr window, IntPtr monitor, uint ownPid)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window) || !NativeMethods.IsZoomed(window)) return false;
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        if (pid == ownPid) return false;
        if (NativeMethods.GetClass(window) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor) return false;
        var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi) || !NativeMethods.GetWindowRect(window, out var r)) return false;
        return r.Left <= mi.rcWork.Left && r.Top <= mi.rcWork.Top && r.Right >= mi.rcWork.Right && r.Bottom >= mi.rcWork.Bottom;
    }

    /// <summary>True if an unframed foreground window covers this monitor (video, borderless fullscreen or a game's window).</summary>
    public static bool IsForegroundFullscreenOn(IntPtr monitor, uint ownPid) => IsWindowFullscreenOn(NativeMethods.GetForegroundWindow(), monitor, ownPid);

    /// <summary>Checks native visibility, window style, process and monitor bounds without changing focus.</summary>
    public static bool IsWindowFullscreenOn(IntPtr fg, IntPtr monitor, uint ownPid)
    {
        if (fg == IntPtr.Zero || !NativeMethods.IsWindowVisible(fg) || NativeMethods.IsIconic(fg)) return false;
        NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        if (pid == ownPid) return false;
        var cls = NativeMethods.GetClass(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor) return false;
        var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi) || !NativeMethods.GetWindowRect(fg, out var r)) return false;
        bool covers = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        if (!covers) return false;
        // Some browser/video and custom-frame windows retain WS_CAPTION in fullscreen.
        // A normal framed maximized window extends its resize borders beyond the monitor;
        // accept a captioned window only when its outer bounds actually match the screen.
        if ((NativeMethods.GetStyle(fg) & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION &&
            (Math.Abs(r.Left-mi.rcMonitor.Left)>1 || Math.Abs(r.Top-mi.rcMonitor.Top)>1 ||
             Math.Abs(r.Right-mi.rcMonitor.Right)>1 || Math.Abs(r.Bottom-mi.rcMonitor.Bottom)>1)) return false;
        return true;
    }

    /// <summary>Windows' own opinion (D3D fullscreen game / presentation mode). Any failure of the API means "unknown" = false.</summary>
    public static bool WindowsReportsFullscreen()
    {
        try
        {
            int hr = NativeMethods.SHQueryUserNotificationState(out var state);
            return FloatingDock.Core.Services.UserNotificationPolicy.IsFullscreen(hr, state);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.COMException) { return false; }
    }
}
