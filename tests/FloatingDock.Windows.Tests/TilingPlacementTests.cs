using System.Runtime.InteropServices;
using FloatingDock.Core.Models;
using FloatingDock.Core.Tiling;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Xunit;

namespace FloatingDock.Windows.Tests;

public sealed class TilingPlacementTests
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out RECT rect);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle,string cls,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
    [Fact]
    public async Task BusyWindowDoesNotBlockPlacementOrLoseItsRestoreToQueuedMoves()
    {
        using var fixture=new BusyWindow();
        using var session=new NativeTilingSession { DiagnosticWindows=new HashSet<long>{fixture.Handle.ToInt64()} };
        Assert.True(session.Reconcile(new[]{new WindowInfo(fixture.Handle.ToInt64(),Environment.ProcessId,null,null,"Owned tiling fixture","STATIC",false)},0));
        GetWindowRect(fixture.Handle,out var original);
        fixture.Block();
        var move=Task.Run(()=>session.Move(fixture.Handle.ToInt64(),new TileRect(original.Left+70,original.Top+50,500,350)));
        Task? restore=null;
        try
        {
            Assert.Same(move,await Task.WhenAny(move,Task.Delay(500)));Assert.True(await move);
            restore=Task.Run(session.Dispose);
            // Keep the application busy until the restore request has been sent.
            Assert.Same(restore,await Task.WhenAny(restore,Task.Delay(500)));await restore;
        }
        finally { fixture.Release(); }
        await restore!.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(200);
        GetWindowRect(fixture.Handle,out var restored);
        Assert.Equal(original.Left,restored.Left);Assert.Equal(original.Top,restored.Top);
        Assert.Equal(original.Width,restored.Width);Assert.Equal(original.Height,restored.Height);
    }

    private sealed class BusyWindow : IDisposable
    {
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG message,IntPtr window,uint first,uint last);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(in MSG message);
        private readonly ManualResetEventSlim _ready=new(),_blocked=new(),_release=new();
        private readonly Thread _thread;
        public IntPtr Handle { get; private set; }
        public BusyWindow()
        {
            _thread=new Thread(()=>
            {
                // Tool windows are excluded from running-app tracking in any live dock.
                Handle=CreateWindowEx(0x80,"STATIC","Owned tiling fixture",0x10CF0000,200,200,420,300,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
                _ready.Set();
                while(GetMessage(out var message,IntPtr.Zero,0,0)>0)
                {
                    if(message.message==0x8001){_blocked.Set();_release.Wait();}
                    else if(message.message==0x8002)break;
                    else DispatchMessage(message);
                }
                DestroyWindow(Handle);
            }){IsBackground=true};
            _thread.Start();Assert.True(_ready.Wait(2000));Assert.NotEqual(IntPtr.Zero,Handle);
        }
        public void Block(){PostMessage(Handle,0x8001,IntPtr.Zero,IntPtr.Zero);Assert.True(_blocked.Wait(2000));}
        public void Release()=>_release.Set();
        public void Dispose()
        {
            Release();PostMessage(Handle,0x8002,IntPtr.Zero,IntPtr.Zero);
            _thread.Join(2000);_ready.Dispose();_blocked.Dispose();_release.Dispose();
        }
    }
}
