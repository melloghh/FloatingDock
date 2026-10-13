using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;

namespace FloatingDock.Windows.Integration;

/// <summary>One notification action's native root menu, including menus owned by
/// the notified application rather than Explorer. No injection or permanent hook.</summary>
public sealed class TrayContextMenuAnchor : IDisposable
{
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG message,IntPtr window,uint first,uint last,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(in MSG message);
    private readonly POINT _anchor;
    private readonly string _executable;
    private readonly Thread _thread;
    private readonly WinEventProc _callback;
    private readonly ManualResetEventSlim _ready=new(),_positioned=new(),_stop=new();
    private readonly Dictionary<uint,bool> _owners=new();
    private readonly HashSet<IntPtr> _existing=new();
    private volatile bool _armed;
    private long _root;
    private uint _armedAt;
    private long _placedAt;
    private bool _disposed;
    public IntPtr MenuHandle=>new(Interlocked.Read(ref _root));
    public bool Positioned=>_positioned.IsSet;
    public TrayContextMenuAnchor(POINT anchor,string executable)
    {
        _anchor=anchor;_executable=Path.GetFileName(Environment.ExpandEnvironmentVariables(executable));
        _callback=OnEvent;
        _thread=new Thread(Run){IsBackground=true,Name="FloatingDock.TrayMenuAnchor"};
        _thread.Start();_ready.Wait(500);
    }
    public void Arm()
    {
        NativeMethods.EnumWindows((window,_)=> {if(NativeMethods.IsWindowVisible(window)&&NativeMethods.GetClass(window)=="#32768")_existing.Add(window);return true;},IntPtr.Zero);
        _armedAt=unchecked((uint)Environment.TickCount);_armed=true;
    }
    public bool WaitForMenu(int milliseconds=800)=>_positioned.Wait(milliseconds);
    private bool Owns(IntPtr window)
    {
        NativeMethods.GetWindowThreadProcessId(window,out uint pid);
        if(!_owners.TryGetValue(pid,out bool matches))
        {
            string? path=WindowIdentityResolver.GetExePath(pid);
            _owners[pid]=matches=path is not null&&Path.GetFileName(path).Equals(_executable,StringComparison.OrdinalIgnoreCase);
        }
        return matches;
    }
    private void OnEvent(IntPtr hook,uint kind,IntPtr window,int objectId,int childId,uint thread,uint time)
    {
        if(!_armed||unchecked((int)(time-_armedAt))<0)return;
        if(kind==NativeMethods.EVENT_OBJECT_SHOW&&(objectId!=NativeMethods.OBJID_WINDOW||childId!=0))return;
        if(window!=IntPtr.Zero&&NativeMethods.GetClass(window)=="#32768")TryPosition(window);
    }
    private void TryPosition(IntPtr window)
    {
        if(!_armed||!NativeMethods.IsWindowVisible(window)||!Owns(window))return;
        var root=MenuHandle;
        if(root!=IntPtr.Zero&&window!=root)return; // Submenus retain Windows' placement next to their parent.
        if(root==IntPtr.Zero&&_existing.Contains(window))return;
        if(!NativeMethods.GetWindowRect(window,out var rect)||rect.Width<1||rect.Height<1)return;
        var monitor=MonitorHelper.Describe(NativeMethods.MonitorFromPoint(_anchor,NativeMethods.MONITOR_DEFAULTTONEAREST));
        if(monitor is null)return;
        int inset=(int)Math.Ceiling(4*monitor.Scale);
        var area=monitor.WorkArea;
        int x=Math.Clamp(_anchor.X,area.Left+inset,Math.Max(area.Left+inset,area.Right-inset-rect.Width));
        int y=_anchor.Y+(int)Math.Ceiling(8*monitor.Scale);
        if(y+rect.Height>area.Bottom-inset)y=_anchor.Y-rect.Height-(int)Math.Ceiling(8*monitor.Scale);
        y=Math.Clamp(y,area.Top+inset,Math.Max(area.Top+inset,area.Bottom-inset-rect.Height));
        if(rect.Left!=x||rect.Top!=y)
            if(!NativeMethods.SetWindowPos(window,IntPtr.Zero,x,y,0,0,NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE|NativeMethods.SWP_NOZORDER))return;
        Interlocked.Exchange(ref _root,window.ToInt64());
        if(!_positioned.IsSet){_placedAt=Stopwatch.GetTimestamp();_positioned.Set();}
    }
    private void Run()
    {
        var menuHook=NativeMethods.SetWinEventHook(6,6,IntPtr.Zero,_callback,0,0,NativeMethods.WINEVENT_OUTOFCONTEXT);
        var showHook=NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_SHOW,NativeMethods.EVENT_OBJECT_SHOW,IntPtr.Zero,_callback,0,0,NativeMethods.WINEVENT_OUTOFCONTEXT);
        _ready.Set();var clock=Stopwatch.StartNew();
        try
        {
            while(!_stop.IsSet&&clock.ElapsedMilliseconds<2500)
            {
                while(PeekMessage(out var message,IntPtr.Zero,0,0,1))DispatchMessage(message);
                if(_armed)
                {
                    if(MenuHandle!=IntPtr.Zero)TryPosition(MenuHandle);
                    else NativeMethods.EnumWindows((window,_)=> {if(NativeMethods.GetClass(window)=="#32768")TryPosition(window);return MenuHandle==IntPtr.Zero;},IntPtr.Zero);
                    if(_positioned.IsSet&&Stopwatch.GetElapsedTime(_placedAt).TotalMilliseconds>=120)break;
                }
                _stop.Wait(8);
            }
        }
        finally
        {
            if(menuHook!=IntPtr.Zero)NativeMethods.UnhookWinEvent(menuHook);
            if(showHook!=IntPtr.Zero)NativeMethods.UnhookWinEvent(showHook);
            GC.KeepAlive(_callback);
        }
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        _stop.Set();_thread.Join();_ready.Dispose();_positioned.Dispose();_stop.Dispose();
    }
}
