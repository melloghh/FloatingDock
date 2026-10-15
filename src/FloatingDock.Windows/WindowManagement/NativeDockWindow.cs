using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

public enum DockPointerKind { Move, Leave, LeftDown, LeftUp, MiddleUp, RightUp, Cancel }
public readonly record struct DockPointerEvent(DockPointerKind Kind, int X, int Y, bool LeftPressed);

/// <summary>A compositor-only HWND with Win32 input. No XAML input site competes with DesktopWindowTarget.</summary>
public sealed class NativeDockWindow : IDisposable
{
    private const string ClassName = "FloatingDock.NativeDock";
    private static readonly WndProc Proc = WindowProc;
    private static readonly Dictionary<IntPtr, NativeDockWindow> Windows = new();
    private static bool _registered;
    private bool _tracking;
    private bool _releasingCapture;
    public IntPtr Handle { get; private set; }
    public event Action<DockPointerEvent>? Pointer;
    public event Action<int>? KeyDown;
    public event Action<bool>? Activated;
    public event Action? CloseRequested;
    public event Action<Exception>? InputFailed;
    public int PointerMessages { get; private set; }
    /// <summary>Optional accessible XAML host for an input-only surface.</summary>
    public IntPtr AccessibilityHost { get; set; }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Tracking { public uint Size, Flags; public IntPtr Window; public uint HoverTime; }
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref Tracking tracking);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);

    public NativeDockWindow()
    {
        var module = NativeMethods.GetModuleHandle(null);
        if (!_registered)
        {
            var cls = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = module,
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(32512)),
                lpszClassName = ClassName,
            };
            if (NativeMethods.RegisterClassEx(ref cls) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
        }
        Handle = NativeMethods.CreateWindowEx(0x00200000 | (uint)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE), ClassName, "FloatingDock", (uint)NativeMethods.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Windows.Add(Handle, this);
        WindowStyler.MakeOverlayWindow(Handle, noActivate: true);
        // Optional visibility to desktop test tools; the input and rendering paths remain identical.
        if (Environment.GetCommandLineArgs().Contains("--diagnostic-input"))
            NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, new IntPtr((NativeMethods.GetExStyle(Handle) & ~(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) | NativeMethods.WS_EX_APPWINDOW));
    }

    public void Capture() => SetCapture(Handle);
    public void Release()
    {
        if (GetCapture() != Handle) return;
        _releasingCapture = true;
        try { ReleaseCapture(); }
        finally { _releasingCapture = false; }
    }
    public void Focus() { WindowStyler.BringToFront(Handle); SetFocus(Handle); }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!Windows.TryGetValue(hwnd, out var window)) return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
        try { return window.OnMessage(message, wParam, lParam); }
        catch (Exception ex) { try { window.InputFailed?.Invoke(ex); } catch { } return IntPtr.Zero; }
    }

    private IntPtr OnMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xffff), y = (short)((lParam.ToInt64() >> 16) & 0xffff);
        var kind = message switch
        {
            0x200 => DockPointerKind.Move, 0x2a3 => DockPointerKind.Leave,
            0x201 => DockPointerKind.LeftDown, 0x202 => DockPointerKind.LeftUp,
            0x205 => DockPointerKind.RightUp, 0x208 => DockPointerKind.MiddleUp,
            0x1f or 0x215 => DockPointerKind.Cancel, _ => (DockPointerKind?)null,
        };
        if (kind is { } pointerKind)
        {
            if (pointerKind == DockPointerKind.Cancel && _releasingCapture) return IntPtr.Zero;
            if (pointerKind == DockPointerKind.Move && !_tracking)
            {
                var tracking = new Tracking { Size = (uint)Marshal.SizeOf<Tracking>(), Flags = 2, Window = Handle };
                _tracking = TrackMouseEvent(ref tracking);
            }
            if (pointerKind == DockPointerKind.Leave) _tracking = false;
            PointerMessages++;
            Pointer?.Invoke(new(pointerKind, x, y, (wParam.ToInt64() & 1) != 0));
            return IntPtr.Zero;
        }
        switch (message)
        {
            case 0x3d when AccessibilityHost!=IntPtr.Zero: return SendMessage(AccessibilityHost,message,wParam,lParam); // WM_GETOBJECT
            case >=0x100 and <=0x109 when AccessibilityHost!=IntPtr.Zero: return SendMessage(AccessibilityHost,message,wParam,lParam);
            case 0x10: CloseRequested?.Invoke(); return IntPtr.Zero; // restore taskbar through application shutdown
            case 0x14: return new IntPtr(1); // no opaque GDI background
            case 0x20 when (lParam.ToInt64() & 0xffff) == 1:
                SetCursor(NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(32512)));
                return new IntPtr(1);
            case 0x21: // WM_MOUSEACTIVATE: preserve the foreground for normal dock clicks
                return new IntPtr((NativeMethods.GetExStyle(Handle) & NativeMethods.WS_EX_NOACTIVATE) != 0 ? 3 : 1);
            case 0x100: KeyDown?.Invoke(wParam.ToInt32()); return IntPtr.Zero;
            case 0x7b when lParam.ToInt64() == -1: KeyDown?.Invoke(0x5d); return IntPtr.Zero;
            case 0x6: Activated?.Invoke((wParam.ToInt64() & 0xffff) != 0); break;
        }
        return NativeMethods.DefWindowProc(Handle, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Release();
        Windows.Remove(Handle);
        NativeMethods.DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }
}
