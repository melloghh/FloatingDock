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
    private readonly DispatcherQueueTimer _settle,_animation;
    private readonly Dictionary<int,Action> _commands=new();
    private readonly HashSet<long> _dragging=new();
    private readonly Dictionary<long,TileMotion> _moves=new();
    private readonly WindowArrivalGate _arrivals=new();
    private NativeTilingSession? _session;
    private DockSettings _settings=new();
    private bool _paused,_disposed,_primed,_bindingsRegistered;
    private static double Now=>Stopwatch.GetTimestamp()*1000d/Stopwatch.Frequency;
    internal NativeTilingSession? Session=>_session;
    internal bool Animating=>_animation.IsRunning;
    internal bool Paused=>_paused;
    internal event Action? StateChanged;
    internal void SwitchWorkspace(int workspace,string monitor)=>Command(s=>s.SwitchWorkspace(workspace,monitor));
    internal IReadOnlySet<long>? DiagnosticWindows { get; set; }
    internal bool SkipHotkeyRegistrationForDiagnostics { get; set; }
    internal TilingManager(DockApp app)
    {
        _app=app;_settle=app.UiQueue.CreateTimer();_settle.IsRepeating=false;_settle.Interval=TimeSpan.FromMilliseconds(45);_settle.Tick+=(_,_)=>Reconcile();
        _animation=app.UiQueue.CreateTimer();_animation.Interval=TimeSpan.FromMilliseconds(1000d/60);_animation.Tick+=(_,_)=>Frame();
        app.Running.Changed+=OnWindows;app.Running.NativeWindowChanged+=OnNative;
        app.Msg.DisplayChanged+=OnDisplay;app.Msg.WorkAreaOrSettingsChanged+=OnDisplay;
    }
    private void OnDisplay()=>_app.UiQueue.TryEnqueue(()=> { if(_session is not null){_session.RefreshMonitors();Schedule();} });
    private void OnWindows(IReadOnlyList<WindowInfo> windows,long foreground){if(_session is not null)Schedule();}
    private void Schedule(){if(!_settle.IsRunning&&!_disposed)_settle.Start();}
    private void OnNative(WindowEventKind kind,IntPtr hwnd)
    {
        if(_session is null)return;
        if(kind==WindowEventKind.Destroyed){_arrivals.Forget(hwnd.ToInt64());if(_session.Forget(hwnd.ToInt64()))Schedule();return;}
        if(kind==WindowEventKind.MinimizeStart)_session.ObserveNativeMinimize(hwnd.ToInt64());
        if(kind==WindowEventKind.LocationChanged&&_session.IsManaged(hwnd.ToInt64())&&(_session.NeedsRestoredPlacement(hwnd.ToInt64())||NativeMethods.IsZoomed(hwnd)||(NativeMethods.GetStyle(hwnd)&NativeMethods.WS_THICKFRAME)==0)){Schedule();return;}
        if(kind==WindowEventKind.MoveStart){_dragging.Add(hwnd.ToInt64());_moves.Remove(hwnd.ToInt64());}
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
        _animation.Interval=TimeSpan.FromMilliseconds(1000d/settings.TilingAnimationFps);
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
        bool changed=Synchronize();
        if(changed||!_animation.IsRunning)AnimateLayout();
        StateChanged?.Invoke();
    }
    private bool Synchronize()
    {
        if(_session is null)return false;
        var snapshot=_app.Running.Snapshot();_arrivals.BeginPass(snapshot.Select(w=>w.Hwnd));
        var ready=snapshot.Where(w=>_session.IsManaged(w.Hwnd)||!_session.CanManageNew(w)||!_primed||!NativeMethods.GetWindowRect(new(w.Hwnd),out var r)||_arrivals.Ready(w.Hwnd,new(r.Left,r.Top,r.Width,r.Height),Now,_settings.TilingStartupDelayMs)).ToArray();
        _primed=true;bool changed=_session.Reconcile(ready,_app.Running.ForegroundRoot);
        foreach(var window in snapshot)
            if(_session.NeedsInitialPlacement(window.Hwnd)&&!NativeMethods.IsIconic(new(window.Hwnd))&&!NativeMethods.IsZoomed(new(window.Hwnd))&&NativeMethods.GetWindowRect(new(window.Hwnd),out var rect)&&_arrivals.Ready(window.Hwnd,new(rect.Left,rect.Top,rect.Width,rect.Height),Now,_settings.TilingStartupDelayMs))
            {_session.FinishInitialPlacement(window.Hwnd);changed=true;}
        if(_arrivals.Waiting)Schedule();
        return changed;
    }
    internal void PrepareActivation(IReadOnlyList<WindowInfo> windows)
    {if(_session is null)return;bool changed=false;foreach(var window in windows)if(_session.IsManaged(window.Hwnd))changed|=_session.ActivateWindow(window.Hwnd);if(changed){AnimateLayout();StateChanged?.Invoke();}}
    internal void AnimateLayout()
    {
        if(_session is null)return;
        var targets=_session.Targets();
        foreach(var id in _moves.Keys.ToArray())
            if(!targets.ContainsKey(id)||_dragging.Contains(id))_moves.Remove(id);
        int duration=Views.SurfaceMotion.Enabled?_settings.TilingAnimationMs:0;
        foreach(var (id,target) in targets)
        {
            // Metadata/focus bursts must not restart an unchanged transition.
            _moves.TryGetValue(id,out var pending);
            if(duration>0&&pending?.Target==target)continue;
            if(_dragging.Contains(id)||!NativeMethods.GetWindowRect(new(id),out var current))continue;
            var from=new TileRect(current.Left,current.Top,current.Width,current.Height);
            TileMotionSample? sample=pending?.Sample(Now);if(sample is { } ongoing)from=new(ongoing.Bounds.X,ongoing.Bounds.Y,from.Width,from.Height);
            _moves.Remove(id);
            if(from==target)continue;
            if(duration==0){_session.Move(id,target);continue;}
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
        if(_moves.Count==0)_animation.Stop();else if(!_animation.IsRunning)_animation.Start();
    }
    private void Frame()
    {
        if(_session is null){_animation.Stop();return;}
        foreach(var (id,move) in _moves.ToArray())
        {
            double now=Now;bool complete=move.Complete(now);
            // A startup routine may apply its saved size after our first placement.
            // Reassert the final size only at completion, never on intermediate frames.
            bool moved=_session.Move(id,move.Sample(now).Bounds,resize:complete);
            if(!moved||complete)_moves.Remove(id);
        }
        if(_moves.Count==0)_animation.Stop();
    }
    private void StopSession(){_settle.Stop();_animation.Stop();_moves.Clear();_dragging.Clear();_arrivals.Clear();_session?.Dispose();_session=null;_primed=false;}
    internal void EmergencyRestore()=>_session?.Dispose();
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;StopSession();Unregister();_app.Running.Changed-=OnWindows;_app.Running.NativeWindowChanged-=OnNative;_app.Msg.DisplayChanged-=OnDisplay;_app.Msg.WorkAreaOrSettingsChanged-=OnDisplay;
    }
}
