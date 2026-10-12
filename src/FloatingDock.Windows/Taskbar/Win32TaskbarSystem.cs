using System.Runtime.InteropServices;
using FloatingDock.Core.Taskbar;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Taskbar;

/// <summary>The real taskbar: documented AppBar messages and reversible window regions on Explorer taskbars. Every change is read back.</summary>
public sealed class Win32TaskbarSystem : ITaskbarSystem
{
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern uint GetRegionData(IntPtr region, uint size, [Out] byte[]? data);
    [DllImport("gdi32.dll")] private static extern IntPtr ExtCreateRegion(IntPtr transform, uint size, byte[] data);
    private static IntPtr MainTaskbar() => NativeMethods.FindWindow(TaskbarFlags.MainClass, null);

    public bool TryGetAppBarState(out int flags)
    {
        flags = 0;
        var main = MainTaskbar();
        if (main == IntPtr.Zero || !NativeMethods.IsWindow(main)) return false;
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = main };
        flags = (int)(long)NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd) & 0xF;
        return true;
    }

    public bool TrySetAppBarState(int flags)
    {
        var main = MainTaskbar();
        if (main == IntPtr.Zero || !NativeMethods.IsWindow(main)) return false;
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = main, lParam = new IntPtr(flags & 0x3) };
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref abd);       // no meaningful return value: verify by reading back
        for (int i = 0; i < 10; i++)
        {
            if (TryGetAppBarState(out var now) && (now & 0x3) == (flags & 0x3)) return true;
            Thread.Sleep(30);
        }
        return false;
    }

    public IReadOnlyList<TaskbarWindow> GetTaskbarWindows()
    {
        var list = new List<TaskbarWindow>();
        var main = MainTaskbar();
        if (main != IntPtr.Zero) list.Add(Describe(main, TaskbarFlags.MainClass));
        IntPtr h = IntPtr.Zero;
        while (list.Count < TaskbarStateRecord.MaxWindows && (h = NativeMethods.FindWindowEx(IntPtr.Zero, h, TaskbarFlags.SecondaryClass, null)) != IntPtr.Zero)
            list.Add(Describe(h, TaskbarFlags.SecondaryClass));
        return list;
    }

    private static TaskbarWindow Describe(IntPtr hwnd, string cls)
    {
        string monitor = "";
        var m = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (m != IntPtr.Zero)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(m, ref mi)) monitor = mi.szDevice ?? "";
        }
        return new TaskbarWindow(hwnd.ToInt64(), cls, monitor, NativeMethods.IsWindowVisible(hwnd));
    }

    public bool IsWindow(long handle) => NativeMethods.IsWindow(new IntPtr(handle));

    public bool IsClippedEmpty(long handle)
    {
        var region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return false;
        try { return GetWindowRgn(new IntPtr(handle), region) == 1; }
        finally { NativeMethods.DeleteObject(region); }
    }

    public bool TryGetRegion(long handle, out byte[]? data)
    {
        data = null;
        if (!IsWindow(handle)) return false;
        var region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return false;
        try
        {
            // ERROR on a valid HWND is the documented representation of "no window region".
            if (GetWindowRgn(new IntPtr(handle), region) == 0) return IsWindow(handle);
            uint size = GetRegionData(region, 0, null);
            if (size < 32 || size > TaskbarStateRecord.MaxRegionBytes) return false;
            data = new byte[size];
            return GetRegionData(region, size, data) == size;
        }
        finally { NativeMethods.DeleteObject(region); }
    }

    public bool TryClipEmpty(long handle)
    {
        if (!IsWindow(handle)) return false;
        if (IsClippedEmpty(handle)) return true;
        var region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return false;
        if (NativeMethods.SetWindowRgn(new IntPtr(handle), region, true) == 0) { NativeMethods.DeleteObject(region); return false; }
        // On success the window owns HRGN. Only query it through a separate region copy.
        return IsClippedEmpty(handle);
    }

    public bool TrySetRegion(long handle, byte[]? data)
    {
        if (!IsWindow(handle) || data is { } bytes && (bytes.Length < 32 || bytes.Length > TaskbarStateRecord.MaxRegionBytes)) return false;
        var region = data is null ? IntPtr.Zero : ExtCreateRegion(IntPtr.Zero, (uint)data.Length, data);
        if (data is not null && region == IntPtr.Zero) return false;
        if (NativeMethods.SetWindowRgn(new IntPtr(handle), region, true) == 0)
        {
            if (region != IntPtr.Zero) NativeMethods.DeleteObject(region);
            return false;
        }
        return TryGetRegion(handle, out var after) && (data is null ? after is null : after is not null && data.AsSpan().SequenceEqual(after));
    }

    public bool TrySetVisible(long handle, bool visible)
    {
        var h = new IntPtr(handle);
        if (!NativeMethods.IsWindow(h)) return false;
        NativeMethods.ShowWindow(h, visible ? NativeMethods.SW_SHOWNA : NativeMethods.SW_HIDE);      // the return value is the PREVIOUS state, not success
        for (int i = 0; i < 10; i++)
        {
            if (NativeMethods.IsWindowVisible(h) == visible) return true;
            Thread.Sleep(30);
        }
        return false;
    }
}
