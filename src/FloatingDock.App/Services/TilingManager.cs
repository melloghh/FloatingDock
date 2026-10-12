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

/// <summary>Coalesced WinEvent-driven tiling. A 60 Hz timer exists only for finite transitions.</summary>
internal sealed class TilingManager : IDisposable
{
    private readonly DockApp _app;
    private readonly DispatcherQueueTimer _settle,_animation;
    private readonly Dictionary<int,Action> _commands=new();
    private readonly HashSet<long> _dragging=new();
    private readonly Dictionary<long,(TileRect From,TileRect To)> _moves=new();
    private NativeTilingSession? _session;
    private DockSettings _settings=new();
    private long _animationStart;
    private int _duration;
    private bool _paused,_disposed;
    internal NativeTilingSession? Session=>_session;
    internal bool Animating=>_animation.IsRunning;
    internal bool Paused=>_paused;
    internal event Action? StateChanged;
    internal void SwitchWorkspace(int workspace,string monitor)=>Command(s=>s.SwitchWorkspace(workspace,monitor));
    internal IReadOnlySet<long>? DiagnosticWindows { get; set; }
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
        if(kind==WindowEventKind.Destroyed){if(_session.Forget(hwnd.ToInt64()))Schedule();return;}
        if(kind==WindowEventKind.LocationChanged&&_session.ManagedWindows.Contains(hwnd.ToInt64())&&(NativeMethods.IsZoomed(hwnd)||(NativeMethods.GetStyle(hwnd)&NativeMethods.WS_THICKFRAME)==0)){Schedule();return;}
        if(kind==WindowEventKind.MoveStart){_dragging.Add(hwnd.ToInt64());_moves.Remove(hwnd.ToInt64());}
        else if(kind==WindowEventKind.MoveEnd){_dragging.Remove(hwnd.ToInt64());_session.MoveFinished(hwnd.ToInt64());Schedule();}
        else if(kind==WindowEventKind.Foreground)Schedule();
        // Ignore our own location changes. They must never schedule another layout/animation.
    }
    internal void Apply(DockSettings settings)
    {
        if(_disposed)return;
        bool bindingsChanged=_settings.TilingEnabled!=settings.TilingEnabled||_settings.TilingWorkspaces!=settings.TilingWorkspaces||_settings.TilingModifier!=settings.TilingModifier;
        bool optionsChanged=bindingsChanged||_settings.TilingGap!=settings.TilingGap||_settings.TilingOuterGap!=settings.TilingOuterGap||_settings.TilingExcludedApps!=settings.TilingExcludedApps;
        _settings=settings.Clone();
        if(!settings.TilingEnabled){StopSession();Unregister();_paused=false;StateChanged?.Invoke();return;}
        if(bindingsChanged||_commands.Count==0)
        {
            Unregister();
            if(!Register())
            {
                StopSession();Unregister();_app.Settings.Update(s=>s.TilingEnabled=false);
                _app.Notify(L.Text("Tiling manager"),L.Text("A tiling shortcut is already in use. Choose another modifier in Settings."),true);return;
            }
        }
        if(!_paused){_session??=new NativeTilingSession { DiagnosticWindows=DiagnosticWindows };Configure();if(optionsChanged)Schedule();}
        StateChanged?.Invoke();
    }
    private void Configure()
    {
        if(_session is null)return;_session.Gap=_settings.TilingGap;_session.OuterGap=_settings.TilingOuterGap;_session.WorkspaceCount=_settings.TilingWorkspaces;_session.SetExcluded(_settings.TilingExcludedApps);
    }
    private bool Register()
    {
        int id=100;string modifier=_settings.TilingModifier==TilingModifier.Alt?"Alt":"Ctrl+Alt+Win";
        bool Add(string keys,Action action)
        {
            if(!FloatingDock.Core.Services.HotkeyParser.TryParse(keys,out var mods,out var key)||!_app.Msg.RegisterHotkey(id,mods,key)){_app.Log.Warn("Tiling shortcut unavailable: "+keys);return false;}
            _commands[id++]=action;return true;
        }
        foreach(var (key,direction) in new[]{("H",TileDirection.Left),("J",TileDirection.Down),("K",TileDirection.Up),("L",TileDirection.Right)})
        {
            if(!Add(modifier+"+"+key,()=>Command(s=>s.Navigate(direction,false))))return false;
            if(!Add(modifier+"+Shift+"+key,()=>Command(s=>s.Navigate(direction,true))))return false;
            string resize="Ctrl+Alt+Shift";
            if(!Add(resize+"+"+key,()=>Command(s=>s.Resize(direction))))return false;
        }
        for(int workspace=1;workspace<=_settings.TilingWorkspaces;workspace++)
        {
            int target=workspace;
            if(!Add(modifier+"+"+workspace,()=>Command(s=>s.SwitchWorkspace(target))))return false;
            if(!Add(modifier+"+Shift+"+workspace,()=>Command(s=>s.MoveToWorkspace(target))))return false;
        }
        return Add(modifier+"+F",()=>Command(s=>s.ToggleFloating()))&&Add(modifier+"+V",()=>Command(s=>s.ToggleDirection()))&&Add(modifier+"+P",()=>
        {
            _paused=!_paused;if(_paused)StopSession();else{_session=new(){DiagnosticWindows=DiagnosticWindows};Configure();Schedule();}
            StateChanged?.Invoke();
        });
    }
    private void Unregister(){foreach(var id in _commands.Keys)_app.Msg.UnregisterHotkey(id);_commands.Clear();}
    internal bool HandleHotkey(int id){if(!_commands.TryGetValue(id,out var command))return false;command();return true;}
    private void Command(Action<NativeTilingSession> action){if(_session is null)return;Reconcile();action(_session);AnimateLayout();StateChanged?.Invoke();}
    private void Reconcile()
    {
        if(_session is null||_disposed)return;
        bool changed=_session.Reconcile(_app.Running.Snapshot(),_app.Running.ForegroundRoot);
        if(changed||!_animation.IsRunning)AnimateLayout();
        StateChanged?.Invoke();
    }
    internal void AnimateLayout()
    {
        if(_session is null)return;
        _animation.Stop();_moves.Clear();
        foreach(var (id,target) in _session.Targets())
        {
            if(_dragging.Contains(id)||!NativeMethods.GetWindowRect(new(id),out var current))continue;
            var from=new TileRect(current.Left,current.Top,current.Width,current.Height);
            if(Math.Abs(from.X-target.X)+Math.Abs(from.Y-target.Y)+Math.Abs(from.Width-target.Width)+Math.Abs(from.Height-target.Height)<=2)continue;
            _moves[id]=(from,target);
        }
        _duration=Views.SurfaceMotion.Enabled?_settings.TilingAnimationMs:0;_animationStart=Stopwatch.GetTimestamp();
        if(_moves.Count==0)return;
        if(_duration==0)Frame();else _animation.Start();
    }
    private void Frame()
    {
        if(_session is null){_animation.Stop();return;}
        double progress=_duration==0?1:Math.Clamp(Stopwatch.GetElapsedTime(_animationStart).TotalMilliseconds/_duration,0,1);
        double ease=1-Math.Pow(1-progress,3);
        foreach(var (id,move) in _moves)
        {
            int Interpolate(int start,int end)=>(int)Math.Round(start+(end-start)*ease);
            _session.Move(id,new(Interpolate(move.From.X,move.To.X),Interpolate(move.From.Y,move.To.Y),Interpolate(move.From.Width,move.To.Width),Interpolate(move.From.Height,move.To.Height)));
        }
        if(progress>=1){_animation.Stop();_moves.Clear();}
    }
    private void StopSession(){_settle.Stop();_animation.Stop();_moves.Clear();_dragging.Clear();_session?.Dispose();_session=null;}
    internal void EmergencyRestore()=>_session?.Dispose();
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;StopSession();Unregister();_app.Running.Changed-=OnWindows;_app.Running.NativeWindowChanged-=OnNative;_app.Msg.DisplayChanged-=OnDisplay;_app.Msg.WorkAreaOrSettingsChanged-=OnDisplay;
    }
}
