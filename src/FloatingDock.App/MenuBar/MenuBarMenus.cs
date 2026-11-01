using FloatingDock.UI.Services;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.Shell;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.MenuBar;

/// <summary>One light-dismiss surface at a time. Queries/polling live only for the open menu.</summary>
internal sealed class MenuBarMenus : IDisposable
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private readonly Action<bool> _opened;
    private readonly DispatcherQueueTimer _audioTimer;
    private readonly DispatcherQueueTimer _dismissTimer;
    private MenuSurfaceMotion? _motion;
    private readonly GlideHighlight _glide=new();
    private Grid? _popupHost;
    private Grid? _contentHost;
    private UIElement? _shown;
    /// <summary>Hovering a list item glides one shared highlight to it, instead of each row flashing on its own.</summary>
    private void OnPopupPointer(object sender,Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        DependencyObject? node=e.OriginalSource as DependencyObject;
        while(node is not null&&!ReferenceEquals(node,_contentHost)&&!(node is Button{Tag:"row"}))node=VisualTreeHelper.GetParent(node);
        if(node is Button row&&row.IsEnabled){var c=M3.Current;_glide.SetColor(ColorHelper.FromArgb(34,c.OnSurface.R,c.OnSurface.G,c.OnSurface.B));_glide.MoveTo(row,M3.CornerL);}
        else _glide.Hide();
    }
    private IntPtr _lastForeground;
    private IntPtr _returnFocus;
    private Window? _popupWindow;
    private IntPtr _popupHwnd;
    private Border? _popupRoot;
    private ScrollViewer? _scroll;
    private StackPanel? _mainPanel;
    private StackPanel? _trayPanel;
    private IReadOnlyList<TrayItem>? _trayPanelItems;
    private FloatingDock.Core.Configuration.InterfaceLanguage _trayPanelLanguage;
    private (FloatingDock.Core.Configuration.InterfaceLanguage Language, bool AutoHide)? _mainState;
    internal int MainContentBuildsForDiagnostics { get; private set; }
    internal ScrollViewer? ScrollForDiagnostics=>_scroll;
    private bool _showing;
    private bool _trayActionPending;
    private IntPtr _trayContextMenu;
    internal bool TrayDiscoveryPending { get; set; }
    private bool TrayContextActive=>TrayDiscoveryPending||_trayActionPending||_trayContextMenu!=IntPtr.Zero&&NativeMethods.IsWindowVisible(_trayContextMenu);
    private Button? _anchor;
    private Action? _resizePopup;
    internal IntPtr PopupHandle => _popupHwnd;
    private int _generation;
    private long _openingUntil;
    private bool Opening=>System.Diagnostics.Stopwatch.GetTimestamp()<_openingUntil;
    internal bool SurfaceVisible { get; private set; }
    private bool _captureActive;
    internal bool CaptureActive
    {
        get=>_captureActive;
        set
        {
            if(_captureActive==value)return;_captureActive=value;
            if(PopupHandle!=IntPtr.Zero)WindowStyler.SetTopmost(PopupHandle,!value);
        }
    }
    internal string? ActiveKind { get; private set; }
    internal Slider? VolumeSlider { get; private set; }
    internal ToggleButton? MuteButton { get; private set; }
    internal IReadOnlyList<Button> LanguageButtons { get; private set; } = Array.Empty<Button>();
    internal IntPtr LanguageTarget { get; private set; }
    internal bool NetworkReady { get; private set; }
    internal string NetworkSummary { get; private set; } = "";
    internal MenuSurfaceMotion? MotionForDiagnostics=>_motion;
    internal MenuBarMenus(Action<bool> opened)
    {
        _opened=opened;_audioTimer=DockApp.Current.UiQueue.CreateTimer();
        _audioTimer.Interval=TimeSpan.FromMilliseconds(700);
        _dismissTimer=DockApp.Current.UiQueue.CreateTimer();_dismissTimer.Interval=TimeSpan.FromMilliseconds(100);
        _dismissTimer.Tick+=(_,_)=>
        {
            var foreground=NativeMethods.GetForegroundWindow();
            if(TrayContextActive){_lastForeground=foreground;return;}
            if(_trayContextMenu!=IntPtr.Zero){_trayContextMenu=IntPtr.Zero;WindowStyler.SetTopmost(PopupHandle,!CaptureActive);_lastForeground=foreground;return;}
            bool pressed=(GetAsyncKeyState(1)&0x8000)!=0||(GetAsyncKeyState(2)&0x8000)!=0;
            bool inPopup=NativeMethods.GetCursorPos(out var p)&&NativeMethods.GetWindowRect(PopupHandle,out var r)&&p.X>=r.Left&&p.X<r.Right&&p.Y>=r.Top&&p.Y<r.Bottom;
            if(Opening&&!pressed)return;
            if(!CaptureActive&&!_showing&&ActiveKind is not null&&!PointerOverAnchor()&&((foreground!=_lastForeground&&foreground!=PopupHandle)||(pressed&&!inPopup)))Close();
            _lastForeground=foreground;
        };
        DockApp.Current.Running.NativeWindowChanged+=OnForegroundChanged;
    }
    private void OnForegroundChanged(FloatingDock.Windows.RunningApps.WindowEventKind kind,IntPtr hwnd)
    {
        if(!CaptureActive&&!Opening&&!TrayContextActive&&kind==FloatingDock.Windows.RunningApps.WindowEventKind.Foreground&&ActiveKind is not null&&!_showing&&NativeMethods.GetForegroundWindow()!=PopupHandle&&!PointerOverAnchor())Close();
    }
    private static SolidColorBrush Brush(byte a,byte r,byte g,byte b)=>new(ColorHelper.FromArgb(a,r,g,b));
    private static TextBlock Text(string text,double size=13,bool secondary=false)=>new() { Text=L.Text(text),FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=secondary?GlassStyle.Secondary:GlassStyle.Primary };
    private static StackPanel Panel()=>new() { Width=300,Spacing=12 };
    private M3.Scheme? _builtScheme;
    /// <summary>Panels cache their brushes; rebuild them when the seed or theme changed.</summary>
    private void Rescheme(){var now=M3.Current;if(ReferenceEquals(_builtScheme,now))return;_builtScheme=now;_mainState=null;_mainPanel=null;_trayPanel=null;_trayPanelItems=null;}
    private static Grid Header(string title,string glyph)
    {
        var grid=new Grid { Margin=new Thickness(2,0,2,4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph=glyph,FontSize=20,Margin=new Thickness(0,0,12,0),Foreground=M3.Brush(M3.Current.Primary) });
        var label=Text(title,18);label.FontWeight=new global::Windows.UI.Text.FontWeight { Weight=500 };Grid.SetColumn(label,1);grid.Children.Add(label);return grid;
    }
    private static Border Divider()=>new() { Height=1,Background=M3.Brush(M3.Current.OutlineVariant,150),Margin=new Thickness(0,6,0,6) };
    /// <summary>A Material list item: leading icon, one or two lines of text, a trailing check when it is the current choice.</summary>
    private static Button Row(string title,string detail,string glyph,Action action,bool selected=false)
    {
        var c=M3.Current;
        var row=new Grid { ColumnSpacing=16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph=glyph,FontSize=18,Width=24,VerticalAlignment=VerticalAlignment.Center,Foreground=M3.Brush(selected?c.OnSecondaryContainer:c.OnSurfaceVariant) });
        var text=new StackPanel { Spacing=1,VerticalAlignment=VerticalAlignment.Center };var name=Text(title,14);if(selected)name.Foreground=M3.Brush(c.OnSecondaryContainer);text.Children.Add(name);
        if(detail.Length>0)text.Children.Add(Text(detail,12,true));Grid.SetColumn(text,1);row.Children.Add(text);
        if(selected) { var check=new FontIcon { Glyph="\uE73E",FontSize=14,Foreground=M3.Brush(c.OnSecondaryContainer),VerticalAlignment=VerticalAlignment.Center };Grid.SetColumn(check,2);row.Children.Add(check); }
        var b=new Button { Tag="row",Content=row,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(16,0,16,0),MinHeight=detail.Length>0?56:48,CornerRadius=new CornerRadius(M3.CornerL),BorderThickness=new Thickness(0) };
        GlassStyle.Button(b,flat:!selected);if(selected)b.Background=GlassStyle.Selected;
        b.Foreground=M3.Brush(selected?c.OnSecondaryContainer:c.OnSurface);
        AutomationProperties.SetName(b,title+" "+detail);b.Click+=(_,_)=>action();return b;
    }
    private void Show(Button anchor,StackPanel content,string kind,bool replace=false)
    {
        if(ActiveKind==kind&&!replace) { Close();return; }
        var previous=NativeMethods.GetForegroundWindow();
        if(previous==PopupHandle)previous=_returnFocus;
        float progress=SurfaceVisible&&_anchor==anchor?(_motion?.Progress??0):0;
        Close(immediately:true);_returnFocus=previous;ActiveKind=kind;_anchor=anchor;_showing=true;
        _openingUntil=System.Diagnostics.Stopwatch.GetTimestamp()+System.Diagnostics.Stopwatch.Frequency*300/1000;
        try
        {
            if(_popupWindow is null)
            {
                _popupRoot=new Border { Background=GlassStyle.Surface,BorderBrush=GlassStyle.Stroke,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(M3.CornerXl),Padding=new Thickness(16) };
                _scroll=new ScrollViewer { HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
                _popupRoot.Child=_scroll;
                _popupHost=new Grid { Padding=Pop.Pad,Background=new SolidColorBrush(Colors.Transparent) };_popupHost.Children.Add(_popupRoot);
                _popupWindow=new Window { Title="FloatingDock quick menu",Content=_popupHost,SystemBackdrop=new TransparentBackdrop() };
                _popupHwnd=WindowSetup.Hwnd(_popupWindow);
                WindowSetup.MakeBorderless(_popupWindow);WindowStyler.MakeOverlayWindow(PopupHandle,noActivate:false);
                _motion=new MenuSurfaceMotion(PopupHandle,_popupRoot);
                _popupWindow.Activated+=(_,e)=> { if(!CaptureActive&&!Opening&&!_showing&&!TrayContextActive&&e.WindowActivationState==WindowActivationState.Deactivated&&NativeMethods.GetForegroundWindow()!=PopupHandle&&!PointerOverAnchor())Close(); };
                _popupRoot.KeyDown+=(_,e)=> { if(e.Key==global::Windows.System.VirtualKey.Escape) { e.Handled=true;Close(); } };
            }
            var monitor=FloatingDock.Windows.Display.MonitorHelper.Describe(NativeMethods.MonitorFromWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(anchor.XamlRoot.ContentIslandEnvironment.AppWindowId),NativeMethods.MONITOR_DEFAULTTONEAREST))!;
            // The seed or theme may have changed since the surface was built.
            M3.Theme(_popupRoot!);_popupRoot!.Background=GlassStyle.Surface;_popupRoot.BorderBrush=GlassStyle.Stroke;
            double maxHeight=Math.Max(160,(monitor.Bounds.Height-70)/monitor.Scale);
            _scroll!.MaxHeight=maxHeight-34;
            if(_contentHost is null){_contentHost=new Grid();_contentHost.Children.Add(_glide.Layer);_scroll.Content=_contentHost;_contentHost.PointerMoved+=OnPopupPointer;_contentHost.PointerExited+=(_,_)=>_glide.Hide();}
            if(!ReferenceEquals(_shown,content)){if(_shown is not null)_contentHost.Children.Remove(_shown);if(content.Parent is Panel old)old.Children.Remove(content);_contentHost.Children.Add(content);_shown=content;}
            _glide.Hide();
            _scroll.ChangeView(0,0,null,true); // Reopening starts at the top, like a fresh popup.
            _popupRoot!.IsHitTestVisible=true;
            _popupRoot.Measure(new global::Windows.Foundation.Size(334,maxHeight));
            int width=(int)Math.Ceiling(334*monitor.Scale),height=(int)Math.Ceiling(_popupRoot.DesiredSize.Height*monitor.Scale);
            var point=anchor.TransformToVisual(anchor.XamlRoot.Content).TransformPoint(new global::Windows.Foundation.Point(anchor.ActualWidth,anchor.ActualHeight));
            NativeMethods.GetWindowRect(Microsoft.UI.Win32Interop.GetWindowFromWindowId(anchor.XamlRoot.ContentIslandEnvironment.AppWindowId),out var owner);
            int x=Math.Clamp(owner.Left+(int)((kind=="main"?point.X-anchor.ActualWidth:point.X)*monitor.Scale)-(kind=="main"?0:width),monitor.Bounds.Left+8,monitor.Bounds.Right-width-8);
            int y=owner.Bottom+(int)Math.Ceiling(6*monitor.Scale);
            int pad=(int)Math.Ceiling(Pop.PadDip*monitor.Scale);
            WindowStyler.SetBounds(PopupHandle,x-pad,y-pad,width+2*pad,Math.Max(height,100)+2*pad,topmost:!CaptureActive);
            SurfaceVisible=true;
            // Clip the actual glass HWND to the same anchored transform as its XAML plate.
            double pivot=(owner.Left+(point.X-anchor.ActualWidth/2)*monitor.Scale-x)/monitor.Scale;
            _motion!.Prepare(SurfaceMotion.Enabled?progress:1);
            _motion.SetGeometry(width,Math.Max(height,100),monitor.Scale,pivot);
            int duration=Math.Clamp(DockApp.Current.Settings.Current.MenuAnimationMs*2,360,700);
            // The measured native geometry is enough to start even on the first open.
            // Waiting for Loaded left a completely transparent window before its first frame.
            if(progress<.99f)SurfaceMotion.MenuContent(content,duration,entering:true);
            _motion.Play(entering:true,duration);
            _opened(true);_popupWindow.Activate();WindowStyler.BringToFront(PopupHandle);
            _lastForeground=NativeMethods.GetForegroundWindow();_dismissTimer.Start();
            int generation=_generation;
            _resizePopup=()=>DockApp.Current.UiQueue.TryEnqueue(()=>
            {
                if(generation!=_generation||ActiveKind!=kind)return;
                _popupRoot.UpdateLayout();content.Measure(new global::Windows.Foundation.Size(300,double.PositiveInfinity));
                int finalHeight=(int)Math.Ceiling(Math.Min(maxHeight,content.DesiredSize.Height+34)*monitor.Scale);
                WindowStyler.SetBounds(PopupHandle,x-pad,y-pad,width+2*pad,Math.Max(finalHeight,100)+2*pad,topmost:!CaptureActive);
                _motion.SetGeometry(width,Math.Max(finalHeight,100),monitor.Scale,pivot);
            });
            _resizePopup();

        }
        finally { _showing=false; }
    }
    internal void Close(bool immediately=false)
    {
        if(ActiveKind is null&&!immediately)return;
        ActiveKind=null;_generation++;_audioTimer.Stop();_dismissTimer.Stop();_resizePopup=null;
        _trayContextMenu=IntPtr.Zero;
        _motion?.Stop();
        MediaButtons=Array.Empty<Button>();MediaReady=false;
        VolumeSlider=null;MuteButton=null;LanguageButtons=Array.Empty<Button>();
        if(_popupRoot is not null)_popupRoot.IsHitTestVisible=false;
        if(_popupWindow is not null)
        {
            if(immediately||!SurfaceMotion.Enabled)HidePopup();
            else
            {
                int duration=Math.Clamp(DockApp.Current.Settings.Current.MenuAnimationMs*4/5,80,300);
                int generation=_generation;
                _motion!.Play(entering:false,duration,()=>{if(generation==_generation&&ActiveKind is null)HidePopup();});
            }
        }
        else _opened(false);
    }
    private void HidePopup()
    {
        _motion?.Stop();
        bool focused=NativeMethods.GetForegroundWindow()==PopupHandle;
        _popupWindow?.AppWindow.Hide();
        WindowStyler.Hide(PopupHandle);
        if(_popupRoot is not null)
        {
            SurfaceMotion.Reset(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_popupRoot),0);
            // Keep the last measured tree attached while its HWND is hidden.
            // Reopening a static menu reuses its templates and layout resources.
        }
        SurfaceVisible=false;_opened(false);
        // Restore the app underneath only when this menu still owns focus. An outside
        // click, screenshot overlay, or another application's activation wins instead.
        if(focused&&NativeMethods.IsWindow(_returnFocus)&&NativeMethods.IsWindowVisible(_returnFocus)&&!NativeMethods.IsIconic(_returnFocus))NativeMethods.SetForegroundWindow(_returnFocus);
    }
    internal void Reassert()
    {
        if(!CaptureActive&&!TrayContextActive&&SurfaceVisible&&NativeMethods.IsWindowVisible(PopupHandle))
            NativeMethods.SetWindowPos(PopupHandle,NativeMethods.HWND_TOPMOST,0,0,0,0,NativeMethods.SWP_NOMOVE|NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE);
    }
    internal void Main(Button anchor)
    {
        if(ActiveKind=="main") { Close();return; }
        Rescheme();
        var app=DockApp.Current;
        var state=(app.Settings.Current.Language,app.Settings.Current.TopBarAutoHide);
        if(_mainState==state&&_mainPanel is not null){Show(anchor,_mainPanel,"main");return;}
        var panel=Panel();panel.Spacing=5;
        void Add(string name,string glyph,Action action,bool selected=false)=>panel.Children.Add(Row(name,"",glyph,()=> { Close(immediately:true);action(); },selected));
        Add("Search","\uE721",app.ToggleSearch);
        Add("Launchpad","\uE80A",app.ToggleLaunchpad);
        Add(L.Text("FloatingDock settings"),"\uE713",()=>app.ShowSettings());
        panel.Children.Add(Divider());
        Add(L.Text("Auto-hide menu bar"),"\uE890",()=>app.Settings.Update(s=>s.TopBarAutoHide=!s.TopBarAutoHide),app.Settings.Current.TopBarAutoHide);
        Add(L.Text("Hide menu bar"),"\uE8F4",()=>app.Settings.Update(s=>s.TopBarEnabled=false));
        panel.Children.Add(Divider());
        foreach(bool restart in new[]{false,true})
            panel.Children.Add(Row(L.Text(restart?"Restart":"Shut down"),"",restart?"\uE777":"\uE7E8",async()=>
            {
                if(_popupRoot?.XamlRoot is not { } root)return;
                _trayActionPending=true;try{await PowerActions.ConfirmAsync(root,restart);}finally{_trayActionPending=false;}
            }));
        panel.Children.Add(Divider());Add(L.Text("Quit FloatingDock"),"\uE7E8",app.Shutdown);
        _mainState=state;_mainPanel=panel;MainContentBuildsForDiagnostics++;
        Show(anchor,panel,"main");
    }
    private bool PointerOverAnchor()
    {
        if((GetAsyncKeyState(1)&0x8000)==0||_anchor?.XamlRoot is null||!NativeMethods.GetCursorPos(out var cursor))return false;
        var window=Microsoft.UI.Win32Interop.GetWindowFromWindowId(_anchor.XamlRoot.ContentIslandEnvironment.AppWindowId);
        if(!NativeMethods.GetWindowRect(window,out var bounds))return false;
        var p=_anchor.TransformToVisual(_anchor.XamlRoot.Content).TransformPoint(new global::Windows.Foundation.Point());
        double scale=_anchor.XamlRoot.RasterizationScale,x=bounds.Left+p.X*scale,y=bounds.Top+p.Y*scale;
        return cursor.X>=x&&cursor.X<x+_anchor.ActualWidth*scale&&cursor.Y>=y&&cursor.Y<y+_anchor.ActualHeight*scale;
    }
    internal IMediaController MediaSource { get; set; } = new MediaController();
    internal IReadOnlyList<Button> MediaButtons { get; private set; } = Array.Empty<Button>();
    internal bool MediaReady { get; private set; }
    internal void Media(Button anchor)
    {
        if(ActiveKind=="media"){Close();return;}
        var c=M3.Current;
        var panel=Panel();
        // Header: a tonal art tile (no cover art is exposed), title and artist.
        var head=new Grid { ColumnSpacing=14 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });head.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
        var art=new Border { Width=64,Height=64,CornerRadius=new CornerRadius(M3.CornerL),Background=M3.Brush(c.SecondaryContainer),Child=new FontIcon { Glyph="",FontSize=26,Foreground=M3.Brush(c.OnSecondaryContainer),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center } };
        head.Children.Add(art);
        var names=new StackPanel { Spacing=2,VerticalAlignment=VerticalAlignment.Center };Grid.SetColumn(names,1);head.Children.Add(names);
        var title=Text("Searching…",16);title.FontWeight=new global::Windows.UI.Text.FontWeight { Weight=600 };title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;
        var artist=Text("",13,true);artist.TextWrapping=TextWrapping.NoWrap;artist.TextTrimming=TextTrimming.CharacterEllipsis;
        names.Children.Add(title);names.Children.Add(artist);panel.Children.Add(head);
        // Transport: shuffle, previous, a large primary play/pause, next, repeat.
        var controls=new StackPanel { Orientation=Orientation.Horizontal,Spacing=8,HorizontalAlignment=HorizontalAlignment.Center };
        Button Round(string glyph,string label,double width,double height,bool primary,double glyphSize=18)
        {
            var button=new Button { Content=new FontIcon { Glyph=glyph,FontSize=glyphSize },Width=width,Height=height,CornerRadius=new CornerRadius(height/2),IsEnabled=false,BorderThickness=new Thickness(0),Padding=new Thickness(0) };
            button.Style=(Style)Application.Current.Resources["M3ButtonStyle"];
            button.Background=M3.Brush(primary?c.Primary:c.ContainerHighest);button.Foreground=M3.Brush(primary?c.OnPrimary:c.OnSurface);
            AutomationProperties.SetName(button,L.Text(label));ToolTipService.SetToolTip(button,L.Text(label));controls.Children.Add(button);return button;
        }
        var shuffle=Round("","Shuffle",44,44,false);
        var previous=Round("","Previous",48,48,false,20);var toggle=Round("","Play / pause",84,56,true,24);var next=Round("","Next",48,48,false,20);
        var repeat=Round("","Repeat",44,44,false);
        panel.Children.Add(controls);
        // Progress: elapsed time, the wave, total time.
        var line=new Grid { ColumnSpacing=10 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });line.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });line.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        var elapsed=Text("0:00",12,true);var total=Text("0:00",12,true);elapsed.VerticalAlignment=total.VerticalAlignment=VerticalAlignment.Center;
        var wave=new WavySlider();Grid.SetColumn(wave,1);Grid.SetColumn(total,2);
        line.Children.Add(elapsed);line.Children.Add(wave);line.Children.Add(total);panel.Children.Add(line);
        var status=Text("",12,true);panel.Children.Add(status);Show(anchor,panel,"media");
        int generation=_generation;bool reading=false,acting=false;
        MediaReady=false;MediaButtons=new[]{previous,toggle,next};
        bool Current()=>ActiveKind=="media"&&generation==_generation;
        static string Clock(double seconds){var t=TimeSpan.FromSeconds(Math.Max(0,seconds));return t.TotalHours>=1?$"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}":$"{t.Minutes}:{t.Seconds:00}";}
        async Task Read()
        {
            if(reading||acting||!Current())return;reading=true;
            try
            {
                var state=await MediaSource.ReadAsync();if(!Current())return;
                title.Text=state is null?L.Text("No active media session"):state.Title;
                artist.Text=state?.Artist??"";
                previous.IsEnabled=state?.Previous==true;toggle.IsEnabled=state?.Toggle==true;next.IsEnabled=state?.Next==true;
                shuffle.IsEnabled=state?.CanShuffle==true;repeat.IsEnabled=state?.CanRepeat==true;
                // An active toggle is a tonal container with the primary colour, as on the reference player.
                shuffle.Background=M3.Brush(state?.Shuffle==true?c.SecondaryContainer:c.ContainerHighest);shuffle.Foreground=M3.Brush(state?.Shuffle==true?c.Primary:c.OnSurface);
                bool repeating=(state?.Repeat??0)!=0;
                repeat.Background=M3.Brush(repeating?c.SecondaryContainer:c.ContainerHighest);repeat.Foreground=M3.Brush(repeating?c.Primary:c.OnSurface);
                ((FontIcon)repeat.Content).Glyph=state?.Repeat==1?"":"";
                ((FontIcon)toggle.Content).Glyph=state?.Playing==true?"":"";status.Text="";
                double duration=state?.Duration??0,position=state?.Position??0;
                elapsed.Text=Clock(position);total.Text=duration>0?Clock(duration):"";
                wave.Set(duration>0?position/duration:0,state?.Playing==true);
            }
            catch(Exception) { if(Current()){title.Text=L.Text("Media controls are unavailable.");previous.IsEnabled=toggle.IsEnabled=next.IsEnabled=shuffle.IsEnabled=repeat.IsEnabled=false;} }
            finally { reading=false;if(Current()){MediaReady=true;_resizePopup?.Invoke();} }
        }
        async Task Act(string command)
        {
            if(acting||!Current())return;acting=true;
            try { if(!await MediaSource.ControlAsync(command)&&Current())status.Text=L.Text("Media controls are unavailable."); }
            catch(Exception){if(Current())status.Text=L.Text("Media controls are unavailable.");}
            finally { acting=false;if(Current())await Read(); }
        }
        previous.Click+=async(_,_)=>await Act("previous");toggle.Click+=async(_,_)=>await Act("toggle");next.Click+=async(_,_)=>await Act("next");
        shuffle.Click+=async(_,_)=>await Act("shuffle");repeat.Click+=async(_,_)=>await Act("repeat");
        if(_audioTick is not null)_audioTimer.Tick-=_audioTick;
        _audioTick=async(_,_)=>await Read();_audioTimer.Tick+=_audioTick;_audioTimer.Start();_=Read();
    }
    internal void TrayUnavailable(Button anchor)
    {
        var panel=Panel();panel.Children.Add(Header("System tray","\uE70D"));
        panel.Children.Add(Text("Windows did not expose its tray arrow. Keep Explorer running and enable hidden tray icons in Windows settings.",13,true));
        panel.Children.Add(Row("Windows taskbar","More settings","\uE713",()=> { Close();ShellLauncher.Execute("ms-settings:taskbar",null,null); }));
        Show(anchor,panel,"tray");
    }
    internal IReadOnlyList<TrayItem> TrayItems { get; private set; }=Array.Empty<TrayItem>();
    internal void Tray(Button anchor,IReadOnlyList<TrayItem> items,bool loading=false)
    {
        Rescheme();
        if(!loading&&ReferenceEquals(items,_trayPanelItems)&&_trayPanel is not null&&_trayPanelLanguage==DockApp.Current.Settings.Current.Language)
        {TrayItems=items;Show(anchor,_trayPanel,"tray",replace:ActiveKind=="tray");return;}
        TrayItems=items;var panel=Panel();panel.Children.Add(Header("System tray","\uE70D"));
        var grid=new Grid { ColumnSpacing=8,RowSpacing=8 };
        for(int c=0;c<6;c++)grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        for(int r=0;r<(items.Count+5)/6;r++)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        for(int i=0;i<items.Count;i++)
        {
            var item=items[i];var button=new Button { Width=42,Height=42,Padding=new Thickness(0),CornerRadius=new CornerRadius(14),Content=new FontIcon { Glyph="\uE71D",FontSize=20 },UseLayoutRounding=true };GlassStyle.Button(button);
            ToolTipService.SetToolTip(button,item.Name);AutomationProperties.SetName(button,item.Name);
            if(item.Icon is not null)_=LoadIcon();
            async Task LoadIcon()
            {
                try
                {
                    using var stream=new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using(var writer=new global::Windows.Storage.Streams.DataWriter(stream)){writer.WriteBytes(item.Icon!);await writer.StoreAsync();writer.DetachStream();}
                    stream.Seek(0);var bitmap=new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();await bitmap.SetSourceAsync(stream);
                    double size=TrayIconImage.DisplaySize(item.Icon,anchor.XamlRoot.RasterizationScale);
                    if(size>0)button.Content=new Image { Source=bitmap,Width=size,Height=size,Stretch=Stretch.Uniform,UseLayoutRounding=true };
                }
                catch(Exception){}
            }
            async Task Activate(bool context)
            {
                var p=button.TransformToVisual(_popupHost).TransformPoint(new(button.ActualWidth/2,button.ActualHeight/2));
                NativeMethods.GetWindowRect(PopupHandle,out var bounds);
                double scale=button.XamlRoot.RasterizationScale;
                var point=new POINT {X=bounds.Left+(int)Math.Round(p.X*scale),Y=bounds.Top+(int)Math.Round(p.Y*scale)};
                if(_trayActionPending)return;
                int generation=_generation;
                if(context){_trayActionPending=true;WindowStyler.SetTopmost(PopupHandle,false);}
                else Close(immediately:true);
                try
                {
                    if(!await DockApp.Current.ActivateTrayItemAsync(item,context,point))DockApp.Current.Notify(L.Text("System tray"),L.Text("Tray icon is no longer available."),true);
                    if(context&&generation==_generation&&ActiveKind=="tray")_trayContextMenu=SystemTrayOverflow.LastContextMenuHandle;
                }
                finally
                {
                    if(context){_trayActionPending=false;_lastForeground=NativeMethods.GetForegroundWindow();if(!TrayContextActive&&SurfaceVisible)WindowStyler.SetTopmost(PopupHandle,!CaptureActive);}
                }
            }
            button.Click+=async(_,_)=>await Activate(false);
            button.RightTapped+=async(_,e)=>{e.Handled=true;await Activate(true);};
            Grid.SetRow(button,i/6);Grid.SetColumn(button,i%6);grid.Children.Add(button);
        }
        panel.Children.Add(grid);
        panel.Children.Add(Text(loading?"Loading tray icons…":items.Count==0?"No tray icons available. Keep Explorer running.":"Click to open · Right-click for options",12,true));
        if(!loading){_trayPanel=panel;_trayPanelItems=items;_trayPanelLanguage=DockApp.Current.Settings.Current.Language;}
        Show(anchor,panel,"tray",replace:ActiveKind=="tray");
    }
    internal void Sound(Button anchor)
    {
        if(ActiveKind=="sound") { Close();return; }
        var panel=Panel();panel.Children.Add(Header(L.Text("Sound"),"\uE767"));
        var number=Text("—",27);number.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;
        panel.Children.Add(number);panel.Children.Add(Text(L.Text("Output device volume"),12,true));
        var slider=new Slider { Minimum=0,Maximum=100,StepFrequency=1,SmallChange=1,LargeChange=10,HorizontalAlignment=HorizontalAlignment.Stretch };
        AutomationProperties.SetName(slider,L.Text("Volume"));panel.Children.Add(slider);
        var mute=new ToggleButton { Content=L.Text("Mute"),CornerRadius=new CornerRadius(20),Height=40,HorizontalAlignment=HorizontalAlignment.Stretch,BorderThickness=new Thickness(0) };
        {
            // Unchecked = tonal, checked (muted) = filled primary.
            var c=M3.Current;
            foreach(var s in new[]{"","PointerOver","Pressed"}){mute.Resources["ToggleButtonBackground"+s]=M3.Brush(c.SecondaryContainer);mute.Resources["ToggleButtonForeground"+s]=M3.Brush(c.OnSecondaryContainer);
                mute.Resources["ToggleButtonBackgroundChecked"+s]=M3.Brush(c.Primary);mute.Resources["ToggleButtonForegroundChecked"+s]=M3.Brush(c.OnPrimary);
                mute.Resources["ToggleButtonBorderBrush"+s]=M3.Clear;mute.Resources["ToggleButtonBorderBrushChecked"+s]=M3.Clear;}
        }
        panel.Children.Add(mute);var status=Text("",12,true);panel.Children.Add(status);
        bool syncing=false, pointerOver=false;
        slider.PointerEntered+=(_,_)=>pointerOver=true;slider.PointerExited+=(_,_)=>pointerOver=false;
        void Read()
        {
            try { var state=SystemAudio.Read();syncing=true;slider.Value=Math.Round(state.Volume*100);mute.IsChecked=state.Muted;number.Text=$"{Math.Round(state.Volume*100)}%";mute.Content=state.Muted?L.Text("Unmute"):L.Text("Mute");slider.IsEnabled=mute.IsEnabled=true;status.Text=""; }
            catch(Exception) { slider.IsEnabled=mute.IsEnabled=false;status.Text=L.Text("Output device unavailable."); }
            finally { syncing=false; }
        }
        Read();
        slider.ValueChanged+=(_,_)=> { if(syncing||ActiveKind!="sound")return;try { SystemAudio.SetVolume((float)slider.Value/100);number.Text=$"{slider.Value:0}%"; }catch(Exception){status.Text=L.Text("Could not change volume.");} };
        void MuteChanged(object sender,RoutedEventArgs args) { if(syncing||ActiveKind!="sound")return;try { SystemAudio.SetMuted(mute.IsChecked==true);Read(); }catch(Exception){status.Text=L.Text("Could not toggle mute.");} }
        mute.Checked+=MuteChanged;mute.Unchecked+=MuteChanged;
        panel.Children.Add(Divider());panel.Children.Add(Row(L.Text("Sound devices"),L.Text("More settings"),"\uE713",()=> { Close();ShellLauncher.Execute("ms-settings:sound",null,null); }));
        Show(anchor,panel,"sound");VolumeSlider=slider;MuteButton=mute;
        // Detach the preceding menu's closure; no timer survives light-dismiss.
        if(_audioTick is not null)_audioTimer.Tick-=_audioTick;
        _audioTick=(_,_)=> { if(ActiveKind=="sound"&&!pointerOver)Read(); };_audioTimer.Tick+=_audioTick;_audioTimer.Start();
    }
    private global::Windows.Foundation.TypedEventHandler<DispatcherQueueTimer,object>? _audioTick;
    internal void Language(Button anchor,IntPtr? diagnosticTarget=null)
    {
        if(ActiveKind=="language") { Close();return; }
        var target=diagnosticTarget??NativeMethods.GetForegroundWindow();LanguageTarget=target;
        if(diagnosticTarget is null&&(target==PopupHandle||NativeMethods.GetTitle(target)=="FloatingDock menu bar"))
            target=new IntPtr(DockApp.Current.Running.ForegroundRoot);
        LanguageTarget=target;
        uint thread=NativeMethods.GetWindowThreadProcessId(InputLanguages.InputSite(target),out _);
        var current=InputLanguages.GetKeyboardLayout(thread);
        var panel=Panel();panel.Children.Add(Header(L.Text("Input language"),"\uE8C1"));panel.Children.Add(Text(L.Text("Current application keyboard layout"),12,true));
        var rows=new StackPanel { Spacing=4 };var buttons=new List<Button>();
        foreach(var language in InputLanguages.Installed())
        {
            var row=Row(language.Name,language.Tag,"\uE765",()=> { _returnFocus=target;Close(immediately:true);NativeMethods.SetForegroundWindow(target);if(!InputLanguages.Request(target,language.Handle))DockApp.Current.Notify(L.Text("Input language"),L.Text("The application did not accept the keyboard layout."),true); },language.Handle==current);
            rows.Children.Add(row);buttons.Add(row);
        }
        if(buttons.Count==0)rows.Children.Add(Text(L.Text("No keyboard layouts available"),13,true));
        panel.Children.Add(new ScrollViewer { Content=rows,MaxHeight=300,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        panel.Children.Add(Divider());panel.Children.Add(Text(L.Text("You can also switch using Win + Space"),12,true));
        Show(anchor,panel,"language");LanguageButtons=buttons;
    }
    internal void Network(Button anchor)
    {
        if(ActiveKind=="network") { Close();return; }
        NetworkReady=false;NetworkSummary="";
        int generation=-1;var panel=Panel();panel.Children.Add(Header(L.Text("Network"),"\uE701"));
        var state=Text(L.Text("Checking connection…"),13,true);panel.Children.Add(state);
        var list=new StackPanel { Spacing=5 };panel.Children.Add(new ScrollViewer { Content=list,MaxHeight=320,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        var status=Text("",12,true);panel.Children.Add(status);panel.Children.Add(Divider());
        panel.Children.Add(Row(L.Text("Refresh networks"),L.Text("Find nearby Wi-Fi"),"\uE72C",()=>_=Refresh(true)));
        panel.Children.Add(Row(L.Text("Windows networks"),L.Text("Connect to a new network with a password"),"\uE774",()=> { Close();ShellLauncher.Execute("ms-availablenetworks:",null,null); }));
        Show(anchor,panel,"network");generation=_generation;_=Refresh(true);
        async Task Refresh(bool scan)
        {
            status.Text=L.Text("Refreshing…");
            try
            {
                var result=await Task.Run(()=>
                {
                    var adapters=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback&&n.NetworkInterfaceType!=NetworkInterfaceType.Tunnel).Select(n=>new { n.Name,Kind=n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211?"Wi-Fi":"Ethernet",Address=n.GetIPProperties().UnicastAddresses.FirstOrDefault(a=>a.Address.AddressFamily==AddressFamily.InterNetwork)?.Address.ToString() }).Where(n=>n.Address is not null).ToArray();
                    return (adapters,wireless:WirelessNetworks.Read(scan));
                });
                if(generation!=_generation||ActiveKind!="network")return;
                list.Children.Clear();state.Text=result.adapters.Length>0?L.Text("Connected"):L.Text("No active connection");
                foreach(var a in result.adapters)
                {
                    var info=new StackPanel { Spacing=4,Margin=new Thickness(10) };
                    var adapterName=Text(a.Kind+" · "+a.Name);adapterName.Foreground=M3.Brush(M3.Current.OnSecondaryContainer);info.Children.Add(adapterName);
                    list.Children.Add(new Border { Child=info,Background=M3.Brush(M3.Current.SecondaryContainer),CornerRadius=new CornerRadius(M3.CornerL) });
                }
                if(result.wireless.Networks.Count>0) { list.Children.Add(Divider());var wifi=Text("WI-FI",12);wifi.Foreground=M3.Brush(M3.Current.Primary);wifi.FontWeight=new global::Windows.UI.Text.FontWeight { Weight=600 };wifi.Margin=new Thickness(4,0,0,0);list.Children.Add(wifi); }
                foreach(var network in result.wireless.Networks)
                {
                    string detail=!network.Available?L.Text("Saved network · connect"):network.Connected?L.Text("Connected · ")+network.Signal+"%":(network.Secured?L.Text("Secured network"):L.Text("Open network"))+" · "+network.Signal+"%";
                    list.Children.Add(Row(network.Name,detail,"\uE701",async ()=>
                    {
                        if(!network.Connected&&network.Profile.Length==0) { Close();ShellLauncher.Execute("ms-availablenetworks:",null,null);return; }
                        status.Text=network.Connected?L.Text("Disconnecting…"):L.Text("Connecting…");
                        try { await Task.Run(()=> { if(network.Connected)WirelessNetworks.Disconnect(network);else WirelessNetworks.ConnectSaved(network); });await Task.Delay(1200);if(generation==_generation)await Refresh(false); }
                        catch(Exception ex) { if(generation==_generation)status.Text=L.Text("Failed: ")+ex.Message; }
                    },network.Connected));
                }
                if(result.wireless.Networks.Count==0&&result.wireless.Error is null)list.Children.Add(Text(L.Text("No Wi-Fi networks found. If scanning is still running, choose Refresh networks."),12,true));
                status.Text=result.wireless.Error??"";NetworkSummary=state.Text;NetworkReady=true;_resizePopup?.Invoke();
                if(result.wireless.Error is not null&&result.wireless.Networks.All(n=>!n.Available))
                    list.Children.Add(Row(L.Text("Allow Wi-Fi scanning"),L.Text("Access nearby network list"),"\uE72E",async ()=>
                    {
                        status.Text=L.Text("Requesting Windows permission…");
                        try
                        {
                            var access=await global::Windows.Devices.WiFi.WiFiAdapter.RequestAccessAsync();
                            if(access==global::Windows.Devices.WiFi.WiFiAccessStatus.Allowed)await Refresh(true);
                            else { status.Text=L.Text("Windows requires location access for Wi-Fi scanning.");ShellLauncher.Execute("ms-settings:privacy-location",null,null); }
                        }
                        catch(Exception) { status.Text=L.Text("Allow Wi-Fi scanning in Windows location settings.");ShellLauncher.Execute("ms-settings:privacy-location",null,null); }
                    }));
            }
            catch(Exception ex) { if(generation==_generation) { status.Text=L.Text("Could not get networks: ")+ex.Message;NetworkSummary=status.Text;NetworkReady=true;_resizePopup?.Invoke(); } }
        }
    }
    internal void Calendar(Button anchor)
    {
        if(ActiveKind=="calendar") { Close();return; }
        var panel=Panel();panel.Children.Add(Header(L.Text("Calendar"),"\uE787"));panel.Children.Add(Text(DateTime.Now.ToString("D"),12,true));
        panel.Children.Add(new CalendarView { Width=300,SelectionMode=CalendarViewSelectionMode.None,RequestedTheme=M3.ElementTheme,CornerRadius=new CornerRadius(M3.CornerL) });
        panel.Children.Add(Divider());panel.Children.Add(Row(L.Text("Date and time"),L.Text("More settings"),"\uE713",()=> { Close();ShellLauncher.Execute("ms-settings:dateandtime",null,null); }));
        Show(anchor,panel,"calendar");
    }
    public void Dispose() { DockApp.Current.Running.NativeWindowChanged-=OnForegroundChanged;Close(immediately:true);_motion?.Dispose();if(_audioTick is not null)_audioTimer.Tick-=_audioTick;_popupWindow?.Close();_popupWindow=null; }
}



