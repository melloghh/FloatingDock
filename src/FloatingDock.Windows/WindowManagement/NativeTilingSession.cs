using System.Runtime.InteropServices;
using System.Diagnostics;
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
        public bool Floating,MinimizedByUs,UserMinimized,Hidden,PendingRestore,Placed,RestoredExternally,InitialPlacementPending;
        public readonly WindowBorderAccent Border=new();
        public TileRect? LastRequest;
        public long SubmittedAt;
        public bool MinimizeOutstanding;
        public bool UserMinimizePending;
        public long VisibilitySubmittedAt;
        public int OwnRestoreEvents;
        public int MinimumWidth=480,MinimumHeight=320;
        public (int Left,int Top,int Right,int Bottom)? FrameInsets;
        public uint FrameDpi;
    }
    private readonly Dictionary<long,Member> _members=new();
    private readonly Dictionary<(string Monitor,int Workspace),TileTree> _trees=new();
    private readonly Dictionary<string,int> _active=new(StringComparer.Ordinal);
    private readonly Dictionary<(string Monitor,int Workspace),long> _lastFocused=new();
    private IReadOnlyList<MonitorDescriptor> _monitors=Array.Empty<MonitorDescriptor>();
    private HashSet<string> _excluded=new(StringComparer.OrdinalIgnoreCase);
    private long _focused;
    private bool _disposed;
    public int Gap { get; set; }=8;
    public int OuterGap { get; set; }=12;
    public int WorkspaceCount { get; set; }=5;
    public bool BringActivatedWindows { get; set; }=true;
    public bool TileMaximizedApps { get; set; }=true;
    public bool BordersEnabled { get; set; }
    public uint ActiveBorderColor { get; set; }=0xFAC7A8;
    public uint InactiveBorderColor { get; set; }=0x564941;
    public IReadOnlySet<long>? DiagnosticWindows { get; set; }
    public Action<long,bool,Action>? VisibilityTransition { get; set; }
    public int ManagedCount=>_members.Count;
    public bool IsManaged(long id)=>_members.ContainsKey(id);
    public bool NeedsRestoredPlacement(long id)=>_members.TryGetValue(id,out var member)&&member.PendingRestore&&!NativeMethods.IsIconic(new(id))&&!IsZoomed(new(id));
    public bool HasBeenPlaced(long id)=>_members.TryGetValue(id,out var member)&&member.Placed;
    public bool IsUserMinimized(long id)=>_members.TryGetValue(id,out var member)&&member.UserMinimized;
    public void ReleaseFailedPlacement(long id)
    {
        if(!_members.TryGetValue(id,out var m))return;
        Tree(m).Remove(id);m.Floating=true;m.Border.Restore(new(id));
    }
    public bool PlacementDrifted(long id)
    {
        if(!_members.TryGetValue(id,out var m)||m.Floating||m.UserMinimized||m.Hidden||m.MinimizedByUs||!m.Placed||m.PendingRestore)return false;
        return m.LastRequest is { } r&&NativeMethods.GetWindowRect(new(id),out var actual)&&(Math.Abs(actual.Left-r.X)>2||Math.Abs(actual.Top-r.Y)>2||Math.Abs(actual.Width-r.Width)>2||Math.Abs(actual.Height-r.Height)>2);
    }
    public bool NeedsInitialPlacement(long id)=>_members.TryGetValue(id,out var member)&&member.InitialPlacementPending&&!member.Floating&&!member.UserMinimized&&member.Workspace==ActiveWorkspace(member.Monitor);
    public void FinishInitialPlacement(long id){if(_members.TryGetValue(id,out var member))member.InitialPlacementPending=false;}
    public bool CanFollowActivation(long id)=>_members.TryGetValue(id,out var member)&&(!member.MinimizedByUs||member.RestoredExternally);
    public TileRect? LastPlacement(long id)=>_members.TryGetValue(id,out var member)?member.LastRequest:null;
    public bool ObserveNativeMinimize(long id,bool userRequest=false)
    {
        if(!_members.TryGetValue(id,out var member))return false;
        if(!userRequest&&!NativeMethods.IsIconic(new(id)))return false;
        member.MinimizeOutstanding=false;
        if(member.MinimizedByUs||!userRequest&&member.PendingRestore||member.UserMinimized)return false;
        member.UserMinimized=true;member.UserMinimizePending=userRequest;member.PendingRestore=false;return true;
    }
    public void ObserveNativeRestore(long id)
    {
        if(!_members.TryGetValue(id,out var member))return;
        // An acknowledgement of our older restore can arrive after a later
        // workspace switch. It is not a user's request to bring this app back.
        if(member.OwnRestoreEvents>0)
        {
            member.OwnRestoreEvents--;member.MinimizeOutstanding=false;
            if(member.OwnRestoreEvents==0||!NativeMethods.IsIconic(new(id)))member.PendingRestore=false;
            return;
        }
        if(member.MinimizedByUs&&member.Workspace!=ActiveWorkspace(member.Monitor)&&!NativeMethods.IsIconic(new(id)))member.RestoredExternally=true;
    }
    public bool HasTiles(string monitor)=>_members.Any(p=>p.Value.Monitor==monitor&&p.Value.Workspace==ActiveWorkspace(monitor)&&!p.Value.Floating&&!p.Value.Hidden&&!p.Value.UserMinimized);
    public IReadOnlyList<long> ManagedWindows=>_members.Keys.ToArray();
    public string DescribeForDiagnostics(long id)=>$"focused={_focused}, window={id}, floating={(_members.TryGetValue(id,out var member)?member.Floating:null)}, hidden={member?.Hidden}, minimized={member?.UserMinimized}, workspace={member?.Workspace}, ownMin={member?.MinimizedByUs}, pendingRestore={member?.PendingRestore}, outstandingMin={member?.MinimizeOutstanding}, ownRestores={member?.OwnRestoreEvents}, minimum={member?.MinimumWidth}x{member?.MinimumHeight}, nativeZoomed={IsZoomed(new(id))}, borderHR={member?.Border.LastResult:X8}, borderColor={member?.Border.Applied:X8}";
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
    {if(!_members.Remove(window,out var member))return false;if(Owns(window,member))member.Border.Restore(new(window));Tree(member).Remove(window);if(_focused==window)_focused=0;foreach(var key in _lastFocused.Where(p=>p.Value==window).Select(p=>p.Key).ToArray())_lastFocused.Remove(key);return true;}
    public bool CanManageNew(WindowInfo window)
    {
        var hwnd=new IntPtr(window.Hwnd);
        if(_members.ContainsKey(window.Hwnd)||_excluded.Contains(window.ExeFileName??""))return false;
        if(DiagnosticWindows is not null&&!DiagnosticWindows.Contains(window.Hwnd))return false;
        if(window.ProcessId==Environment.ProcessId&&DiagnosticWindows is null)return false;
        if(window.IsMinimized||NativeMethods.IsIconic(hwnd)||IsZoomed(hwnd)&&!TileMaximizedApps||!NativeMethods.IsWindowVisible(hwnd)||(NativeMethods.GetStyle(hwnd)&NativeMethods.WS_THICKFRAME)==0)return false;
        var owner=GetWindow(hwnd,4);return owner==IntPtr.Zero||!NativeMethods.IsWindowVisible(owner);
    }
    private void AddBalanced(long id,Member member,long near=0)
    {
        var monitor=_monitors.FirstOrDefault(m=>m.DeviceName==member.Monitor);if(monitor is null)return;
        var tree=Tree(member);var minimums=tree.Windows.Append(id).Distinct().Where(w=>_members.TryGetValue(w,out var m)&&!m.UserMinimized&&!m.Hidden&&!m.Floating).ToDictionary(w=>w,w=>(_members[w].MinimumWidth,_members[w].MinimumHeight));
        var rect=monitor.WorkArea;
        if(!tree.AddBalanced(id,near,new(rect.Left,rect.Top,rect.Width,rect.Height),(int)(Gap*monitor.Scale),(int)(OuterGap*monitor.Scale),minimums))
        {member.Floating=true;member.Border.Restore(new(id));if((member.Placed||member.PendingRestore)&&Owns(id,member))RestorePlacement(new(id),member.Original);}
    }
    public bool Reconcile(IReadOnlyList<WindowInfo> windows,long foreground)
    {
        if(_disposed)return false;
        bool changed=false;
        if(_monitors.Count==0)RefreshMonitors();
        var live=windows.ToDictionary(w=>w.Hwnd);
        foreach(var (id,member) in _members.ToArray())
        {
            if(!Owns(id,member)){Tree(member).Remove(id);_members.Remove(id);changed=true;continue;}
            var info=live.GetValueOrDefault(id);
            bool hidden=(!live.ContainsKey(id)&&!member.MinimizedByUs)||!NativeMethods.IsWindowVisible(new(id));
            if(hidden!=member.Hidden)
            {
                member.Hidden=hidden;if(hidden)Tree(member).Remove(id);else if(!member.Floating)AddBalanced(id,member);
                changed=true;
            }
            if(info is not null&&_excluded.Contains(info.ExeFileName??"")){Restore(id,member);Tree(member).Remove(id);_members.Remove(id);changed=true;continue;}
            if(!_monitors.Any(m=>m.DeviceName==member.Monitor))
            {Tree(member).Remove(id);member.Monitor=_monitors.FirstOrDefault()?.DeviceName??"";member.Workspace=1;if(!member.Floating)AddBalanced(id,member);changed=true;}
            if(member.Workspace>WorkspaceCount){Tree(member).Remove(id);member.Workspace=1;if(!member.Floating)AddBalanced(id,member);changed=true;}
            bool iconic=NativeMethods.IsIconic(new(id));
            if(iconic){member.MinimizeOutstanding=false;member.UserMinimizePending=false;}
            if(!iconic&&!IsZoomed(new(id))&&!member.MinimizeOutstanding)member.PendingRestore=false;
            bool minimized=member.UserMinimizePending||iconic&&!member.MinimizedByUs&&!member.PendingRestore;
            if(minimized!=member.UserMinimized){member.UserMinimized=minimized;if(!minimized&&!member.Floating&&!member.Hidden)AddBalanced(id,member);changed=true;}
            // Maximizing deliberately floats an existing window. User resizing is handled on move/size end.
            var memberMonitor=_monitors.FirstOrDefault(m=>m.DeviceName==member.Monitor);
            bool fullscreen=memberMonitor is not null&&FullscreenDetector.IsWindowFullscreenOn(new(id),memberMonitor.Handle,0);
            bool ownPlacement=member.LastRequest is { } placed&&NativeMethods.GetWindowRect(new(id),out var positioned)&&
                Math.Abs(positioned.Left-placed.X)<=2&&Math.Abs(positioned.Top-placed.Y)<=2&&Math.Abs(positioned.Width-placed.Width)<=2&&Math.Abs(positioned.Height-placed.Height)<=2;
            if((IsZoomed(new(id))||fullscreen&&!ownPlacement||(NativeMethods.GetStyle(new(id))&NativeMethods.WS_THICKFRAME)==0)&&!member.Floating&&!member.PendingRestore){Tree(member).Remove(id);member.Floating=true;changed=true;}
        }
        foreach(var window in windows)
        {
            var hwnd=new IntPtr(window.Hwnd);
            if(!CanManageNew(window))continue;
            var monitor=_monitors.FirstOrDefault(m=>m.Handle==NativeMethods.MonitorFromWindow(hwnd,NativeMethods.MONITOR_DEFAULTTONEAREST));
            if(monitor is null)continue;
            if(FullscreenDetector.IsWindowFullscreenOn(hwnd,monitor.Handle,0))continue;
            var placement=new Placement { Length=Marshal.SizeOf<Placement>() };if(!GetWindowPlacement(hwnd,ref placement))continue;
            NativeMethods.GetWindowThreadProcessId(hwnd,out var nativePid);
            var member=new Member { Pid=(int)nativePid,Original=placement,Monitor=monitor.DeviceName,Workspace=ActiveWorkspace(monitor.DeviceName),PendingRestore=IsZoomed(hwnd),InitialPlacementPending=IsZoomed(hwnd) };
            var minimum=new MinMaxInfo();
            if(SendMinimumQuery(hwnd,0x24,IntPtr.Zero,ref minimum,2,40,out _)!=IntPtr.Zero)
            {
                var useful=TileMinimum.Resolve(minimum.MinTrackSize.X,minimum.MinTrackSize.Y,monitor.Scale);
                member.MinimumWidth=useful.Width;member.MinimumHeight=useful.Height;
            }
            else
            {
                var useful=TileMinimum.Resolve(0,0,monitor.Scale);
                member.MinimumWidth=useful.Width;member.MinimumHeight=useful.Height;
            }
            _members[window.Hwnd]=member;
            if(member.PendingRestore)NativeMethods.ShowWindowAsync(hwnd,9);
            AddBalanced(window.Hwnd,member,_focused);changed=true;
        }
        foreach(var monitor in _monitors)
        {
            if(ActiveWorkspace(monitor.DeviceName)>WorkspaceCount){_active[monitor.DeviceName]=1;changed=true;}
            ApplyWorkspaceVisibility(monitor.DeviceName);
        }
        if(_members.TryGetValue(foreground,out var focused))
        {
            // A tracker snapshot can lag a workspace click or a new-window event.
            // Only an explicit activation may change workspace membership/selection.
            if(focused.Workspace==ActiveWorkspace(focused.Monitor)&&!focused.MinimizedByUs)
            {_focused=foreground;_lastFocused[(focused.Monitor,focused.Workspace)]=foreground;}
        }
        else
        {
            NativeMethods.GetWindowThreadProcessId(new(foreground),out var pid);
            if(pid!=(uint)Environment.ProcessId)_focused=0;
        }
        UpdateBorders();return changed;
    }
    public IReadOnlyDictionary<long,TileRect> Targets()
    {
        var result=new Dictionary<long,TileRect>();
        foreach(var monitor in _monitors)
        {
            var tree=Tree(monitor.DeviceName,ActiveWorkspace(monitor.DeviceName));
            // Minimized windows keep their membership/order without occupying a tile.
            var usable=tree.Windows.Where(id=>_members.TryGetValue(id,out var m)&&!m.UserMinimized&&!m.Floating&&!m.Hidden&&!m.InitialPlacementPending).ToHashSet();
            var area=monitor.WorkArea;
            if(usable.Count>0)
            {
                var minimums=usable.ToDictionary(id=>id,id=>(_members[id].MinimumWidth,_members[id].MinimumHeight));
                tree.AddBalanced(usable.First(),0,new(area.Left,area.Top,area.Width,area.Height),(int)(Gap*monitor.Scale),(int)(OuterGap*monitor.Scale),minimums);
            }
            bool repeat;
            do
            {
                repeat=false;
                foreach(var (id,rect) in tree.Layout(new(area.Left,area.Top,area.Width,area.Height),(int)(Gap*monitor.Scale),(int)(OuterGap*monitor.Scale),usable))
                {
                    var member=_members[id];var target=FrameToWindow(new(id),rect,member);
                    if(target.Width<member.MinimumWidth||target.Height<member.MinimumHeight)
                    {
                        tree.Remove(id);usable.Remove(id);member.Floating=true;result.Remove(id);
                        if(member.Placed&&Owns(id,member))RestorePlacement(new(id),member.Original);
                        member.Border.Restore(new(id));
                        repeat=true;break;
                    }
                    result[id]=target;
                }
            }while(repeat);
        }
        return result;
    }
    private static TileRect FrameToWindow(IntPtr hwnd,TileRect frame,Member member)
    {
        uint dpi=NativeMethods.GetDpiForWindow(hwnd);
        // DWM's presented bounds can lag GetWindowRect during movement. Reading
        // their difference every frame turns that lag into a new layout target.
        if(member.FrameInsets is null||member.FrameDpi!=dpi)
        {
            if(NativeMethods.GetWindowRect(hwnd,out var outer)&&DwmGetWindowAttribute(hwnd,9,out var visible,Marshal.SizeOf<RECT>())>=0)
            {member.FrameInsets=(visible.Left-outer.Left,visible.Top-outer.Top,outer.Right-visible.Right,outer.Bottom-visible.Bottom);member.FrameDpi=dpi;}
        }
        if(member.FrameInsets is { } inset)
            return new(frame.X-inset.Left,frame.Y-inset.Top,frame.Width+inset.Left+inset.Right,frame.Height+inset.Top+inset.Bottom);
        return frame;
    }
    public void SwitchWorkspace(int workspace,string? monitor=null)
    {
        if(_disposed)return;workspace=Math.Clamp(workspace,1,WorkspaceCount);monitor??=CurrentMonitor();bool changed=ActiveWorkspace(monitor)!=workspace;_active[monitor]=workspace;
        ApplyWorkspaceVisibility(monitor);
        if(changed)
        {
            var candidates=_members.Where(p=>p.Value.Monitor==monitor&&p.Value.Workspace==workspace&&!p.Value.Hidden&&!p.Value.UserMinimized).Select(p=>p.Key).ToArray();
            long previous=_lastFocused.GetValueOrDefault((monitor,workspace));_focused=candidates.Contains(previous)?previous:candidates.FirstOrDefault();
            if(_focused!=0&&DiagnosticWindows is null)NativeMethods.SetForegroundWindow(new(_focused));
        }
        UpdateBorders();
    }
    private void ApplyWorkspaceVisibility(string monitor)
    {
        int workspace=ActiveWorkspace(monitor);
        foreach(var (id,member) in _members.Where(p=>p.Value.Monitor==monitor))
        {
            if(!Owns(id,member)||member.Hidden)continue;
            if(member.Workspace!=workspace)
            {
                if(member.UserMinimized)continue;
                bool newlyHidden=!member.MinimizedByUs;
                member.MinimizedByUs=true;member.RestoredExternally=false;member.PendingRestore=false;
                if(newlyHidden||!NativeMethods.IsIconic(new(id))&&!member.MinimizeOutstanding)
                {
                    void Minimize(){if(Owns(id,member)&&member.MinimizedByUs&&member.Workspace!=ActiveWorkspace(member.Monitor)){member.VisibilitySubmittedAt=Stopwatch.GetTimestamp();member.MinimizeOutstanding=NativeMethods.ShowWindowAsync(new(id),6);}}
                    if(VisibilityTransition is { } transition)transition(id,false,Minimize);else Minimize();
                }
            }
            else if(!member.UserMinimized&&(member.MinimizedByUs||member.PendingRestore&&NativeMethods.IsIconic(new(id))&&!member.MinimizeOutstanding&&Stopwatch.GetElapsedTime(member.VisibilitySubmittedAt).TotalMilliseconds>750))
            {
                bool expectEvent=NativeMethods.IsIconic(new(id))||member.MinimizeOutstanding;
                member.MinimizedByUs=false;member.RestoredExternally=false;member.PendingRestore=true;
                void RestoreWindow(){if(Owns(id,member)&&!member.MinimizedByUs&&!member.UserMinimized&&member.Workspace==ActiveWorkspace(member.Monitor)){member.VisibilitySubmittedAt=Stopwatch.GetTimestamp();if(NativeMethods.ShowWindowAsync(new(id),4)&&expectEvent)member.OwnRestoreEvents++;}}
                if(VisibilityTransition is { } transition)transition(id,true,RestoreWindow);else RestoreWindow();
            }
        }
    }
    public bool ActivateWindow(long id)
    {
        if(_disposed||!_members.TryGetValue(id,out var member)||!Owns(id,member))return false;
        bool changed=member.Workspace!=ActiveWorkspace(member.Monitor);
        bool restore=member.UserMinimized||member.UserMinimizePending;
        if(restore){member.UserMinimized=false;member.UserMinimizePending=false;member.PendingRestore=true;member.InitialPlacementPending=false;}
        if(member.Hidden&&NativeMethods.IsWindowVisible(new(id))){member.Hidden=false;changed=true;}
        if(changed)
        {
            if(BringActivatedWindows){Tree(member).Remove(id);member.Workspace=ActiveWorkspace(member.Monitor);if(!member.Floating)AddBalanced(id,member);ApplyWorkspaceVisibility(member.Monitor);}
            else SwitchWorkspace(member.Workspace,member.Monitor);
        }
        if(restore)
        {
            if(!member.Floating)AddBalanced(id,member);
            if(NativeMethods.IsIconic(new(id)))
            {
                void RestoreUserWindow(){if(Owns(id,member)&&member.Workspace==ActiveWorkspace(member.Monitor)){member.VisibilitySubmittedAt=Stopwatch.GetTimestamp();NativeMethods.ShowWindowAsync(new(id),4);}}
                if(VisibilityTransition is { } transition)transition(id,true,RestoreUserWindow);else RestoreUserWindow();
            }
            changed=true;
        }
        _focused=id;_lastFocused[(member.Monitor,member.Workspace)]=id;UpdateBorders();return changed;
    }
    public void ChangeWorkspace(int delta)=>SwitchWorkspace((ActiveWorkspace(CurrentMonitor())-1+delta+WorkspaceCount)%WorkspaceCount+1);
    public void UpdateBorders()
    {
        foreach(var (id,member) in _members)
        {
            if(!Owns(id,member))continue;
            if(BordersEnabled&&!member.Floating&&!member.Hidden&&!member.UserMinimized&&member.Workspace==ActiveWorkspace(member.Monitor))member.Border.Apply(new(id),id==_focused?ActiveBorderColor:InactiveBorderColor);
            else member.Border.Restore(new(id));
        }
    }
    public void MoveToWorkspace(int workspace)
    {
        if(!_members.TryGetValue(_focused,out var member))return;
        Tree(member).Remove(_focused);member.Workspace=Math.Clamp(workspace,1,WorkspaceCount);if(!member.Floating)AddBalanced(_focused,member);
        SwitchWorkspace(ActiveWorkspace(member.Monitor),member.Monitor);
    }
    public void ToggleFloating()
    {
        if(!_members.TryGetValue(_focused,out var m))return;
        if(m.Floating&&(NativeMethods.GetStyle(new(_focused))&NativeMethods.WS_THICKFRAME)==0)return;
        m.Floating=!m.Floating;if(m.Floating){Tree(m).Remove(_focused);if(Owns(_focused,m))RestorePlacement(new(_focused),m.Original);}
        else{m.PendingRestore=true;NativeMethods.ShowWindowAsync(new(_focused),9);AddBalanced(_focused,m);}
    }
    public void ToggleDirection(){if(_members.TryGetValue(_focused,out var m))Tree(m).ToggleDirection(_focused);}
    public void Resize(TileDirection direction){if(_members.TryGetValue(_focused,out var m))Tree(m).Resize(_focused,direction);}
    public void Navigate(TileDirection direction,bool move)
    {
        var layout=Targets();long next=TileTree.Neighbor(_focused,direction,layout);if(next==0)return;
        if(move&&_members.TryGetValue(_focused,out var m))
        {
            if(_members[next].Monitor==m.Monitor)Tree(m).Swap(_focused,next);
            else{Tree(m).Remove(_focused);m.Monitor=_members[next].Monitor;m.Workspace=ActiveWorkspace(m.Monitor);AddBalanced(_focused,m,next);}
        }
        else{_focused=next;NativeMethods.SetForegroundWindow(new(next));}
    }
    public void MoveFinished(long window)
    {
        if(!_members.TryGetValue(window,out var m)||m.Floating)return;
        var monitor=MonitorHelper.Describe(NativeMethods.MonitorFromWindow(new(window),NativeMethods.MONITOR_DEFAULTTONEAREST));
        if(monitor is not null&&monitor.DeviceName!=m.Monitor)
        {Tree(m).Remove(window);m.Monitor=monitor.DeviceName;m.Workspace=ActiveWorkspace(m.Monitor);AddBalanced(window,m);}
        else if(NativeMethods.GetWindowRect(new(window),out var rect))
        {
            var layout=Targets();var overlap=layout.Where(p=>p.Key!=window&&_members[p.Key].Monitor==m.Monitor&&rect.Left+rect.Width/2>=p.Value.X&&rect.Left+rect.Width/2<p.Value.X+p.Value.Width&&rect.Top+rect.Height/2>=p.Value.Y&&rect.Top+rect.Height/2<p.Value.Y+p.Value.Height).FirstOrDefault();
            if(overlap.Key!=0)Tree(m).Swap(window,overlap.Key);
        }
    }
    public bool Move(long id,TileRect rect,bool resize=true)
    {
        if(!_members.TryGetValue(id,out var member)||!Owns(id,member)||member.Floating||member.UserMinimized||NativeMethods.IsIconic(new(id))||IsZoomed(new(id))||(NativeMethods.GetStyle(new(id))&NativeMethods.WS_THICKFRAME)==0||member.Workspace!=ActiveWorkspace(member.Monitor))return false;
        // Keep at most a short bounded queue when the application is busy painting.
        if((!resize||member.LastRequest==rect)&&member.LastRequest is { } pending&&Stopwatch.GetElapsedTime(member.SubmittedAt).TotalMilliseconds<80&&NativeMethods.GetWindowRect(new(id),out var actual)&&
           (actual.Left!=pending.X||actual.Top!=pending.Y||actual.Width!=pending.Width||actual.Height!=pending.Height))return true;
        bool moved=NativeMethods.SetWindowPos(new(id),IntPtr.Zero,rect.X,rect.Y,Math.Max(1,rect.Width),Math.Max(1,rect.Height),NativeMethods.SWP_NOZORDER|NativeMethods.SWP_NOACTIVATE|NativeMethods.SWP_ASYNCWINDOWPOS|(resize?0:NativeMethods.SWP_NOSIZE));
        if(moved){member.Placed=true;member.LastRequest=rect;member.SubmittedAt=Stopwatch.GetTimestamp();}return moved;
    }
    private void Restore(long id,Member member)
    {
        if(!Owns(id,member))return;
        member.Border.Restore(new(id));
        if(member.Hidden)return;
        if(member.Floating){if(member.MinimizedByUs)NativeMethods.ShowWindowAsync(new(id),4);return;}
        var placement=member.Original;if(member.UserMinimized&&!member.MinimizedByUs)placement.Show=2;
        RestorePlacement(new(id),placement);
    }
    private static void RestorePlacement(IntPtr hwnd,Placement placement)
    {
        // Queue behind previously submitted async moves. A synchronous restore
        // can overtake them in a busy GUI thread and then be undone by that queue.
        placement.Flags|=4; // WPF_ASYNCWINDOWPLACEMENT
        SetWindowPlacement(hwnd,placement);
    }
    public void Dispose(){if(_disposed)return;_disposed=true;foreach(var (id,member) in _members)Restore(id,member);_members.Clear();_trees.Clear();}
}
