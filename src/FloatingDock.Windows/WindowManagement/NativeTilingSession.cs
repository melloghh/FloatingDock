using System.Runtime.InteropServices;
using FloatingDock.Core.Models;
using FloatingDock.Core.Tiling;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>One opt-in native session. Windows on inactive workspaces are minimized, never hidden or removed from the shell.</summary>
public sealed class NativeTilingSession : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] public struct Placement { public int Length,Flags,Show;public POINT Minimum,Maximum;public RECT Normal; }
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd,ref Placement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd,in Placement placement);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd,uint command);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public POINT Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize; }
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW")] private static extern IntPtr SendMinimumQuery(IntPtr hwnd,uint message,IntPtr wparam,ref MinMaxInfo info,uint flags,uint timeout,out UIntPtr result);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out RECT rect,int size);
    private sealed class Member
    {
        public required int Pid;
        public required Placement Original;
        public required string Monitor;
        public int Workspace=1;
        public bool Floating,MinimizedByUs,UserMinimized,Hidden;
        public int MinimumWidth=120,MinimumHeight=80;
    }
    private readonly Dictionary<long,Member> _members=new();
    private readonly Dictionary<(string Monitor,int Workspace),TileTree> _trees=new();
    private readonly Dictionary<string,int> _active=new(StringComparer.Ordinal);
    private IReadOnlyList<MonitorDescriptor> _monitors=Array.Empty<MonitorDescriptor>();
    private HashSet<string> _excluded=new(StringComparer.OrdinalIgnoreCase);
    private long _focused;
    private bool _disposed;
    public int Gap { get; set; }=8;
    public int OuterGap { get; set; }=12;
    public int WorkspaceCount { get; set; }=5;
    public IReadOnlySet<long>? DiagnosticWindows { get; set; }
    public int ManagedCount=>_members.Count;
    public IReadOnlyList<long> ManagedWindows=>_members.Keys.ToArray();
    public string DescribeForDiagnostics(long id)=>$"focused={_focused}, window={id}, floating={(_members.TryGetValue(id,out var member)?member.Floating:null)}, hidden={member?.Hidden}, minimized={member?.UserMinimized}, minimum={member?.MinimumWidth}x{member?.MinimumHeight}, nativeZoomed={IsZoomed(new(id))}";
    public void SetExcluded(string names)=>_excluded=new(names.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries),StringComparer.OrdinalIgnoreCase);
    private bool Owns(long hwnd,Member member)
    {NativeMethods.GetWindowThreadProcessId(new(hwnd),out var pid);return pid==member.Pid&&NativeMethods.IsWindow(new(hwnd));}
    private TileTree Tree(Member member)=>Tree(member.Monitor,member.Workspace);
    private TileTree Tree(string monitor,int workspace)
    {if(!_trees.TryGetValue((monitor,workspace),out var tree))_trees[(monitor,workspace)]=tree=new();return tree;}
    public int ActiveWorkspace(string monitor)=>_active.GetValueOrDefault(monitor,1);
    private string CurrentMonitor()=>_members.TryGetValue(_focused,out var m)?m.Monitor:(MonitorHelper.FromCursor()??_monitors.FirstOrDefault())?.DeviceName??"";
    public void RefreshMonitors()=>_monitors=MonitorHelper.GetMonitors();
    public bool Forget(long window)
    {if(!_members.Remove(window,out var member))return false;Tree(member).Remove(window);return true;}
    public bool Reconcile(IReadOnlyList<WindowInfo> windows,long foreground)
    {
        if(_disposed)return false;
        RefreshMonitors();bool changed=false;
        var live=windows.Select(w=>w.Hwnd).ToHashSet();
        foreach(var (id,member) in _members.ToArray())
        {
            if(!Owns(id,member)){Tree(member).Remove(id);_members.Remove(id);changed=true;continue;}
            var info=windows.FirstOrDefault(w=>w.Hwnd==id);
            bool hidden=(!live.Contains(id)&&!member.MinimizedByUs)||!NativeMethods.IsWindowVisible(new(id));
            if(hidden!=member.Hidden)
            {
                member.Hidden=hidden;if(hidden)Tree(member).Remove(id);else if(!member.Floating)Tree(member).Add(id);
                changed=true;
            }
            if(info is not null&&_excluded.Contains(info.ExeFileName??"")){Restore(id,member);Tree(member).Remove(id);_members.Remove(id);changed=true;continue;}
            if(!_monitors.Any(m=>m.DeviceName==member.Monitor))
            {Tree(member).Remove(id);member.Monitor=_monitors.FirstOrDefault()?.DeviceName??"";member.Workspace=1;if(!member.Floating)Tree(member).Add(id);changed=true;}
            if(member.Workspace>WorkspaceCount){Tree(member).Remove(id);member.Workspace=1;if(!member.Floating)Tree(member).Add(id);changed=true;}
            bool minimized=NativeMethods.IsIconic(new(id))&&!member.MinimizedByUs;
            if(minimized!=member.UserMinimized){member.UserMinimized=minimized;changed=true;}
            // Maximizing deliberately floats an existing window. User resizing is handled on move/size end.
            var memberMonitor=_monitors.FirstOrDefault(m=>m.DeviceName==member.Monitor);
            bool fullscreen=memberMonitor is not null&&FullscreenDetector.IsWindowFullscreenOn(new(id),memberMonitor.Handle,0);
            if((IsZoomed(new(id))||fullscreen||(NativeMethods.GetStyle(new(id))&NativeMethods.WS_THICKFRAME)==0)&&!member.Floating){Tree(member).Remove(id);member.Floating=true;changed=true;}
        }
        foreach(var window in windows)
        {
            var hwnd=new IntPtr(window.Hwnd);
            if(_members.ContainsKey(window.Hwnd)||!live.Contains(window.Hwnd)||_excluded.Contains(window.ExeFileName??""))continue;
            if(DiagnosticWindows is not null&&!DiagnosticWindows.Contains(window.Hwnd))continue;
            if(window.ProcessId==Environment.ProcessId&&DiagnosticWindows is null)continue;
            if(window.IsMinimized||IsZoomed(hwnd)||!NativeMethods.IsWindowVisible(hwnd)||(NativeMethods.GetStyle(hwnd)&0x40000)==0)continue;
            var owner=GetWindow(hwnd,4);if(owner!=IntPtr.Zero&&NativeMethods.IsWindowVisible(owner))continue;
            var monitor=_monitors.FirstOrDefault(m=>m.Handle==NativeMethods.MonitorFromWindow(hwnd,NativeMethods.MONITOR_DEFAULTTONEAREST));
            if(monitor is null)continue;
            if(FullscreenDetector.IsWindowFullscreenOn(hwnd,monitor.Handle,0))continue;
            var placement=new Placement { Length=Marshal.SizeOf<Placement>() };if(!GetWindowPlacement(hwnd,ref placement))continue;
            var member=new Member { Pid=window.ProcessId,Original=placement,Monitor=monitor.DeviceName,Workspace=ActiveWorkspace(monitor.DeviceName) };
            var minimum=new MinMaxInfo();
            if(SendMinimumQuery(hwnd,0x24,IntPtr.Zero,ref minimum,2,40,out _)!=IntPtr.Zero)
            {member.MinimumWidth=Math.Max(120,minimum.MinTrackSize.X);member.MinimumHeight=Math.Max(80,minimum.MinTrackSize.Y);}
            _members[window.Hwnd]=member;
            var rect=monitor.WorkArea;Tree(member).Add(window.Hwnd,_focused,rect.Width>=rect.Height);changed=true;
        }
        foreach(var monitor in _monitors)
        {
            if(ActiveWorkspace(monitor.DeviceName)>WorkspaceCount){_active[monitor.DeviceName]=1;changed=true;}
            if(changed)SwitchWorkspace(ActiveWorkspace(monitor.DeviceName),monitor.DeviceName);
        }
        if(_members.TryGetValue(foreground,out var focused))
        {
            _focused=foreground;
            if(focused.Workspace!=ActiveWorkspace(focused.Monitor)&&!NativeMethods.IsIconic(new(foreground)))
            {SwitchWorkspace(focused.Workspace,focused.Monitor);changed=true;}
        }
        else
        {
            NativeMethods.GetWindowThreadProcessId(new(foreground),out var pid);
            if(pid!=(uint)Environment.ProcessId)_focused=0;
        }
        return changed;
    }
    public IReadOnlyDictionary<long,TileRect> Targets()
    {
        var result=new Dictionary<long,TileRect>();
        foreach(var monitor in _monitors)
        {
            var tree=Tree(monitor.DeviceName,ActiveWorkspace(monitor.DeviceName));
            // Minimized windows keep their membership/order without occupying a tile.
            var usable=tree.Windows.Where(id=>_members.TryGetValue(id,out var m)&&!m.UserMinimized&&!m.Floating&&!m.Hidden).ToHashSet();
            var area=monitor.WorkArea;
            bool repeat;
            do
            {
                repeat=false;
                foreach(var (id,rect) in tree.Layout(new(area.Left,area.Top,area.Width,area.Height),(int)(Gap*monitor.Scale),(int)(OuterGap*monitor.Scale),usable))
                {
                    var target=FrameToWindow(new(id),rect);var member=_members[id];
                    if(target.Width<member.MinimumWidth||target.Height<member.MinimumHeight)
                    {
                        tree.Remove(id);usable.Remove(id);member.Floating=true;result.Remove(id);
                        if(Owns(id,member))SetWindowPlacement(new(id),member.Original);
                        repeat=true;break;
                    }
                    result[id]=target;
                }
            }while(repeat);
        }
        return result;
    }
    private static TileRect FrameToWindow(IntPtr hwnd,TileRect frame)
    {
        if(NativeMethods.GetWindowRect(hwnd,out var outer)&&DwmGetWindowAttribute(hwnd,9,out var visible,Marshal.SizeOf<RECT>())>=0)
            return new(frame.X-(visible.Left-outer.Left),frame.Y-(visible.Top-outer.Top),frame.Width+(visible.Left-outer.Left)+(outer.Right-visible.Right),frame.Height+(visible.Top-outer.Top)+(outer.Bottom-visible.Bottom));
        return frame;
    }
    public void SwitchWorkspace(int workspace,string? monitor=null)
    {
        if(_disposed)return;workspace=Math.Clamp(workspace,1,WorkspaceCount);monitor??=CurrentMonitor();_active[monitor]=workspace;
        foreach(var (id,member) in _members.Where(p=>p.Value.Monitor==monitor))
        {
            if(!Owns(id,member)||member.Hidden)continue;
            if(member.Workspace!=workspace)
            {if(!NativeMethods.IsIconic(new(id))){member.MinimizedByUs=true;NativeMethods.ShowWindow(new(id),6);}}
            else if(member.MinimizedByUs){member.MinimizedByUs=false;NativeMethods.ShowWindow(new(id),4);}
        }
    }
    public void MoveToWorkspace(int workspace)
    {
        if(!_members.TryGetValue(_focused,out var member))return;
        Tree(member).Remove(_focused);member.Workspace=Math.Clamp(workspace,1,WorkspaceCount);if(!member.Floating)Tree(member).Add(_focused);
        SwitchWorkspace(ActiveWorkspace(member.Monitor),member.Monitor);
    }
    public void ToggleFloating()
    {
        if(!_members.TryGetValue(_focused,out var m))return;
        if(m.Floating&&(NativeMethods.GetStyle(new(_focused))&NativeMethods.WS_THICKFRAME)==0)return;
        m.Floating=!m.Floating;if(m.Floating){Tree(m).Remove(_focused);if(Owns(_focused,m))SetWindowPlacement(new(_focused),m.Original);}
        else{NativeMethods.ShowWindow(new(_focused),9);Tree(m).Add(_focused);}
    }
    public void ToggleDirection(){if(_members.TryGetValue(_focused,out var m))Tree(m).ToggleDirection(_focused);}
    public void Resize(TileDirection direction){if(_members.TryGetValue(_focused,out var m))Tree(m).Resize(_focused,direction);}
    public void Navigate(TileDirection direction,bool move)
    {
        var layout=Targets();long next=TileTree.Neighbor(_focused,direction,layout);if(next==0)return;
        if(move&&_members.TryGetValue(_focused,out var m))
        {
            if(_members[next].Monitor==m.Monitor)Tree(m).Swap(_focused,next);
            else{Tree(m).Remove(_focused);m.Monitor=_members[next].Monitor;m.Workspace=ActiveWorkspace(m.Monitor);Tree(m).Add(_focused,next);}
        }
        else{_focused=next;NativeMethods.SetForegroundWindow(new(next));}
    }
    public void MoveFinished(long window)
    {
        if(!_members.TryGetValue(window,out var m)||m.Floating)return;
        var monitor=MonitorHelper.Describe(NativeMethods.MonitorFromWindow(new(window),NativeMethods.MONITOR_DEFAULTTONEAREST));
        if(monitor is not null&&monitor.DeviceName!=m.Monitor)
        {Tree(m).Remove(window);m.Monitor=monitor.DeviceName;m.Workspace=ActiveWorkspace(m.Monitor);Tree(m).Add(window);}
        else if(NativeMethods.GetWindowRect(new(window),out var rect))
        {
            var layout=Targets();var overlap=layout.Where(p=>p.Key!=window&&_members[p.Key].Monitor==m.Monitor&&rect.Left+rect.Width/2>=p.Value.X&&rect.Left+rect.Width/2<p.Value.X+p.Value.Width&&rect.Top+rect.Height/2>=p.Value.Y&&rect.Top+rect.Height/2<p.Value.Y+p.Value.Height).FirstOrDefault();
            if(overlap.Key!=0)Tree(m).Swap(window,overlap.Key);
        }
    }
    public bool Move(long id,TileRect rect)
    {
        if(!_members.TryGetValue(id,out var member)||!Owns(id,member)||member.Floating||member.UserMinimized||IsZoomed(new(id))||(NativeMethods.GetStyle(new(id))&NativeMethods.WS_THICKFRAME)==0||member.Workspace!=ActiveWorkspace(member.Monitor))return false;
        return NativeMethods.SetWindowPos(new(id),IntPtr.Zero,rect.X,rect.Y,Math.Max(1,rect.Width),Math.Max(1,rect.Height),NativeMethods.SWP_NOZORDER|NativeMethods.SWP_NOACTIVATE);
    }
    private void Restore(long id,Member member)
    {
        if(!Owns(id,member))return;
        if(member.Hidden)return;
        if(member.Floating){if(member.MinimizedByUs)NativeMethods.ShowWindow(new(id),4);return;}
        var placement=member.Original;if(member.UserMinimized&&!member.MinimizedByUs)placement.Show=2;
        SetWindowPlacement(new(id),placement);
    }
    public void Dispose(){if(_disposed)return;_disposed=true;foreach(var (id,member) in _members)Restore(id,member);_members.Clear();_trees.Clear();}
}
