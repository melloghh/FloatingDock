using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>Win32 styling of FloatingDock's own WinUI windows that WinUI/AppWindow does not expose.</summary>
public static class WindowStyler
{
    /// <summary>Removes caption / frame, hides the window from Alt+Tab and the taskbar, keeps it on top. Optionally makes it non-activating.</summary>
    public static void MakeOverlayWindow(IntPtr hwnd, bool noActivate, bool topmost = true)
    {
        long style = NativeMethods.GetStyle(hwnd);
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_BORDER | NativeMethods.WS_DLGFRAME);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));
        SetNoActivate(hwnd, noActivate);
        // no DWM border, no rounded corners, no shadow of the (invisible) frame
        int none = unchecked((int)NativeMethods.DWMWA_COLOR_NONE);
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref none, sizeof(int));
        int doNotRound = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));
        NativeMethods.SetWindowPos(hwnd, topmost ? NativeMethods.HWND_TOPMOST : IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED | (topmost ? 0u : NativeMethods.SWP_NOZORDER));
    }

    public static void SetNoActivate(IntPtr hwnd, bool noActivate)
    {
        long ex = NativeMethods.GetExStyle(hwnd);
        ex |= NativeMethods.WS_EX_TOOLWINDOW;
        ex &= ~NativeMethods.WS_EX_APPWINDOW;
        if (noActivate) ex |= NativeMethods.WS_EX_NOACTIVATE; else ex &= ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    public static void SetBounds(IntPtr hwnd, int x, int y, int w, int h, bool topmost = true) =>
        NativeMethods.SetWindowPos(hwnd, topmost ? NativeMethods.HWND_TOPMOST : IntPtr.Zero, x, y, w, h, NativeMethods.SWP_NOACTIVATE | (topmost ? 0u : NativeMethods.SWP_NOZORDER));

    public static void SetTopmost(IntPtr hwnd, bool topmost) =>
        NativeMethods.SetWindowPos(hwnd, topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    public static void Move(IntPtr hwnd, int x, int y) =>
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    public static void Show(IntPtr hwnd, bool activate) => NativeMethods.ShowWindow(hwnd, activate ? NativeMethods.SW_SHOW : NativeMethods.SW_SHOWNA);
    public static void Hide(IntPtr hwnd) => NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
    public static bool IsVisible(IntPtr hwnd) => NativeMethods.IsWindowVisible(hwnd);

    public static void BringToFront(IntPtr hwnd) => WindowActivator.Activate(hwnd);

    public static IntPtr Foreground() => NativeMethods.GetForegroundWindow();

    /// <summary>Blur-behind limited to a rounded rectangle (client coordinates, pixels). Best effort: DWM decides how it is drawn.</summary>
    public static void SetBlurBehind(IntPtr hwnd, bool enable, int left, int top, int right, int bottom, int cornerPx)
    {
        IntPtr rgn = IntPtr.Zero;
        // DWM's alpha compositing must stay enabled even when the user turns blur off.
        // An off-window region enables transparency without blurring desktop pixels.
        var bb = new DWM_BLURBEHIND { dwFlags = 3, fEnable = 1 };
        rgn = enable
            ? NativeMethods.CreateRoundRectRgn(left, top, right + 1, bottom + 1, cornerPx * 2, cornerPx * 2)
            : NativeMethods.CreateRectRgn(-2, -2, -1, -1);
        bb.hRgnBlur = rgn;
        try { NativeMethods.DwmEnableBlurBehindWindow(hwnd, ref bb); }
        finally { if (rgn != IntPtr.Zero) NativeMethods.DeleteObject(rgn); }
    }

    /// <summary>Clips the window (and therefore its mouse hit area) to a rectangle in window pixels; null removes the clip. Best effort.</summary>
    public static void SetRegion(IntPtr hwnd, (int L, int T, int R, int B)? rect)
    {
        if (rect is null) { NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true); return; }
        var rgn = NativeMethods.CreateRectRgn(rect.Value.L, rect.Value.T, rect.Value.R, rect.Value.B);
        if (NativeMethods.SetWindowRgn(hwnd, rgn, true) == 0) NativeMethods.DeleteObject(rgn);   // on success the system owns the region
    }

    public static void SetRoundedRegion(IntPtr hwnd,int width,int height,int radius)
    {
        var region=NativeMethods.CreateRoundRectRgn(0,0,width+1,height+1,radius*2,radius*2);
        if(region==IntPtr.Zero)throw new InvalidOperationException("Could not create rounded window region.");
        if(NativeMethods.SetWindowRgn(hwnd,region,true)==0) { NativeMethods.DeleteObject(region);throw new InvalidOperationException("Could not clip window corners."); }
    }

    public static (int X, int Y, int W, int H)? GetRect(IntPtr hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out var r) ? (r.Left, r.Top, r.Width, r.Height) : null;

    public static (int X, int Y)? CursorPos() => NativeMethods.GetCursorPos(out var p) ? (p.X, p.Y) : null;

    public static IntPtr MonitorOf(IntPtr hwnd) => NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
}
