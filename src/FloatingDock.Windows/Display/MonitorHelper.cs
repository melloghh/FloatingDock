using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Display;

public sealed record MonitorDescriptor(IntPtr Handle, string DeviceName, RectPx Bounds, RectPx WorkArea, bool IsPrimary, double Scale);

public readonly record struct RectPx(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    internal static RectPx From(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}

/// <summary>Monitor enumeration with effective DPI (scale = dpi / 96).</summary>
public static class MonitorHelper
{
    public static IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var list = new List<MonitorDescriptor>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) =>
        {
            var d2 = Describe(h);
            if (d2 is not null) list.Add(d2);
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
    }

    public static MonitorDescriptor? Describe(IntPtr monitor)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return null;
        double scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dx, out _) == 0 && dx > 0) scale = dx / 96.0;
        return new MonitorDescriptor(monitor, mi.szDevice, RectPx.From(mi.rcMonitor), RectPx.From(mi.rcWork), (mi.dwFlags & 1) != 0, scale);
    }

    public static MonitorDescriptor? Primary() => GetMonitors().FirstOrDefault(m => m.IsPrimary) ?? GetMonitors().FirstOrDefault();

    public static MonitorDescriptor? FromCursor()
    {
        NativeMethods.GetCursorPos(out var p);
        var h = NativeMethods.MonitorFromPoint(p, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return h == IntPtr.Zero ? Primary() : Describe(h);
    }

    public static (int X, int Y)? CursorPosition() => NativeMethods.GetCursorPos(out var p) ? (p.X, p.Y) : null;
}
