using System.Runtime.InteropServices;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Xunit;

namespace FloatingDock.Windows.Tests;

public class ShellAnimationRectTests
{
    [StructLayout(LayoutKind.Sequential)] private struct Info { public IntPtr Window; public RECT Rect; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr h,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);

    [Fact]
    public void InvalidPointerDoesNotCallTheProvider()
    {
        bool called = false;
        Assert.False(ShellAnimationRect.TryUpdate(new IntPtr(1), _ => { called = true; return null; }));
        Assert.False(called);
    }

    [Fact]
    public void UnmatchedWindowLeavesThePayloadUntouched()
    {
        WithPayload(new Info { Window = new IntPtr(7), Rect = new RECT { Left = 20, Top = 30, Right = 40, Bottom = 50 } }, p =>
        {
            Assert.False(ShellAnimationRect.TryUpdate(p, _ => null));
            Assert.Equal(20, Marshal.PtrToStructure<Info>(p).Rect.Left);
        });
    }

    [Fact]
    public void NegativeMonitorCoordinatesPreserveTheWindowHandle()
    {
        WithPayload(new Info { Window = new IntPtr(7), Rect = new RECT { Right = 4, Bottom = 4 } }, p =>
        {
            Assert.True(ShellAnimationRect.TryUpdate(p, h => h == new IntPtr(7) ? new RECT { Left = -1800, Top = 100, Right = -1750, Bottom = 150 } : null));
            var info = Marshal.PtrToStructure<Info>(p);
            Assert.Equal(new IntPtr(7), info.Window);
            Assert.Equal(-1800, info.Rect.Left);
            Assert.Equal(150, info.Rect.Bottom);
        });
    }

    [Fact]
    public void PackedPointsRemainPackedAndSigned()
    {
        WithPayload(new Info { Window = new IntPtr(7), Rect = new RECT { Left = 100 | (900 << 16), Top = 140 | (940 << 16) } }, p =>
        {
            Assert.True(ShellAnimationRect.TryUpdate(p, _ => new RECT { Left = -1800, Top = -100, Right = -1750, Bottom = -50 }));
            var r = Marshal.PtrToStructure<Info>(p).Rect;
            Assert.Equal(-1800, (short)(r.Left & 0xffff));
            Assert.Equal(-100, (short)(r.Left >> 16));
            Assert.Equal(-1750, (short)(r.Top & 0xffff));
            Assert.Equal(-50, (short)(r.Top >> 16));
            Assert.Equal(0, r.Right); Assert.Equal(0, r.Bottom);
        });
    }

    [Fact]
    public void PackedCoordinateOverflowDoesNotWrapToAnotherMonitor()
    {
        WithPayload(new Info { Window = new IntPtr(7), Rect = new RECT { Left = 1, Top = 2 } }, p =>
        {
            Assert.False(ShellAnimationRect.TryUpdate(p, _ => new RECT { Left = 40000, Top = 0, Right = 40040, Bottom = 40 }));
            Assert.Equal(1, Marshal.PtrToStructure<Info>(p).Rect.Left);
        });
    }

    [Fact]
    public void EmptyAnchorDoesNotOverwriteTheShellRectangle()
    {
        WithPayload(new Info { Window = new IntPtr(7), Rect = new RECT { Left = 20, Top = 30, Right = 40, Bottom = 50 } }, p =>
        {
            Assert.False(ShellAnimationRect.TryUpdate(p, _ => new RECT()));
            Assert.Equal(20, Marshal.PtrToStructure<Info>(p).Rect.Left);
        });
    }

    [Fact]
    public void ShellMessageReturnsHandledAndCountsOnlyMatchingPayloads()
    {
        using var window = new MessageWindow();
        window.AnimationRectProvider = h => h == new IntPtr(7) ? new RECT { Left = 100, Top = 200, Right = 150, Bottom = 250 } : null;
        WithPayload(new Info { Window = new IntPtr(7) }, p =>
        {
            var result = SendMessage(window.Handle, RegisterWindowMessage("SHELLHOOK"), new IntPtr(5), p);
            Assert.Equal(new IntPtr(1), result);
            Assert.Equal(1, window.AnimationRectsUpdated);
            Assert.Equal(100, Marshal.PtrToStructure<Info>(p).Rect.Left);
        });
        window.Dispose();
        Assert.False(window.AnimationRectHookRegistered);
    }

    [Fact]
    public void ShellRectangleIsAnsweredWhileTheOwnerThreadIsBlocked()
    {
        using var window=new MessageWindow();
        int owner=Environment.CurrentManagedThreadId,providerThread=0;
        window.AnimationRectProvider=_=>{providerThread=Environment.CurrentManagedThreadId;return new RECT{Left=20,Top=400,Right=60,Bottom=440};};
        WithPayload(new Info{Window=new IntPtr(7),Rect=new RECT{Left=100,Top=100,Right=140,Bottom=140}},payload=>
        {
            var request=Task.Run(()=>SendMessageTimeout(window.AnimationRectMessageHandle,RegisterWindowMessage("SHELLHOOK"),new IntPtr(5),payload,2,150,out _));
            // No message pumping on the owner thread, just as during a costly UI frame.
            Assert.True(request.Wait(1000));Assert.NotEqual(IntPtr.Zero,request.Result);
            Assert.NotEqual(owner,providerThread);Assert.Equal(20,Marshal.PtrToStructure<Info>(payload).Rect.Left);
            Assert.Equal(1,window.AnimationRectsUpdated);
        });
    }

    private static void WithPayload(Info info, Action<IntPtr> action)
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<Info>());
        try { Marshal.StructureToPtr(info, p, false); action(p); }
        finally { Marshal.FreeHGlobal(p); }
    }
}
