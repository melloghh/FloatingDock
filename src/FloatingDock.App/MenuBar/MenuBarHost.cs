using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.Taskbar;
using FloatingDock.Windows.WindowManagement;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Shell;
using FloatingDock.UI.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FloatingDock.UI.MenuBar;

internal sealed class MenuBarHost : IDisposable
{
    private readonly DockApp _app;
    private readonly Dictionary<string, MenuBarWindow> _bars = new();
    private readonly AppBarReservation _reservation = new();
    private readonly DispatcherQueueTimer _geometryTimer;
    private bool _disposed;
    internal MenuBarWindow? Primary => _bars.Values.FirstOrDefault();
    internal IReadOnlyCollection<MenuBarWindow> Windows => _bars.Values;
    internal void ReassertMenus() { foreach(var bar in _bars.Values)bar.Menus.Reassert(); }
    internal MenuBarHost(DockApp app)
    {
        _app=app; _reservation.Log=app.Log.Warn;
        _geometryTimer=app.UiQueue.CreateTimer();_geometryTimer.IsRepeating=false;
        _geometryTimer.Interval=TimeSpan.FromMilliseconds(50);_geometryTimer.Tick+=(_,_)=>RefreshFullscreen();
        _reservation.PositionChanged+=()=>app.UiQueue.TryEnqueue(()=> { if(!_disposed) ApplySettings(); });
        _app.Running.Changed+=OnRunning;
        _app.Running.NativeWindowChanged+=OnNativeWindowChanged;
        _app.Msg.TaskbarCreated+=OnExplorerRestart;
        _app.Msg.InputLanguageChanged+=OnLanguageChanged;
        _app.Tiling.StateChanged+=OnTilingChanged;
        ApplySettings();
    }
    private void OnLanguageChanged()=>_app.UiQueue.TryEnqueue(()=> { foreach(var bar in _bars.Values)bar.RefreshLanguage(); });
    private void OnTilingChanged(){foreach(var bar in _bars.Values)bar.RefreshWorkspaces();}
    private void OnExplorerRestart() { _reservation.Release(); ApplySettings(); }
    private void OnNativeWindowChanged(FloatingDock.Windows.RunningApps.WindowEventKind kind,IntPtr hwnd)
    {
        if(_disposed||Program.IsSmokeTest||!_app.Settings.Current.TopBarEnabled)return;
        var foreground=NativeMethods.GetForegroundWindow();
        bool changed=kind==FloatingDock.Windows.RunningApps.WindowEventKind.Foreground;
        if(!changed && (kind!=FloatingDock.Windows.RunningApps.WindowEventKind.LocationChanged||hwnd!=foreground))return;
        if(changed) { foreach(var bar in _bars.Values)bar.RefreshLanguage();_geometryTimer.Stop();RefreshFullscreen(); }
        else if(!_geometryTimer.IsRunning)_geometryTimer.Start(); // batch movement; always inspect the final geometry
    }
    private void RefreshFullscreen()
    {
        if(_disposed||!_app.Settings.Current.TopBarEnabled)return;
        var foreground=NativeMethods.GetForegroundWindow();
        var root=NativeMethods.GetAncestor(foreground,NativeMethods.GA_ROOTOWNER);
        NativeMethods.GetWindowThreadProcessId(foreground,out var foregroundPid);
        if(foregroundPid==(uint)Environment.ProcessId) { foreground=new IntPtr(_app.Running.ForegroundRoot);root=NativeMethods.GetAncestor(foreground,NativeMethods.GA_ROOTOWNER); }
        foreach(var bar in _bars.Values)bar.SetFullscreen(FullscreenDetector.IsWindowFullscreenOn(foreground,bar.Monitor.Handle,(uint)Environment.ProcessId)||FullscreenDetector.IsWindowFullscreenOn(root,bar.Monitor.Handle,(uint)Environment.ProcessId));
    }
    internal void ApplySettings()
    {
        if(_disposed) return;
        var s=_app.Settings.Current;
        if(!s.TopBarEnabled)_geometryTimer.Stop();
        var monitors=MonitorHelper.GetMonitors();
        if(s.MonitorMode==MonitorMode.Primary) monitors=monitors.Take(1).ToList();
        foreach(var m in monitors)
        {
            if(!_bars.TryGetValue(m.DeviceName,out var bar)) _bars[m.DeviceName]=bar=new MenuBarWindow(m);
            bar.Apply(s,m);
        }
        foreach(var bar in _bars.Where(p=>!monitors.Any(m=>m.DeviceName==p.Key)).ToList()) { _bars.Remove(bar.Key);bar.Value.Dispose(); }
        if(s.TopBarEnabled && !s.TopBarAutoHide && s.TopBarReserveSpace && !Program.IsSmokeTest && !Program.IsDiagnosticProfile)
        {
            var result=_reservation.Apply(monitors.Select(m=>new AppBarRequest(m,(int)Math.Ceiling((MenuBarWindow.HeightDip+MenuBarWindow.TopInsetDip+MenuBarWindow.BottomInsetDip)*m.Scale),Top:true)).ToArray());
            if(!result.AllReserved) _app.Log.Warn("Menu bar work area not reserved: "+string.Join(",",result.Failed));
        }
        else _reservation.Release();
        OnRunning(_app.Running.Snapshot(),_app.Running.ForegroundRoot);
    }
    private void OnRunning(IReadOnlyList<WindowInfo> windows,long foreground)
    {
        if(_disposed||!_app.Settings.Current.TopBarEnabled) return;
        var current=windows.FirstOrDefault(w=>w.Hwnd==foreground);
        string? name=current is null ? NativeMethods.GetClass(new IntPtr(foreground)) is "Progman" or "WorkerW" ? L.Text("Desktop") : null : _app.Pinned.Snapshot().FirstOrDefault(p=>AppMatching.Matches(p,current))?.Name ?? DisplayName(current);
        foreach(var bar in _bars.Values)
        {
            if(name is not null) bar.SetAppName(name);
            if(!Program.IsSmokeTest) bar.SetFullscreen(FullscreenDetector.IsWindowFullscreenOn(new IntPtr(foreground),bar.Monitor.Handle,(uint)Environment.ProcessId));
        }
    }
    private string DisplayName(WindowInfo w)
    {
        string name=AppMatching.DefaultName(w);
        return name.ToLowerInvariant() switch { "explorer"=>L.Text("File Explorer"), "floatingdock"=>"FloatingDock settings", "taskmgr"=>L.Text("Task Manager"), "msedge"=>"Microsoft Edge", "chrome"=>"Google Chrome", "steam"=>"Steam", "chatgpt"=>"ChatGPT", "code"=>"Visual Studio Code", _=>name.Length>0?char.ToUpperInvariant(name[0])+name[1..]:L.Text("Desktop") };
    }
    internal void SetCaptureHidden(bool hidden) { foreach(var b in _bars.Values) b.SetCaptureHidden(hidden); }
    internal void SetSuspended(bool hidden) { foreach(var b in _bars.Values) b.SetSuspended(hidden); }
    public void Dispose()
    {
        _disposed=true;_app.Running.Changed-=OnRunning;_app.Running.NativeWindowChanged-=OnNativeWindowChanged;_app.Msg.TaskbarCreated-=OnExplorerRestart;_app.Msg.InputLanguageChanged-=OnLanguageChanged;_app.Tiling.StateChanged-=OnTilingChanged;
        _geometryTimer.Stop();_reservation.Dispose();foreach(var b in _bars.Values) b.Dispose();_bars.Clear();
    }
}

