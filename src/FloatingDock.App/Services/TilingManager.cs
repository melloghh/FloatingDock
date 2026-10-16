using System.Diagnostics;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Tiling;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Dispatching;

namespace FloatingDock.UI.Services;

/// <summary>Coalesced WinEvent-driven tiling. The configured frame timer exists only for finite transitions.</summary>
internal sealed class TilingManager : IDisposable
{
    private readonly DockApp _app;
    private readonly DispatcherQueueTimer _settle;
    private readonly FrameScheduler _animation;
    private readonly Dictionary<int,Action> _commands=new();
    private readonly HashSet<long> _dragging=new();
    private readonly Dictionary<long,TileMotion> _moves=new();
    private readonly WindowArrivalGate _arrivals=new();
    private sealed record HeldWindow(WindowInfo Info,WindowOpacityLease Lease,double Started,bool Returning=false);
    private sealed class Presentation(WindowTransitionSurface surface,TileRect from,TileRect target,double now,int duration,bool hiding,byte opacity=255,bool gentleStart=false,TileMotionSample? momentum=null,bool dockMotion=false)
    {
        public readonly WindowTransitionSurface Surface=surface;
        public readonly TileRect From=from,Target=target;
        public readonly double Queued=now;
        public readonly double Started=now;
        public readonly int Duration=duration;
        public readonly bool Hiding=hiding;
        public readonly byte FromOpacity=opacity;
        public readonly bool GentleStart=gentleStart;
        public readonly bool DockMotion=dockMotion;
        public readonly TileMotion Motion=new(from,target,now,duration,momentum?.VelocityX??0,momentum?.VelocityY??0,interpolateSize:true,velocityWidth:momentum?.VelocityWidth??0,velocityHeight:momentum?.VelocityHeight??0);
    }
    private readonly Dictionary<long,HeldWindow> _held=new();
    private readonly Dictionary<long,Presentation> _presentations=new();
    private readonly Dictionary<long,TileRect> _normalBounds=new();
    private readonly HashSet<long> _minimized=new();
    private NativeTilingSession? _session;
    private DockSettings _settings=new();
    private bool _paused,_disposed,_primed,_bindingsRegistered;
    private double _closingUntil;
    private static double Now=>Stopwatch.GetTimestamp()*1000d/Stopwatch.Frequency;
    internal NativeTilingSession? Session=>_session;
    internal bool Animating=>_animation.IsRunning||_held.Count>0;
    internal int PresentationCountForDiagnostics=>_presentations.Count;
    internal void ReconcileForDiagnostics()=>Reconcile();
    internal IReadOnlyDictionary<long,(TileRect Bounds,byte Opacity,IntPtr Handle)> PresentationsForDiagnostics=>_presentations.ToDictionary(p=>p.Key,p=>(p.Value.Surface.Bounds,p.Value.Surface.Opacity,p.Value.Surface.Handle));
    internal bool Paused=>_paused;
    internal event Action? StateChanged;
    internal void SwitchWorkspace(int workspace,string monitor)=>Command(s=>s.SwitchWorkspace(workspace,monitor));
    internal IReadOnlySet<long>? DiagnosticWindows { get; set; }
    internal bool SkipHotkeyRegistrationForDiagnostics { get; set; }
    internal TilingManager(DockApp app)
    {
        _app=app;_settle=app.UiQueue.CreateTimer();_settle.IsRepeating=false;_settle.Interval=TimeSpan.FromMilliseconds(45);_settle.Tick+=(_,_)=>Reconcile();
        _animation=new(action=>app.UiQueue.TryEnqueue(()=>action()),Frame);
        app.Running.Changed+=OnWindows;app.Running.NativeWindowChanged+=OnNative;
        app.Msg.DisplayChanged+=OnDisplay;app.Msg.WorkAreaOrSettingsChanged+=OnDisplay;
    }
    private void OnDisplay()=>_app.UiQueue.TryEnqueue(()=> { if(_session is not null){_session.RefreshMonitors();Schedule();} });
    private void OnWindows(IReadOnlyList<WindowInfo> windows,long foreground)
    {
        foreach(var window in windows)
            if(!window.IsMinimized&&!NativeMethods.IsIconic(new(window.Hwnd))&&!_presentations.ContainsKey(window.Hwnd)&&NativeMethods.GetWindowRect(new(window.Hwnd),out var r))
            {
                _minimized.Remove(window.Hwnd);
                _normalBounds[window.Hwnd]=new(r.Left,r.Top,r.Width,r.Height);
            }
        if(_session is not null)Schedule();
    }
    private void Schedule(){if(!_settle.IsRunning&&!_disposed)_settle.Start();}
    private void OnNative(WindowEventKind kind,IntPtr hwnd)
    {
        long window=hwnd.ToInt64();
        if(kind==WindowEventKind.Destroyed){_minimized.Remove(window);_normalBounds.Remove(window);ReleasePresentation(window);if(_held.Remove(window,out var held))held.Lease.Dispose();_arrivals.Forget(window);if(_session?.Forget(window)==true){if(SurfaceMotion.Enabled&&_settings.TilingAnimationMs>0)_closingUntil=Now+100;Schedule();}return;}
        if(kind==WindowEventKind.LocationChanged&&_normalBounds.ContainsKey(window)&&!_minimized.Contains(window)&&!_presentations.ContainsKey(window)&&!NativeMethods.IsIconic(hwnd)&&NativeMethods.GetWindowRect(hwnd,out var bounds))
            _normalBounds[window]=new(bounds.Left,bounds.Top,bounds.Width,bounds.Height);
        if(kind==WindowEventKind.MinimizeStart)
        {
            if(!NativeMethods.IsIconic(hwnd)&&_session?.IsUserMinimized(window)!=true)return;
            bool managed=_session?.IsManaged(window)==true;
            if(!managed&&!_normalBounds.ContainsKey(window))return;
            // A native acknowledgement can arrive after the compositor surface
            // has already finished. One minimization lasts until actual restore,
            // not merely until its short-lived presentation is disposed.
            if(!_minimized.Add(window)){_session?.ObserveNativeMinimize(window);return;}
            if(_presentations.TryGetValue(window,out var existing)&&existing.Hiding){_session?.ObserveNativeMinimize(window);return;}
            if(managed)_session!.ObserveNativeMinimize(window);
            // Out-of-context WinEvents arrive after Windows has started its
            // minimize animation. A second thumbnail here duplicates that image.
            // Only reuse a surface that already owned an invisible source before
            // minimization; otherwise keep the native animation and shell anchor.
            if(existing is not null)TransitionVisibility(window,false,()=>{},_settings.IconAnimation?_settings.IconAnimationMs:0);
            if(managed){AnimateLayout();Schedule();}
            return;
        }
        if(kind==WindowEventKind.MinimizeEnd&&!NativeMethods.IsIconic(hwnd))
        {
            _minimized.Remove(window);
            if(_session?.IsManaged(window)==true)PrepareDockRestore(window);
            else if(_presentations.TryGetValue(window,out var hiding)&&hiding.Hiding)ReleasePresentation(window);
        }
        if(_session is null)return;
        if(kind==WindowEventKind.MinimizeEnd&&_session.IsUserMinimized(window)&&!_held.ContainsKey(window)&&WindowOpacityLease.TryAcquire(hwnd) is { } returning)
            _held[window]=new(WindowIdentityResolver.ResolveBasic(hwnd),returning,Now,true);
        if(kind is WindowEventKind.Created or WindowEventKind.Shown)HoldNewWindow(hwnd);
        if(kind==WindowEventKind.LocationChanged&&_session.IsManaged(window)&&!_dragging.Contains(window)&&(_session.NeedsRestoredPlacement(window)||NativeMethods.IsZoomed(hwnd)||(NativeMethods.GetStyle(hwnd)&NativeMethods.WS_THICKFRAME)==0||!_moves.ContainsKey(window)&&!_presentations.ContainsKey(window)&&_session.PlacementDrifted(window))){Schedule();return;}
        if(kind==WindowEventKind.MoveStart){_dragging.Add(window);_moves.Remove(window);ReleasePresentation(window);}
        else if(kind==WindowEventKind.MoveEnd){_dragging.Remove(hwnd.ToInt64());_session.MoveFinished(hwnd.ToInt64());Schedule();}
        else if(kind is WindowEventKind.Foreground or WindowEventKind.MinimizeEnd)
        {
            if(kind==WindowEventKind.MinimizeEnd)_session.ObserveNativeRestore(hwnd.ToInt64());
            var foreground=NativeMethods.GetForegroundWindow();var root=NativeMethods.GetAncestor(foreground,NativeMethods.GA_ROOTOWNER);long id=(root==IntPtr.Zero?foreground:root).ToInt64();
            if(_session.CanFollowActivation(id)&&!NativeMethods.IsIconic(new(id)))_session.ActivateWindow(id);
            Schedule();
        }
        // Ignore our own location changes. They must never schedule another layout/animation.
    }
    internal void Apply(DockSettings settings)
    {
        if(_disposed)return;
        var previous=_settings.Clone();bool hadBindings=_bindingsRegistered;
        bool bindingsChanged=_settings.TilingEnabled!=settings.TilingEnabled||!TilingShortcuts.Definitions(_settings).Select(d=>TilingShortcuts.Effective(_settings,d)).SequenceEqual(TilingShortcuts.Definitions(settings).Select(d=>TilingShortcuts.Effective(settings,d)));
        bool optionsChanged=bindingsChanged||_settings.TilingGap!=settings.TilingGap||_settings.TilingOuterGap!=settings.TilingOuterGap||_settings.TilingExcludedApps!=settings.TilingExcludedApps;
        _settings=settings.Clone();
        _animation.SetFrameRate(settings.TilingEnabled&&!_paused?settings.TilingAnimationFps:settings.AnimationFps);
        if(!settings.TilingEnabled){StopSession();Unregister();_paused=false;StateChanged?.Invoke();return;}
        if(bindingsChanged||!_bindingsRegistered)
        {
            Unregister();
            if(TilingShortcuts.Error(settings) is not null||!Register())
            {
                Unregister();_settings=previous;
                if(hadBindings&&Register())
                {
                    _app.Settings.Update(s=>{s.TilingKeybindings=new(previous.TilingKeybindings);s.TilingModifier=previous.TilingModifier;s.TilingWorkspaces=previous.TilingWorkspaces;});
                    _app.Notify(L.Text("Tiling manager"),L.Text("Shortcut unavailable. Previous tiling bindings were kept."),true);
                }
                else
                {
                    StopSession();Unregister();_app.Settings.Update(s=>s.TilingEnabled=false);
                    _app.Notify(L.Text("Tiling manager"),L.Text("A tiling shortcut is already in use. Choose another modifier in Settings."),true);
                }
                _app.UiQueue.TryEnqueue(()=>_app.SettingsWin?.RefreshTilingBindingsAfterRejection());
                StateChanged?.Invoke();return;
            }
        }
        if(!_paused){if(_session is null){_session=new NativeTilingSession { DiagnosticWindows=DiagnosticWindows };_primed=false;}Configure();if(optionsChanged)Schedule();}
        StateChanged?.Invoke();
    }
    private void Configure()
    {
        if(_session is null)return;_session.Gap=_settings.TilingGap;_session.OuterGap=_settings.TilingOuterGap;_session.WorkspaceCount=_settings.TilingWorkspaces;_session.SetExcluded(_settings.TilingExcludedApps);
        _session.VisibilityTransition=(id,visible,native)=>TransitionVisibility(id,visible,native);
        _session.BringActivatedWindows=_settings.TilingBringActivatedWindows;_session.BordersEnabled=_settings.TilingBorders;
        _session.TileMaximizedApps=_settings.TilingTileMaximizedApps;
        _session.ActiveBorderColor=WindowBorderAccent.Color(_settings.TilingActiveBorderColor);_session.InactiveBorderColor=WindowBorderAccent.Color(_settings.TilingInactiveBorderColor);_session.UpdateBorders();
    }
    private bool Register()
    {
        int id=100;
        foreach(var binding in TilingShortcuts.Definitions(_settings))
        {
            int keyId=id++;string keys=TilingShortcuts.Effective(_settings,binding);if(string.IsNullOrWhiteSpace(keys))continue;
            if(!FloatingDock.Core.Services.HotkeyParser.TryParse(keys,out var mods,out var key)||(!SkipHotkeyRegistrationForDiagnostics||DiagnosticWindows is null)&&!_app.Msg.RegisterHotkey(keyId,mods,key)){_app.Log.Warn("Tiling shortcut unavailable: "+keys);return false;}
            _commands[keyId]=binding.Kind==TilingActionKind.Pause?()=>
            {
                _paused=!_paused;if(_paused)StopSession();else{_session=new(){DiagnosticWindows=DiagnosticWindows};_primed=false;Configure();Schedule();}StateChanged?.Invoke();
            }:()=>Command(s=>
            {
                switch(binding.Kind)
                {
                    case TilingActionKind.Focus:s.Navigate(binding.Direction,false);break;
                    case TilingActionKind.Move:s.Navigate(binding.Direction,true);break;
                    case TilingActionKind.Resize:s.Resize(binding.Direction);break;
                    case TilingActionKind.Workspace:s.SwitchWorkspace(binding.Workspace);break;
                    case TilingActionKind.Send:s.MoveToWorkspace(binding.Workspace);break;
                    case TilingActionKind.Float:s.ToggleFloating();break;
                    case TilingActionKind.Split:s.ToggleDirection();break;
                    case TilingActionKind.PreviousWorkspace:s.ChangeWorkspace(-1);break;
                    case TilingActionKind.NextWorkspace:s.ChangeWorkspace(1);break;
                }
            });
        }
        _bindingsRegistered=true;return true;
    }
    private void Unregister(){foreach(var id in _commands.Keys)_app.Msg.UnregisterHotkey(id);_commands.Clear();_bindingsRegistered=false;}
    internal bool HandleHotkey(int id){if(!_commands.TryGetValue(id,out var command))return false;command();return true;}
    private void Command(Action<NativeTilingSession> action){if(_session is null)return;Synchronize();action(_session);AnimateLayout();StateChanged?.Invoke();}
    private void Reconcile()
    {
        if(_session is null||_disposed)return;
        // Let DWM finish the application's native close fade before adjacent
        // tiles expand over it. No source HWND survives destruction.
        if(Now<_closingUntil){Schedule();return;}
        bool changed=Synchronize();
        if(changed||!_animation.IsRunning)AnimateLayout();
        StateChanged?.Invoke();
    }
    private bool Synchronize()
    {
        if(_session is null)return false;
        foreach(var (id,held) in _held.ToArray())
            if(!NativeMethods.IsWindow(new(id))||Now-held.Started>1800){held.Lease.Dispose();_held.Remove(id);}
        var tracked=_app.Running.Snapshot();
        var snapshot=tracked.Concat(_held.Values.Where(h=>!tracked.Any(w=>w.Hwnd==h.Info.Hwnd)&&NativeMethods.IsWindow(new(h.Info.Hwnd))).Select(h=>h.Info)).ToArray();
        // Hold new, eligible windows before the previous startup wait. They stay
        // in the shell, but their saved placement is never used as the opening visual.
        if(_primed)foreach(var window in snapshot)if(_session.CanManageNew(window))HoldNewWindow(new(window.Hwnd));
        _arrivals.BeginPass(snapshot.Select(w=>w.Hwnd));
        var ready=snapshot.Where(w=>_session.IsManaged(w.Hwnd)||!_session.CanManageNew(w)||!_primed||!NativeMethods.GetWindowRect(new(w.Hwnd),out var r)||_arrivals.Ready(w.Hwnd,new(r.Left,r.Top,r.Width,r.Height),Now,_settings.TilingStartupDelayMs)).ToArray();
        _primed=true;bool changed=_session.Reconcile(ready,_app.Running.ForegroundRoot);
        foreach(var window in snapshot)
            if(_session.NeedsInitialPlacement(window.Hwnd)&&!NativeMethods.IsIconic(new(window.Hwnd))&&!NativeMethods.IsZoomed(new(window.Hwnd))&&NativeMethods.GetWindowRect(new(window.Hwnd),out var rect)&&_arrivals.Ready(window.Hwnd,new(rect.Left,rect.Top,rect.Width,rect.Height),Now,_settings.TilingStartupDelayMs))
            {_session.FinishInitialPlacement(window.Hwnd);changed=true;}
        if(_arrivals.Waiting||_held.Count>0)Schedule();
        var releasable=_held.Where(p=>!p.Value.Returning&&_session.IsManaged(p.Key)&&!_session.NeedsInitialPlacement(p.Key)).Select(p=>p.Key).ToArray();
        if(releasable.Length>0)
        {
            var targets=_session.Targets();
            foreach(var id in releasable)if(!targets.ContainsKey(id)){_held[id].Lease.Dispose();_held.Remove(id);}
        }
        return changed;
    }
    private void HoldNewWindow(IntPtr hwnd)
    {
        long id=hwnd.ToInt64();if(_session is null||_session.IsManaged(id)||_held.ContainsKey(id)||!WindowFilter.IsCandidate(hwnd,DiagnosticWindows?.Contains(id)==true?0:(uint)Environment.ProcessId))return;
        var info=WindowIdentityResolver.ResolveBasic(hwnd);
        if(WindowFilter.IsIgnoredExe(info.ExePath)||info.ExePath is null||!_session.CanManageNew(info))return;
        var lease=WindowOpacityLease.TryAcquire(hwnd);if(lease is null)return;
        _held[id]=new(info,lease,Now);Schedule();
    }
    private TileRect DockOrigin(long id,TileRect fallback)
    {
        if(_app.Dock.CurrentAnimationRectFor(new(id)) is { } r)return new(r.Left,r.Top,Math.Max(1,r.Width),Math.Max(1,r.Height));
        var mon=FloatingDock.Windows.Display.MonitorHelper.Describe(NativeMethods.MonitorFromWindow(new(id),NativeMethods.MONITOR_DEFAULTTONEAREST));
        return mon is null?fallback with{Width=32,Height=32}:new(mon.Bounds.Left+mon.Bounds.Width/2-16,mon.Bounds.Bottom-40,32,32);
    }
    private void ReleasePresentation(long id)
    {if(_presentations.Remove(id,out var p))p.Surface.Dispose();}
    private int DockDuration(bool restoring)=>Views.SurfaceMotion.Enabled?
        Math.Clamp((int)(Math.Max(_settings.TilingAnimationMs,_settings.IconAnimationMs)*(restoring?1.4:1.25)),180,700):0;
    private void PrepareDockRestore(long id)
    {
        if(_session is null||!_session.IsManaged(id))return;
        if(_presentations.TryGetValue(id,out var existing)&&!existing.Hiding)return;
        if(!_session.IsUserMinimized(id)&&!_minimized.Contains(id)&&existing is null)return;
        if(existing is null&&!_held.ContainsKey(id)&&WindowOpacityLease.TryAcquire(new(id)) is { } lease)
            _held[id]=new(WindowIdentityResolver.ResolveBasic(new(id)),lease,Now,true);
        _minimized.Remove(id);
        if(existing is {Hiding:true})TransitionVisibility(id,true,()=>{},DockDuration(true));
        Schedule();
    }
    private void TransitionVisibility(long id,bool visible,Action nativeVisibility,int? animationMs=null)
    {
        _moves.Remove(id);
        if(!NativeMethods.GetWindowRect(new(id),out var rect)){ReleasePresentation(id);nativeVisibility();return;}
        var from=new TileRect(rect.Left,rect.Top,rect.Width,rect.Height);
        if(NativeMethods.IsIconic(new(id)))from=_session?.LastPlacement(id)??_normalBounds.GetValueOrDefault(id,from);
        else _normalBounds[id]=from;
        int duration=Views.SurfaceMotion.Enabled?animationMs??_settings.TilingAnimationMs:0;
        if(_session is null)_animation.SetFrameRate(_settings.AnimationFps);
        if(!visible&&duration>0&&_session is not null)duration=DockDuration(false);
        if(visible)
        {
            // Keep the source invisible through SW_SHOWNA and final placement.
            if(_presentations.TryGetValue(id,out var reversing))
            {
                var target=_session!.Targets().GetValueOrDefault(id,reversing.From);
                _presentations[id]=new(reversing.Surface,reversing.Surface.Bounds,target,Now,DockDuration(true),false,reversing.Surface.Opacity,dockMotion:true);
                nativeVisibility();_session.Move(id,target);Schedule();return;
            }
            // Dock preparation already hid this source. Reuse that lease:
            // removing/readding WS_EX_LAYERED restarts browser composition.
            if(_held.TryGetValue(id,out var held))_held[id]=held with{Returning=true};
            else if(WindowOpacityLease.TryAcquire(new(id)) is { } lease)_held[id]=new(WindowIdentityResolver.ResolveBasic(new(id)),lease,Now,true);
            nativeVisibility();Schedule();return;
        }
        _presentations.Remove(id,out var previous);
        if(_held.Remove(id,out var pending))pending.Lease.Dispose();
        if(previous is not null)from=previous.Surface.Bounds;
        var to=DockOrigin(id,from);
        var surface=duration>0?previous?.Surface??WindowTransitionSurface.TryCreate(new(id),from,to):null;
        if(duration==0)previous?.Surface.Dispose();
        // Minimize immediately under the representation. This keeps workspace
        // state synchronous and prevents late native animations to the taskbar.
        if(surface is not null)_presentations[id]=new(surface,from,to,Now,duration,true,surface.Opacity,gentleStart:_session is null,dockMotion:_session is not null);
        nativeVisibility();if(surface is not null&&!_animation.IsRunning)_animation.Start();
    }
    internal void PrepareActivation(IReadOnlyList<WindowInfo> windows)
    {if(_session is null)return;bool changed=false;foreach(var window in windows)if(_session.IsManaged(window.Hwnd)){if(NativeMethods.IsIconic(new(window.Hwnd)))PrepareDockRestore(window.Hwnd);changed|=_session.ActivateWindow(window.Hwnd);}if(changed){AnimateLayout();StateChanged?.Invoke();}}
    internal bool MinimizeToDock(IntPtr hwnd)
    {
        long id=hwnd.ToInt64();
        if(NativeMethods.IsIconic(hwnd))return false;
        if(!_minimized.Add(id))return true;
        if(_session?.IsManaged(id)==true)_session.ObserveNativeMinimize(id,userRequest:true);
        TransitionVisibility(id,false,()=>NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_MINIMIZE),_settings.IconAnimation?_settings.IconAnimationMs:0);
        if(_session is not null){AnimateLayout();Schedule();}
        return true;
    }
    internal void HidePresentationsForCapture()
    {foreach(var id in _presentations.Keys.ToArray())ReleasePresentation(id);_moves.Clear();_animation.Stop();}
    internal void AnimateLayout()
    {
        if(_session is null)return;
        var targets=_session.Targets();
        foreach(var id in _presentations.Keys.ToArray())
            if(!_presentations[id].Hiding&&(!targets.ContainsKey(id)||_dragging.Contains(id)))ReleasePresentation(id);
        foreach(var id in _moves.Keys.ToArray())
            if(!targets.ContainsKey(id)||_dragging.Contains(id))_moves.Remove(id);
        int duration=Views.SurfaceMotion.Enabled?_settings.TilingAnimationMs:0;
        if(duration>0)duration=Math.Max(duration,220);
        if(duration>0&&(_presentations.Values.Any(p=>p.DockMotion)||_held.Values.Any(h=>h.Returning)))
            duration=Math.Max(duration,DockDuration(true));
        foreach(var (id,target) in targets)
        {
            // Metadata/focus bursts must not restart an unchanged transition.
            _moves.TryGetValue(id,out var pending);
            _presentations.TryGetValue(id,out var showing);
            if(showing is {Hiding:false}&&showing.Target==target)continue;
            if(duration>0&&pending?.Target==target)continue;
            if(_dragging.Contains(id)||!NativeMethods.GetWindowRect(new(id),out var current))continue;
            var from=new TileRect(current.Left,current.Top,current.Width,current.Height);
            if(showing is not null)from=showing.Surface.Bounds;
            TileMotionSample? sample=showing is {Hiding:false,DockMotion:false}?showing.Motion.Sample(Now):pending?.Sample(Now);
            // Retarget from the last presented bounds. Sampling ahead here can
            // visibly jump when the UI thread is between two animation frames.
            if(showing is null&&pending is not null&&sample is { } ongoing)from=new(ongoing.Bounds.X,ongoing.Bounds.Y,from.Width,from.Height);
            _moves.Remove(id);
            bool opening=_held.TryGetValue(id,out var held)||!_session.HasBeenPlaced(id);
            bool dockReveal=held?.Returning==true||showing is {Hiding:false,DockMotion:true};
            if(from==target&&!opening)continue;
            if(duration==0)
            {_session.Move(id,target);ReleasePresentation(id);if(_held.Remove(id,out var immediate))immediate.Lease.Dispose();continue;}
            WindowTransitionSurface? surface=showing?.Surface;
            byte opacity=showing?.Surface.Opacity??(dockReveal?(byte)255:opening?(byte)0:(byte)255);
            if(showing is not null)_presentations.Remove(id);
            if(opening)from=held?.Returning==true?TileMinimizeMotion.FitAtDock(target,DockOrigin(id,target)):new(target.X+target.Width/100,target.Y+target.Height/100,Math.Max(1,target.Width*98/100),Math.Max(1,target.Height*98/100));
            surface??=WindowTransitionSurface.TryCreate(new(id),from,target,held?.Lease);
            if(surface is not null)
            {
                _held.Remove(id);surface.Update(from,opacity);
                _session.Move(id,target);
                _presentations[id]=new(surface,from,target,Now,dockReveal?DockDuration(true):duration,false,opacity,momentum:sample,dockMotion:dockReveal||showing?.DockMotion==true);continue;
            }
            if(_held.Remove(id,out var failed))failed.Lease.Dispose();
            // Unsupported layered-window owners keep the native motion fallback.
            if(opening){_session.Move(id,target);continue;}
            // Set the final client size once. Resizing a newly starting application
            // at 60 Hz repeatedly rebuilds its layout/swap chain and stalls painting.
            if(from.Width!=target.Width||from.Height!=target.Height)
            {
                from=new(from.X,from.Y,target.Width,target.Height);
                if(!_session.Move(id,from))continue;
            }
            if(from.X==target.X&&from.Y==target.Y)continue;
            _moves[id]=new(from,target,Now,duration,sample?.VelocityX??0,sample?.VelocityY??0);
        }
        _session.UpdateBorders();
        if(_moves.Count==0&&_presentations.Count==0)_animation.Stop();else if(!_animation.IsRunning)_animation.Start();
    }
    private void Frame()
    {
        foreach(var (id,p) in _presentations.ToArray())
        {
            if(!NativeMethods.IsWindow(new(id))){ReleasePresentation(id);continue;}
            if(!p.Hiding&&_session is null){ReleasePresentation(id);continue;}
            double now=Now;
            bool atTarget=!p.Hiding&&NativeMethods.GetWindowRect(new(id),out var actual)&&!NativeMethods.IsIconic(new(id))&&Math.Abs(actual.Left-p.Target.X)<=2&&Math.Abs(actual.Top-p.Target.Y)<=2&&Math.Abs(actual.Width-p.Target.Width)<=2&&Math.Abs(actual.Height-p.Target.Height)<=2;
            if(!p.Hiding&&!atTarget)
            {
                if(now-p.Queued>1000){ReleasePresentation(id);_session!.ReleaseFailedPlacement(id);Schedule();continue;}
                _session!.Move(id,p.Target);
            }
            double t=Math.Clamp((now-p.Started)/Math.Max(1,p.Duration),0,1),ease=t*t*t*(10+t*(-15+6*t));
            var bounds=p.DockMotion?TileDockMotion.Sample(p.From,p.Hiding?TileMinimizeMotion.FitAtDock(p.From,p.Target):p.Target,t,!p.Hiding)
                :p.Hiding?TileMinimizeMotion.Sample(p.From,p.Target,t,p.GentleStart):p.Motion.Sample(now).Bounds;
            double fade=Math.Clamp((t-.82)/.18,0,1);fade=fade*fade*(3-2*fade);
            double reveal=p.DockMotion?Math.Min(1,t*5):ease;
            byte alpha=(byte)Math.Clamp(p.Hiding?Math.Round(p.FromOpacity*(1-fade)):Math.Round(p.FromOpacity+(255-p.FromOpacity)*reveal),0,255);
            if(!p.Surface.Update(bounds,alpha)||t>=1&&(p.Hiding||atTarget))ReleasePresentation(id);
        }
        foreach(var (id,move) in _moves.ToArray())
        {
            double now=Now;bool complete=move.Complete(now);
            // A startup routine may apply its saved size after our first placement.
            // Reassert the final size only at completion, never on intermediate frames.
            bool moved=_session?.Move(id,move.Sample(now).Bounds,resize:complete)==true;
            if(!moved||complete)_moves.Remove(id);
        }
        if(_moves.Count==0&&_presentations.Count==0){_animation.Stop();WindowTransitionSurface.TrimIdleViewports();}
    }
    private void StopSession(){_settle.Stop();_animation.Stop();_moves.Clear();foreach(var id in _presentations.Keys.ToArray())ReleasePresentation(id);WindowTransitionSurface.TrimIdleViewports(0);foreach(var held in _held.Values)held.Lease.Dispose();_held.Clear();_dragging.Clear();_arrivals.Clear();_session?.Dispose();_session=null;_primed=false;}
    internal void EmergencyRestore()=>StopSession();
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;StopSession();_animation.Dispose();Unregister();_app.Running.Changed-=OnWindows;_app.Running.NativeWindowChanged-=OnNative;_app.Msg.DisplayChanged-=OnDisplay;_app.Msg.WorkAreaOrSettingsChanged-=OnDisplay;
    }
}
