using System.Runtime.InteropServices;
using FloatingDock.Windows.WindowManagement;
using Xunit;

namespace FloatingDock.Windows.Tests;

public class NativeDockWindowTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    private static IntPtr Point(int x, int y) => new(((y & 0xffff) << 16) | (x & 0xffff));

    [Fact]
    public void PointerDispatch_PreservesSignedCoordinatesAndContinuesAfterCancel()
    {
        using var window = new NativeDockWindow();
        var received = new List<DockPointerEvent>();
        window.Pointer += received.Add;
        SendMessage(window.Handle, 0x200, IntPtr.Zero, Point(-120, -54));
        SendMessage(window.Handle, 0x1f, IntPtr.Zero, IntPtr.Zero);
        SendMessage(window.Handle, 0x201, new IntPtr(1), Point(30, 60));
        SendMessage(window.Handle, 0x202, IntPtr.Zero, Point(30, 60));
        Assert.Equal(new DockPointerEvent(DockPointerKind.Move, -120, -54, false), received[0]);
        Assert.Equal(new[] { DockPointerKind.Move, DockPointerKind.Cancel, DockPointerKind.LeftDown, DockPointerKind.LeftUp }, received.Select(p => p.Kind));
        Assert.Equal(4, window.PointerMessages);
    }

    [Fact]
    public void ReleasingOurCapture_DoesNotCancelTheCompletedClick()
    {
        using var window = new NativeDockWindow();
        var received = new List<DockPointerEvent>();
        window.Pointer += received.Add;
        window.Capture();
        Assert.Equal(window.Handle, GetCapture());
        window.Release();
        Assert.NotEqual(window.Handle, GetCapture());
        Assert.DoesNotContain(received, p => p.Kind == DockPointerKind.Cancel);
        SendMessage(window.Handle, 0x205, IntPtr.Zero, Point(30, 60));
        Assert.Equal(DockPointerKind.RightUp, received.Last().Kind);
    }

    [Fact]
    public void CloseRequest_InvokesOrderlyShutdownBeforeDestroyingTheWindow()
    {
        using var window = new NativeDockWindow();
        int requests = 0;
        window.CloseRequested += () => requests++;
        SendMessage(window.Handle, 0x10, IntPtr.Zero, IntPtr.Zero);
        Assert.Equal(1, requests);
        Assert.True(IsWindow(window.Handle));
    }

    [Fact]
    public void Disposal_DestroysTheInputWindowAndIsIdempotent()
    {
        var window = new NativeDockWindow();
        var handle = window.Handle;
        Assert.True(IsWindow(handle));
        window.Dispose();
        window.Dispose();
        Assert.Equal(IntPtr.Zero, window.Handle);
        Assert.False(IsWindow(handle));
    }
}
