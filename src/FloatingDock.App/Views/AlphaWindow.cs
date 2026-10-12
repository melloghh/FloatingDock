using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.UI.Views;

/// <summary>Own-window DWM alpha setup and background erasure. No color key, injection or system-wide hooks.</summary>
internal sealed class AlphaWindow : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int item);
    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;
    private readonly UIntPtr _id = new(0x464441);
    private bool _disposed;

    internal AlphaWindow(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = OnMessage;
        if (!SetWindowSubclass(hwnd, _proc, _id, UIntPtr.Zero)) throw new InvalidOperationException("Could not configure Dock alpha window.");
        try
        {
            Configure();
            var dc = NativeMethods.GetDC(hwnd);
            try { Clear(dc); } finally { if (dc != IntPtr.Zero) NativeMethods.ReleaseDC(hwnd, dc); }
        }
        catch { RemoveWindowSubclass(hwnd, _proc, _id); throw; }
    }

    private void Configure()
    {
        var margins = new Margins();
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(_hwnd, ref margins));
        WindowStyler.SetBlurBehind(_hwnd, false, 0, 0, 0, 0, 0);
        // Keep an opaque HWND redirection bitmap out of the desktop composition path.
        var extended = NativeMethods.GetExStyle(_hwnd) | 0x00200000L;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(extended));
    }

    private bool Clear(IntPtr dc)
    {
        if (dc == IntPtr.Zero || !GetClientRect(_hwnd, out var rect)) return false;
        return FillRect(dc, ref rect, GetStockObject(4 /* BLACK_BRUSH: DWM interprets this as transparent */)) != 0;
    }

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        try
        {
            if (message == 0x14 && Clear(wParam)) return new IntPtr(1); // WM_ERASEBKGND
            if (message == 0x31e) Configure(); // WM_DWMCOMPOSITIONCHANGED
        }
        catch (Exception ex) { DockApp.TryLog("dock alpha configuration failed", ex); }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveWindowSubclass(_hwnd, _proc, _id);
    }
}
