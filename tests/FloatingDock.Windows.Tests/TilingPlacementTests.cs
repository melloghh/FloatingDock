using System.Runtime.InteropServices;
using FloatingDock.Core.Models;
using FloatingDock.Core.Tiling;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Xunit;

namespace FloatingDock.Windows.Tests;

public sealed class TilingPlacementTests
{
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [Fact]
    public async Task DelayedOwnMinimizeEventCannotMinimizeAnAlreadyRestoredWindow()
    {
        using var fixture=new BusyWindow();long id=fixture.Handle.ToInt64();
        using var session=new NativeTilingSession {DiagnosticWindows=new HashSet<long>{id}};
        var info=new WindowInfo(id,Environment.ProcessId,null,null,"Owned delayed event fixture","STATIC",false);
        session.Reconcile(new[]{info},0);var mon=FloatingDock.Windows.Display.MonitorHelper.Primary()!;
        session.SwitchWorkspace(2,mon.DeviceName);await Task.Delay(100);
        session.SwitchWorkspace(1,mon.DeviceName);await Task.Delay(100);
        Assert.False(session.ObserveNativeMinimize(id));
        Assert.False(session.IsUserMinimized(id));
    }
    [Fact]
    public async Task SwitchingAwayWhileRestoreIsQueuedKeepsTheWindowOnItsWorkspace()
    {
        using var fixture=new BusyWindow();long id=fixture.Handle.ToInt64();
        using var session=new NativeTilingSession {DiagnosticWindows=new HashSet<long>{id}};
        var info=new WindowInfo(id,Environment.ProcessId,null,null,"Owned queued workspace fixture","STATIC",false);
        session.Reconcile(new[]{info},0);var mon=FloatingDock.Windows.Display.MonitorHelper.Primary()!;
        session.SwitchWorkspace(2,mon.DeviceName);await Task.Delay(100);session.ObserveNativeMinimize(id);
        fixture.Block();
        try {session.SwitchWorkspace(1,mon.DeviceName);session.SwitchWorkspace(2,mon.DeviceName);}
        finally {fixture.Release();}
        await Task.Delay(150);session.Reconcile(new[]{info},0);await Task.Delay(150);
        Assert.False(session.CanFollowActivation(id));
        Assert.DoesNotContain(id,session.Targets().Keys);
        session.SwitchWorkspace(1,mon.DeviceName);await Task.Delay(100);session.Reconcile(new[]{info},0);
        Assert.Contains(id,session.Targets().Keys);
    }
    [Fact]
    public async Task ExplicitMinimizeSurvivesATrackerPassWhileItsOwnerIsBusy()
    {
        using var fixture=new BusyWindow();long id=fixture.Handle.ToInt64();
        using var session=new NativeTilingSession {DiagnosticWindows=new HashSet<long>{id}};
        var info=new WindowInfo(id,Environment.ProcessId,null,null,"Owned pending minimize fixture","STATIC",false);
        session.Reconcile(new[]{info},0);fixture.Block();
        try{Assert.True(session.ObserveNativeMinimize(id,userRequest:true));session.Reconcile(new[]{info},0);Assert.True(session.IsUserMinimized(id));Assert.DoesNotContain(id,session.Targets().Keys);}
        finally{fixture.Release();}
        session.ActivateWindow(id);session.Reconcile(new[]{info},0);Assert.Contains(id,session.Targets().Keys);
    }
    [Fact]
    public async Task ExplicitActivationMovesAUserMinimizedWindowAndRestoresItsTile()
    {
        using var first=new BusyWindow();using var second=new BusyWindow();long a=first.Handle.ToInt64(),b=second.Handle.ToInt64();
        using var session=new NativeTilingSession {DiagnosticWindows=new HashSet<long>{a,b}};
        WindowInfo Info(BusyWindow w)=>new(w.Handle.ToInt64(),Environment.ProcessId,null,null,"Owned two workspace fixture","STATIC",false);
        var mon=FloatingDock.Windows.Display.MonitorHelper.Primary()!;
        session.Reconcile(new[]{Info(first)},0);session.SwitchWorkspace(2,mon.DeviceName);await Task.Delay(100);
        session.Reconcile(new[]{Info(first),Info(second)},0);ShowWindow(second.Handle,6);await Task.Delay(100);session.ObserveNativeMinimize(b);
        session.SwitchWorkspace(1,mon.DeviceName);await Task.Delay(100);ShowWindow(first.Handle,6);session.ObserveNativeMinimize(a,userRequest:true);
        Assert.True(session.ActivateWindow(b));await Task.Delay(100);session.Reconcile(new[]{Info(first),Info(second)},0);
        Assert.Equal(1,session.ActiveWorkspace(mon.DeviceName));Assert.Contains(b,session.Targets().Keys);Assert.DoesNotContain(a,session.Targets().Keys);
        Assert.False(session.IsUserMinimized(b));Assert.True(session.IsUserMinimized(a));
    }
    [Fact]
    public async Task OurFullMonitorPlacementDoesNotAccidentallyFloatTheOnlyTile()
    {
        using var fixture=new BusyWindow();long id=fixture.Handle.ToInt64();
        using var session=new NativeTilingSession {DiagnosticWindows=new HashSet<long>{id},Gap=0,OuterGap=0};
        var info=new WindowInfo(id,Environment.ProcessId,null,null,"Owned zero gap fixture","STATIC",false);
        session.Reconcile(new[]{info},0);var bounds=FloatingDock.Windows.Display.MonitorHelper.Primary()!.Bounds;
        Assert.True(session.Move(id,new(bounds.Left,bounds.Top,bounds.Width,bounds.Height)));await Task.Delay(100);
        session.Reconcile(new[]{info},0);Assert.Contains(id,session.Targets().Keys);
    }
    [Fact]
    public async Task BusyOwnerDoesNotBlockOpacityAcquisitionOrRestoration()
    {
        using var fixture=new BusyWindow();long original=GetWindowLongPtr(fixture.Handle,-20).ToInt64();
        var lease=WindowOpacityLease.TryAcquire(fixture.Handle);Assert.NotNull(lease);
        fixture.Block();var clock=System.Diagnostics.Stopwatch.StartNew();
        lease.Dispose();Assert.True(clock.ElapsedMilliseconds<150);
        clock.Restart();Assert.Null(WindowOpacityLease.TryAcquire(fixture.Handle));Assert.True(clock.ElapsedMilliseconds<150);
        fixture.Release();for(int i=0;i<20&&GetWindowLongPtr(fixture.Handle,-20).ToInt64()!=original;i++)await Task.Delay(20);
        Assert.Equal(original,GetWindowLongPtr(fixture.Handle,-20).ToInt64());
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out RECT rect);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle,string cls,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
    [Fact]
    public async Task HostedAppIdentityUsesTheActualNativeOwnerForMovement()
    {
        using var fixture=new BusyWindow();using var session=new NativeTilingSession {DiagnosticWindows=new HashSet<long>{fixture.Handle.ToInt64()}};
        session.Reconcile(new[]{new WindowInfo(fixture.Handle.ToInt64(),Environment.ProcessId+1,"hosted-app.exe",null,"Owned hosted identity fixture","STATIC",false)},0);
        Assert.True(session.Move(fixture.Handle.ToInt64(),new(260,240,500,350)));
        await Task.Delay(100);GetWindowRect(fixture.Handle,out var moved);Assert.Equal(260,moved.Left);Assert.Equal(240,moved.Top);
    }
    [Fact]
    public async Task LateOwnRestoreAcknowledgementDoesNotBecomeAUserActivation()
    {
        using var fixture=new BusyWindow();using var session=new NativeTilingSession {DiagnosticWindows=new HashSet<long>{fixture.Handle.ToInt64()}};
        session.Reconcile(new[]{new WindowInfo(fixture.Handle.ToInt64(),Environment.ProcessId,null,null,"Owned workspace fixture","STATIC",false)},fixture.Handle.ToInt64());
        var monitor=FloatingDock.Windows.Display.MonitorHelper.Primary()!;
        session.SwitchWorkspace(2,monitor.DeviceName);await Task.Delay(70);session.ObserveNativeMinimize(fixture.Handle.ToInt64());
        session.SwitchWorkspace(1,monitor.DeviceName);await Task.Delay(70);session.SwitchWorkspace(2,monitor.DeviceName);await Task.Delay(70);
        session.ObserveNativeRestore(fixture.Handle.ToInt64());Assert.False(session.CanFollowActivation(fixture.Handle.ToInt64()));
        ShowWindow(fixture.Handle,9);session.ObserveNativeRestore(fixture.Handle.ToInt64());Assert.True(session.CanFollowActivation(fixture.Handle.ToInt64()));
    }
    [Fact]
    public async Task StaleForegroundDoesNotSendNewWindowToAnotherWorkspace()
    {
        using var first=new BusyWindow();using var second=new BusyWindow();
        using var session=new NativeTilingSession { DiagnosticWindows=new HashSet<long>{first.Handle.ToInt64(),second.Handle.ToInt64()} };
        WindowInfo Info(BusyWindow w)=>new(w.Handle.ToInt64(),Environment.ProcessId,null,null,"Owned tiling fixture","STATIC",false);
        var monitor=FloatingDock.Windows.Display.MonitorHelper.Primary()!;
        session.Reconcile(new[]{Info(first)},first.Handle.ToInt64());session.SwitchWorkspace(2,monitor.DeviceName);await Task.Delay(100);
        session.Reconcile(new[]{Info(first),Info(second)},second.Handle.ToInt64());await Task.Delay(100);
        ShowWindow(first.Handle,9); // A late activation arrives after the new window was admitted.
        session.Reconcile(new[]{Info(first),Info(second)},first.Handle.ToInt64());await Task.Delay(100);
        Assert.Equal(2,session.ActiveWorkspace(monitor.DeviceName));
        Assert.Contains(second.Handle.ToInt64(),session.Targets().Keys);
    }
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