internal sealed class MenuBarWindow : IDisposable
{
    internal const int HeightDip=28;
    internal const int TopInsetDip=4,SideInsetDip=12,BottomInsetDip=4;
    private readonly DockApp _app=DockApp.Current;
    private readonly Window _window;
    private readonly Grid _root=new() { RequestedTheme=ElementTheme.Dark, Background=new SolidColorBrush(ColorHelper.FromArgb(96,18,21,27)), CornerRadius=new CornerRadius(14), BorderThickness=new Thickness(0) };
    private readonly TextBlock _name=new() { Text=L.Text("Desktop"),FontSize=13,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=420 };
    private readonly TextBlock _clock=new() { FontSize=13,VerticalAlignment=VerticalAlignment.Center };
    private readonly TextBlock _language=new() { FontSize=11,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center };
    private readonly TextBlock _battery=new() { FontSize=12,VerticalAlignment=VerticalAlignment.Center };
    private readonly FontIcon _network=new() { Glyph="\uE839",FontSize=15 };
    private readonly Button _batteryButton, _clockButton;
    private readonly Button _networkButton, _soundButton, _languageButton, _mediaButton, _searchButton, _trayButton;
    private bool _trayOpen;
    private int _trayGeneration;
    private readonly MenuBarMenus _menus;
    private readonly Button _mainButton;
    private readonly StackPanel _workspaces=new(){Orientation=Orientation.Horizontal,Spacing=3,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
    private readonly List<Button> _workspaceButtons=new();
    private IntPtr? _diagnosticLanguageTarget;
    internal MenuBarMenus Menus => _menus;
    internal void OpenMenuForDiagnostics(string kind,IntPtr target=default)
    {
        var button=kind switch { "main"=>_mainButton,"sound"=>_soundButton,"network"=>_networkButton,"language"=>_languageButton,"calendar"=>_clockButton,"media"=>_mediaButton,_=>throw new ArgumentException("Unknown quick menu.") };
        bool ignore=IgnoreRealCursorForDiagnostics;IgnoreRealCursorForDiagnostics=false;
        _diagnosticLanguageTarget=target==IntPtr.Zero?null:target;
        try
        {
            var peer=new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
        finally { IgnoreRealCursorForDiagnostics=ignore;_diagnosticLanguageTarget=null; }
    }
    private readonly DispatcherQueueTimer _clockTimer, _edgeTimer, _slideTimer, _languageTimer;
    private DockSettings _settings=new();
    private bool _disposed,_hover,_capture,_suspended,_fullscreen,_targetHidden;
    private bool _menuOpen;
    private float _hide;
    private long _lastSlideTicks;
    private DateTime _lastHover=DateTime.UtcNow;
    private string _clockText="";
    private IntPtr _languageSource;
    internal string LanguageCode=>_language.Text;
    internal MonitorDescriptor Monitor { get; private set; }
    internal IntPtr Handle { get; }
    internal float HideAmount=>_hide;
    internal string DiagnosticState=>$"hide={_hide}, target={_targetHidden}, hover={_hover}, menu={_menuOpen}, fullscreen={_fullscreen}, capture={_capture}, suspended={_suspended}, timer={_slideTimer.IsRunning}";
    internal string AppName=>_name.Text;
    internal bool IgnoreRealCursorForDiagnostics { get; set; }
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out Power status);
    [StructLayout(LayoutKind.Sequential)] private struct Power { public byte Ac,Flags,Percent,Reserved;public uint Life,FullLife; }

    internal MenuBarWindow(MonitorDescriptor monitor)
    {
        Monitor=monitor;
        _menus=new MenuBarMenus(open=> { _menuOpen=open;_lastHover=DateTime.UtcNow;if(open)Reveal(); });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var left=new StackPanel { Orientation=Orientation.Horizontal,Spacing=8,Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Center };
        var ring=new Ellipse { Width=12,Height=12,Stroke=new SolidColorBrush(Colors.White),StrokeThickness=2 };
        _mainButton=ActionButton(ring,"FloatingDock menu",ShowMenu);left.Children.Add(_mainButton);left.Children.Add(_name);
        _root.Children.Add(left);
        Grid.SetColumn(_workspaces,1);_root.Children.Add(_workspaces);
        var right=new StackPanel { Orientation=Orientation.Horizontal,Spacing=3,Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center };
        _searchButton=ActionButton(new FontIcon { Glyph="\uE721",FontSize=14 },"Search",_app.ToggleSearch);right.Children.Add(_searchButton);
        _mediaButton=ActionButton(new FontIcon { Glyph="\uE8D6",FontSize=15 },"Media",()=>_menus.Media(_mediaButton!));right.Children.Add(_mediaButton);
        _trayButton=ActionButton(new FontIcon { Glyph="\uE70D",FontSize=13 },"System tray",async()=>await OpenTrayAsync());right.Children.Add(_trayButton);
        _batteryButton=ActionButton(_battery,"Battery settings",()=>Open("ms-settings:batterysaver"));
        right.Children.Add(_batteryButton);
        _networkButton=ActionButton(_network,"Network",()=> { _menus.Network(_networkButton!); });right.Children.Add(_networkButton);
        _soundButton=ActionButton(new FontIcon { Glyph="\uE767",FontSize=15 },"Sound",()=> { _menus.Sound(_soundButton!); });right.Children.Add(_soundButton);
        _languageButton=ActionButton(_language,"Input language",()=> { _menus.Language(_languageButton!,_diagnosticLanguageTarget); });right.Children.Add(_languageButton);
        right.Children.Add(ActionButton(new FontIcon { Glyph="\uE713",FontSize=15 },"FloatingDock settings",()=>_app.ShowSettings()));
        _clockButton=ActionButton(_clock,"Date and time",ShowCalendar);right.Children.Add(_clockButton);
        Grid.SetColumn(right,2);_root.Children.Add(right);
        _window=new Window { Title="FloatingDock menu bar",Content=_root,SystemBackdrop=new GlassBackdrop() };
        WindowSetup.MakeBorderless(_window);Handle=WindowSetup.Hwnd(_window);WindowStyler.MakeOverlayWindow(Handle,noActivate:true);
        _root.PointerEntered+=(_,_)=> { if(IgnoreRealCursorForDiagnostics)return;_hover=true;Reveal(); };
        _root.PointerExited+=(_,_)=> { if(IgnoreRealCursorForDiagnostics)return;_hover=false;_lastHover=DateTime.UtcNow; };
        _languageTimer=_app.UiQueue.CreateTimer();_languageTimer.Interval=TimeSpan.FromMilliseconds(1000);_languageTimer.Tick+=(_,_)=>RefreshLanguage();
        _clockTimer=_app.UiQueue.CreateTimer();_clockTimer.Interval=TimeSpan.FromSeconds(1);_clockTimer.Tick+=(_,_)=>RefreshClock();
        _edgeTimer=_app.UiQueue.CreateTimer();_edgeTimer.Interval=TimeSpan.FromMilliseconds(100);_edgeTimer.Tick+=(_,_)=>CheckEdge();
        _slideTimer=_app.UiQueue.CreateTimer();_slideTimer.Interval=TimeSpan.FromMilliseconds(16);_slideTimer.Tick+=(_,_)=>Slide();
        NetworkChange.NetworkAvailabilityChanged+=OnNetwork;
        RefreshNetwork();RefreshClock();
    }
    private Button ActionButton(UIElement content,string tip,Action action)
    {
        var b=new Button { Content=content,Height=24,MinWidth=28,VerticalAlignment=VerticalAlignment.Center,Padding=new Thickness(7,0,7,0),BorderThickness=new Thickness(0),Background=new SolidColorBrush(Colors.Transparent),Foreground=new SolidColorBrush(Colors.White),CornerRadius=new CornerRadius(8) };
        ToolTipService.SetToolTip(b,L.Text(tip));b.Click+=(_,_)=> { if(!IgnoreRealCursorForDiagnostics){if(b!=_trayButton)CloseTray();action();} };return b;
    }
    internal Task OpenTrayForDiagnostics()=>OpenTrayAsync();
    internal bool TrayVisible=>_menus.ActiveKind=="tray"&&_menus.SurfaceVisible;
    private async Task OpenTrayAsync()
    {
        if(_trayOpen||_menus.ActiveKind=="tray"){CloseTray();return;}
        _menus.Close(immediately:true);_app.Palette?.Hide();_trayOpen=true;Reveal();int generation=++_trayGeneration;
        var items=await _app.ReadTrayItemsAsync();
        if(_disposed||generation!=_trayGeneration||_capture||_suspended||_fullscreen||!_settings.TopBarEnabled)return;
        _trayOpen=false;_menus.Tray(_trayButton,items);
    }
    private void CloseTray(){_trayGeneration++;if(_menus.ActiveKind=="tray")_menus.Close(immediately:true);_trayOpen=false;}
    private void Open(string uri) { if(!ShellLauncher.Execute(uri,null,null)) _app.Log.Warn("Could not open "+uri); }
    internal void Apply(DockSettings s,MonitorDescriptor m)
    {
        _settings=s;Monitor=m;
        RefreshWorkspaces();
        _slideTimer.Interval=TimeSpan.FromMilliseconds(1000d/s.TopBarAnimationFps);
        _mediaButton.Visibility=s.TopBarMediaEnabled?Visibility.Visible:Visibility.Collapsed;
        _trayButton.Visibility=s.TopBarTrayEnabled?Visibility.Visible:Visibility.Collapsed;
        _searchButton.Visibility=s.TopBarSearchEnabled?Visibility.Visible:Visibility.Collapsed;
        if(!s.TopBarTrayEnabled)CloseTray();

        int height=(int)Math.Ceiling(HeightDip*m.Scale);
        int inset=(int)Math.Round(SideInsetDip*m.Scale);
        WindowStyler.SetBounds(Handle,m.Bounds.Left+inset,ShownTop-(int)Math.Round(HideDistance*_hide),Math.Max(100,m.Bounds.Width-2*inset),height);
        WindowStyler.SetRoundedRegion(Handle,Math.Max(100,m.Bounds.Width-2*inset),height,(int)Math.Round(14*m.Scale));
        RefreshClock();
        if(s.TopBarEnabled) { if(!_capture&&!_suspended&&!(_fullscreen&&_hide>=1))WindowStyler.Show(Handle,false);_clockTimer.Start(); }
        else { CloseTray();_menus.Close();WindowStyler.Hide(Handle);_clockTimer.Stop();_languageTimer.Stop();_edgeTimer.Stop();_slideTimer.Stop();return; }
        if(s.TopBarAutoHide||_fullscreen) _edgeTimer.Start();else _edgeTimer.Stop();
        UpdateLanguageTimer();Target(!_hover&&!_menuOpen&&(s.TopBarAutoHide||_fullscreen)); // settings changes cancel any prior slide target
    }
    internal void SetAppName(string name) { if(_name.Text!=name)_name.Text=name; }
    internal void RefreshWorkspaces()
    {
        var settings=_app.Settings.Current;var session=_app.Tiling.Session;
        _workspaces.Visibility=settings.TilingEnabled&&settings.TilingShowWorkspaces&&session is not null&&Monitor.Bounds.Width/Monitor.Scale>=1000?Visibility.Visible:Visibility.Collapsed;
        if(_workspaces.Visibility==Visibility.Collapsed)return;
        if(_workspaceButtons.Count!=settings.TilingWorkspaces)
        {
            _workspaces.Children.Clear();_workspaceButtons.Clear();
            for(int workspace=1;workspace<=settings.TilingWorkspaces;workspace++)
            {
                int target=workspace;var button=ActionButton(new TextBlock {Text=workspace.ToString(),FontSize=11},L.Format("Workspace {0}",workspace),()=>_app.Tiling.SwitchWorkspace(target,Monitor.DeviceName));
                _workspaceButtons.Add(button);_workspaces.Children.Add(button);
            }
        }
        int active=session!.ActiveWorkspace(Monitor.DeviceName);
        for(int i=0;i<_workspaceButtons.Count;i++)
        {
            bool selected=i+1==active;
            if(_workspaceButtons[i].Tag is bool previous&&previous==selected)continue;
            _workspaceButtons[i].Tag=selected;_workspaceButtons[i].Background=new SolidColorBrush(selected?ColorHelper.FromArgb(45,255,255,255):Colors.Transparent);
        }
    }
    private void RefreshClock()
    {
        if(_disposed)return;
        var now=DateTime.Now;
        string text=now.ToString(_settings.TopBarShowSeconds?"HH:mm:ss":"HH:mm",CultureInfo.CurrentCulture);
        if(_settings.TopBarShowDate)text=now.ToString("dd MMM",CultureInfo.CurrentCulture)+"  "+text;
        if(_clockText!=text) { _clockText=text;_clock.Text=text;ToolTipService.SetToolTip(_clock,now.ToString("D",CultureInfo.CurrentCulture)); }
        RefreshLanguage();
        if(now.Second%30==0||_battery.Text.Length==0)
        {
            if(GetSystemPowerStatus(out var p) && p.Percent<=100 && (p.Flags&128)==0) { _battery.Text=(p.Ac==1?"↯ ":"")+p.Percent+"%";_batteryButton.Visibility=Visibility.Visible; }
            else { _battery.Text="";_batteryButton.Visibility=Visibility.Collapsed; }
        }
    }
    private int ShownTop=>Monitor.Bounds.Top+(int)Math.Round(TopInsetDip*Monitor.Scale);
    private int HideDistance=>(int)Math.Ceiling((HeightDip+TopInsetDip)*Monitor.Scale)-2;
    internal void RefreshLanguage()
    {
        if(_disposed||_capture||_suspended||_hide>=1||!_settings.TopBarEnabled)return;
        var foreground=NativeMethods.GetForegroundWindow();
        uint thread=NativeMethods.GetWindowThreadProcessId(foreground,out var pid);
        if(pid!=(uint)Environment.ProcessId&&foreground!=IntPtr.Zero)_languageSource=foreground;
        else if(_menus.ActiveKind is not null||foreground==Handle||thread==0)
        {
            var target=NativeMethods.IsWindow(_menus.LanguageTarget)?_menus.LanguageTarget:_languageSource;
            if(NativeMethods.IsWindow(target))thread=NativeMethods.GetWindowThreadProcessId(target,out _);
        }
        if(thread==0)return;
        try { string language=CultureInfo.GetCultureInfo((int)(GetKeyboardLayout(thread).ToInt64()&0xffff)).TwoLetterISOLanguageName.ToUpperInvariant();if(_language.Text!=language)_language.Text=language; } catch(CultureNotFoundException) { }
    }
    private void UpdateLanguageTimer()
    {
        if(_settings.TopBarEnabled&&!_capture&&!_suspended&&_hide<1) { if(!_languageTimer.IsRunning)_languageTimer.Start(); }
        else _languageTimer.Stop();
    }
    private void OnNetwork(object? sender,NetworkAvailabilityEventArgs e)=>_app.UiQueue.TryEnqueue(()=> { if(!_disposed)RefreshNetwork(); });
    private void RefreshNetwork()=>_network.Opacity=NetworkInterface.GetIsNetworkAvailable()?1:.4;
    private void ShowCalendar() => _menus.Calendar(_clockButton);
    private void ShowMenu()
    {
        _menus.Main(_mainButton);
    }
    internal void SetCaptureHidden(bool hidden)
    {
        if(_capture==hidden)return;_capture=hidden;
        if(hidden) { CloseTray();_app.Palette?.Hide();_menus.Close(immediately:true);_slideTimer.Stop();WindowStyler.Hide(Handle);_languageTimer.Stop(); }
        else if(_settings.TopBarEnabled&&!_suspended) { _hide=1;Move();WindowStyler.Show(Handle,false);Target(_settings.TopBarAutoHide||_fullscreen);UpdateLanguageTimer(); }
    }
    internal void SetSuspended(bool hidden)
    {
        _suspended=hidden;
        if(hidden) { CloseTray();_app.Palette?.Hide();_menus.Close(immediately:true);WindowStyler.Hide(Handle);_languageTimer.Stop(); }
        else if(_settings.TopBarEnabled&&!_capture) { WindowStyler.Show(Handle,false);Target(_settings.TopBarAutoHide||_fullscreen);UpdateLanguageTimer(); }
    }
    internal void SetFullscreen(bool fullscreen)
    {
        if(_fullscreen==fullscreen)return;_fullscreen=fullscreen;
        if(fullscreen){CloseTray();_app.Palette?.Hide();_menus.Close(immediately:true);}
        else if(_settings.TopBarEnabled&&!_capture&&!_suspended)WindowStyler.Show(Handle,false);
        if(_settings.TopBarEnabled) { if(fullscreen||_settings.TopBarAutoHide)_edgeTimer.Start();else _edgeTimer.Stop();Target(fullscreen||_settings.TopBarAutoHide); }
    }
    internal void Reveal() { if(_fullscreen)return;_lastHover=DateTime.UtcNow;Target(false); }
    private void CheckEdge()
    {
        if(_capture||_suspended||_menuOpen||_trayOpen||!_settings.TopBarEnabled)return;
        if(_fullscreen) { Target(true);return; }
        if(!IgnoreRealCursorForDiagnostics && WindowStyler.CursorPos() is { } c)
        {
            int h=(int)Math.Ceiling(HeightDip*Monitor.Scale);
            int inset=(int)(SideInsetDip*Monitor.Scale);
            bool over=c.X>=Monitor.Bounds.Left+inset&&c.X<Monitor.Bounds.Right-inset&&c.Y>=Monitor.Bounds.Top&&c.Y<(_hide>.99?Monitor.Bounds.Top+2:ShownTop+h);
            if(over) { Reveal();return; }
        }
        if(!_hover && DateTime.UtcNow-_lastHover>TimeSpan.FromMilliseconds(_settings.TopBarHideDelayMs))Target(true);
    }
    internal void Target(bool hidden)
    {
        if(_capture||_suspended||!_settings.TopBarEnabled)return;
        hidden |= _fullscreen;
        _targetHidden=hidden;
        if(!hidden&&_hide>=1)_languageTimer.Start();
        if(_hide!=(hidden?1:0)&&!_slideTimer.IsRunning) { _lastSlideTicks=System.Diagnostics.Stopwatch.GetTimestamp();_slideTimer.Start(); }
    }
    private void Slide()
    {
        float target=_targetHidden?1:0;
        if(!SurfaceMotion.Enabled){_hide=target;Move();UpdateLanguageTimer();_slideTimer.Stop();return;}
        long now=System.Diagnostics.Stopwatch.GetTimestamp();
        float dt=(float)Math.Clamp((now-_lastSlideTicks)/(double)System.Diagnostics.Stopwatch.Frequency,0,.1);_lastSlideTicks=now;
        _hide=target>_hide?Math.Min(target,_hide+dt/(_settings.TopBarHideMs/1000f)):Math.Max(target,_hide-dt/(_settings.TopBarShowMs/1000f));
        Move();UpdateLanguageTimer();if(_hide==target) { _slideTimer.Stop();if(_fullscreen&&_hide>=1)WindowStyler.Hide(Handle); }
    }
    private void Move()
    {
        int h=(int)Math.Ceiling(HeightDip*Monitor.Scale);
        float ease=_hide*_hide*(3-2*_hide);
        WindowStyler.Move(Handle,Monitor.Bounds.Left+(int)Math.Round(SideInsetDip*Monitor.Scale),ShownTop-(int)Math.Round(HideDistance*ease));
    }
    public void Dispose()
    {
        _disposed=true;CloseTray();_menus.Dispose();NetworkChange.NetworkAvailabilityChanged-=OnNetwork;_clockTimer.Stop();_edgeTimer.Stop();_slideTimer.Stop();_languageTimer.Stop();_window.Close();
    }
}

