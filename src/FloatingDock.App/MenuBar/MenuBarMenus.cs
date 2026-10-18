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
    private readonly GlassBackdrop _glass=new();
    private IntPtr _lastForeground;
    private IntPtr _returnFocus;
    private Window? _popupWindow;
    private IntPtr _popupHwnd;
    private Border? _popupRoot;
    private ScrollViewer? _scroll;
    private StackPanel? _mainPanel;
    private (FloatingDock.Core.Configuration.InterfaceLanguage Language, bool AutoHide)? _mainState;
    internal int MainContentBuildsForDiagnostics { get; private set; }
    internal ScrollViewer? ScrollForDiagnostics=>_scroll;
    private bool _showing;
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
        _glass.SetVisible(false);
        _audioTimer.Interval=TimeSpan.FromMilliseconds(700);
        _dismissTimer=DockApp.Current.UiQueue.CreateTimer();_dismissTimer.Interval=TimeSpan.FromMilliseconds(100);
        _dismissTimer.Tick+=(_,_)=>
        {
            var foreground=NativeMethods.GetForegroundWindow();
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
        if(!CaptureActive&&!Opening&&kind==FloatingDock.Windows.RunningApps.WindowEventKind.Foreground&&ActiveKind is not null&&!_showing&&NativeMethods.GetForegroundWindow()!=PopupHandle&&!PointerOverAnchor())Close();
    }
    private static SolidColorBrush Brush(byte a,byte r,byte g,byte b)=>new(ColorHelper.FromArgb(a,r,g,b));
    private static TextBlock Text(string text,double size=13,bool secondary=false)=>new() { Text=L.Text(text),FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=secondary?Brush(255,169,177,192):Brush(255,246,247,251) };
    private static StackPanel Panel()=>new() { Width=300,Spacing=12 };
    private static Grid Header(string title,string glyph)
    {
        var grid=new Grid { Margin=new Thickness(2,0,2,4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph=glyph,FontSize=18,Margin=new Thickness(0,0,10,0),Foreground=Brush(255,164,199,255) });
        var label=Text(title,17);label.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;Grid.SetColumn(label,1);grid.Children.Add(label);return grid;
    }
    private static Border Divider()=>new() { Height=1,Background=Brush(24,255,255,255),Margin=new Thickness(0,3,0,3) };
    private static Button Row(string title,string detail,string glyph,Action action,bool selected=false)
    {
        var row=new Grid { ColumnSpacing=12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph=glyph,FontSize=17,Width=24,VerticalAlignment=VerticalAlignment.Center,Foreground=Brush(255,190,203,226) });
        var text=new StackPanel { Spacing=3 };text.Children.Add(Text(title));if(detail.Length>0)text.Children.Add(Text(detail,12,true));Grid.SetColumn(text,1);row.Children.Add(text);
        if(selected) { var check=new FontIcon { Glyph="\uE73E",FontSize=13,Foreground=Brush(255,149,196,255) };Grid.SetColumn(check,2);row.Children.Add(check); }
        var b=new Button { Content=row,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(10),CornerRadius=new CornerRadius(9),Background=selected?Brush(35,132,175,255):Brush(8,255,255,255),BorderThickness=new Thickness(0) };
        AutomationProperties.SetName(b,title+" "+detail);b.Click+=(_,_)=>action();return b;
    }
    private void Show(Button anchor,StackPanel content,string kind)
    {
        if(ActiveKind==kind) { Close();return; }
        var previous=NativeMethods.GetForegroundWindow();
        if(previous==PopupHandle)previous=_returnFocus;
        float progress=SurfaceVisible&&_anchor==anchor?(_motion?.Progress??0):0;
        Close(immediately:true);_returnFocus=previous;ActiveKind=kind;_anchor=anchor;_showing=true;
        _openingUntil=System.Diagnostics.Stopwatch.GetTimestamp()+System.Diagnostics.Stopwatch.Frequency*300/1000;
        try
        {
            if(_popupWindow is null)
            {
                _popupRoot=new Border { RequestedTheme=ElementTheme.Dark,Background=Brush(96,20,22,27),BorderBrush=Brush(26,255,255,255),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(16),Padding=new Thickness(16) };
                _scroll=new ScrollViewer { HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
                _popupRoot.Child=_scroll;
                _popupWindow=new Window { Title="FloatingDock quick menu",Content=_popupRoot,SystemBackdrop=_glass };
                _popupHwnd=WindowSetup.Hwnd(_popupWindow);
                WindowSetup.MakeBorderless(_popupWindow);WindowStyler.MakeOverlayWindow(PopupHandle,noActivate:false);
                _motion=new MenuSurfaceMotion(PopupHandle,_popupRoot,_glass);
                _popupWindow.Activated+=(_,e)=> { if(!CaptureActive&&!Opening&&!_showing&&e.WindowActivationState==WindowActivationState.Deactivated&&NativeMethods.GetForegroundWindow()!=PopupHandle&&!PointerOverAnchor())Close(); };
                _popupRoot.KeyDown+=(_,e)=> { if(e.Key==global::Windows.System.VirtualKey.Escape) { e.Handled=true;Close(); } };
            }
            var monitor=FloatingDock.Windows.Display.MonitorHelper.Describe(NativeMethods.MonitorFromWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(anchor.XamlRoot.ContentIslandEnvironment.AppWindowId),NativeMethods.MONITOR_DEFAULTTONEAREST))!;
            double maxHeight=Math.Max(160,(monitor.Bounds.Height-70)/monitor.Scale);
            _scroll!.MaxHeight=maxHeight-34;
            if(!ReferenceEquals(_scroll.Content,content))_scroll.Content=content;
            _scroll.ChangeView(0,0,null,true); // Reopening starts at the top, like a fresh popup.
            _popupRoot!.IsHitTestVisible=true;
            _popupRoot.Measure(new global::Windows.Foundation.Size(334,maxHeight));
            int width=(int)Math.Ceiling(334*monitor.Scale),height=(int)Math.Ceiling(_popupRoot.DesiredSize.Height*monitor.Scale);
            var point=anchor.TransformToVisual(anchor.XamlRoot.Content).TransformPoint(new global::Windows.Foundation.Point(anchor.ActualWidth,anchor.ActualHeight));
            NativeMethods.GetWindowRect(Microsoft.UI.Win32Interop.GetWindowFromWindowId(anchor.XamlRoot.ContentIslandEnvironment.AppWindowId),out var owner);
            int x=Math.Clamp(owner.Left+(int)((kind=="main"?point.X-anchor.ActualWidth:point.X)*monitor.Scale)-(kind=="main"?0:width),monitor.Bounds.Left+8,monitor.Bounds.Right-width-8);
            int y=owner.Bottom+(int)Math.Ceiling(6*monitor.Scale);
            WindowStyler.SetBounds(PopupHandle,x,y,width,Math.Max(height,100),topmost:!CaptureActive);
            _glass.SetVisible(true);SurfaceVisible=true;
            // Clip the actual glass HWND to the same anchored transform as its XAML plate.
            double pivot=(owner.Left+(point.X-anchor.ActualWidth/2)*monitor.Scale-x)/monitor.Scale;
            _motion!.Prepare(SurfaceMotion.Enabled?progress:1);
            _motion.SetGeometry(width,Math.Max(height,100),monitor.Scale,pivot);
            int duration=Math.Clamp((int)(DockApp.Current.Settings.Current.MenuAnimationMs*1.4),160,600);
            // The measured native geometry is enough to start even on the first open.
            // Waiting for Loaded left a completely transparent window before its first frame.
            SurfaceMotion.MenuContent(content,duration,entering:true);
            _motion.Play(entering:true,duration);
            _opened(true);_popupWindow.Activate();WindowStyler.BringToFront(PopupHandle);
            _lastForeground=NativeMethods.GetForegroundWindow();_dismissTimer.Start();
            int generation=_generation;
            _resizePopup=()=>DockApp.Current.UiQueue.TryEnqueue(()=>
            {
                if(generation!=_generation||ActiveKind!=kind)return;
                _popupRoot.UpdateLayout();content.Measure(new global::Windows.Foundation.Size(300,double.PositiveInfinity));
                int finalHeight=(int)Math.Ceiling(Math.Min(maxHeight,content.DesiredSize.Height+34)*monitor.Scale);
                WindowStyler.SetBounds(PopupHandle,x,y,width,Math.Max(finalHeight,100),topmost:!CaptureActive);
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
        _glass.SetVisible(false);
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
        if(!CaptureActive&&SurfaceVisible&&NativeMethods.IsWindowVisible(PopupHandle))
            NativeMethods.SetWindowPos(PopupHandle,NativeMethods.HWND_TOPMOST,0,0,0,0,NativeMethods.SWP_NOMOVE|NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE);
    }
    internal void Main(Button anchor)
    {
        if(ActiveKind=="main") { Close();return; }
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
        var panel=Panel();panel.Children.Add(Header("Media","\uE8D6"));
        var title=Text("Searching…",17);title.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;
        var artist=Text("",13,true);
        panel.Children.Add(title);panel.Children.Add(artist);
        var controls=new StackPanel { Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Center };
        Button Button(string glyph,string label)
        {
            var button=new Button { Content=new FontIcon { Glyph=glyph,FontSize=20 },Width=64,Height=44,CornerRadius=new CornerRadius(12),IsEnabled=false };
            AutomationProperties.SetName(button,L.Text(label));ToolTipService.SetToolTip(button,L.Text(label));controls.Children.Add(button);return button;
        }
        var previous=Button("\uE892","Previous");var toggle=Button("\uE768","Play / pause");var next=Button("\uE893","Next");panel.Children.Add(controls);
        var status=Text("",12,true);panel.Children.Add(status);Show(anchor,panel,"media");
        int generation=_generation;bool reading=false,acting=false;
        MediaReady=false;MediaButtons=new[]{previous,toggle,next};
        bool Current()=>ActiveKind=="media"&&generation==_generation;
        async Task Read()
        {
            if(reading||acting||!Current())return;reading=true;
            try
            {
                var state=await MediaSource.ReadAsync();if(!Current())return;
                title.Text=state is null?L.Text("No active media session"):state.Title;
                artist.Text=state?.Artist??"";
                previous.IsEnabled=state?.Previous==true;toggle.IsEnabled=state?.Toggle==true;next.IsEnabled=state?.Next==true;
                ((FontIcon)toggle.Content).Glyph=state?.Playing==true?"\uE769":"\uE768";status.Text="";
            }
            catch(Exception) { if(Current()){title.Text=L.Text("Media controls are unavailable.");previous.IsEnabled=toggle.IsEnabled=next.IsEnabled=false;} }
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
    internal void Tray(Button anchor,IReadOnlyList<TrayItem> items)
    {
        TrayItems=items;var panel=Panel();panel.Children.Add(Header("System tray","\uE70D"));
        var grid=new Grid { ColumnSpacing=8,RowSpacing=8 };
        for(int c=0;c<6;c++)grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        for(int r=0;r<(items.Count+5)/6;r++)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        for(int i=0;i<items.Count;i++)
        {
            var item=items[i];var button=new Button { Width=42,Height=42,Padding=new Thickness(9),CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(0),Background=Brush(10,255,255,255),Content=new FontIcon { Glyph="\uE71D",FontSize=20 } };
            ToolTipService.SetToolTip(button,item.Name);AutomationProperties.SetName(button,item.Name);
            if(item.Icon is not null)_=LoadIcon();
            async Task LoadIcon()
            {
                try
                {
                    using var stream=new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using(var writer=new global::Windows.Storage.Streams.DataWriter(stream)){writer.WriteBytes(item.Icon!);await writer.StoreAsync();writer.DetachStream();}
                    stream.Seek(0);var bitmap=new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();await bitmap.SetSourceAsync(stream);
                    button.Content=new Image { Source=bitmap,Width=24,Height=24,Stretch=Stretch.Uniform };
                }
                catch(Exception){}
            }
            async Task Activate(bool context)
            {
                var p=button.TransformToVisual(_popupRoot).TransformPoint(new(button.ActualWidth/2,button.ActualHeight/2));
                NativeMethods.GetWindowRect(PopupHandle,out var bounds);
                double scale=button.XamlRoot.RasterizationScale;
                var point=new POINT {X=bounds.Left+(int)Math.Round(p.X*scale),Y=bounds.Top+(int)Math.Round(p.Y*scale)};
                Close(immediately:true);
                if(!await DockApp.Current.ActivateTrayItemAsync(item,context,point))DockApp.Current.Notify(L.Text("System tray"),L.Text("Tray icon is no longer available."),true);
            }
            button.Click+=async(_,_)=>await Activate(false);
            button.RightTapped+=async(_,e)=>{e.Handled=true;await Activate(true);};
            Grid.SetRow(button,i/6);Grid.SetColumn(button,i%6);grid.Children.Add(button);
        }
        panel.Children.Add(grid);
        panel.Children.Add(Text(items.Count==0?"No tray icons available. Keep Explorer running.":"Click to open · Right-click for options",12,true));
        Show(anchor,panel,"tray");
    }
    internal void Sound(Button anchor)
    {
        if(ActiveKind=="sound") { Close();return; }
        var panel=Panel();panel.Children.Add(Header(L.Text("Sound"),"\uE767"));
        var number=Text("—",27);number.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;
        panel.Children.Add(number);panel.Children.Add(Text(L.Text("Output device volume"),12,true));
        var slider=new Slider { Minimum=0,Maximum=100,StepFrequency=1,SmallChange=1,LargeChange=10,HorizontalAlignment=HorizontalAlignment.Stretch };
        AutomationProperties.SetName(slider,L.Text("Volume"));panel.Children.Add(slider);
        var mute=new ToggleButton { Content=L.Text("Mute"),CornerRadius=new CornerRadius(8),HorizontalAlignment=HorizontalAlignment.Stretch };
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
                    info.Children.Add(Text(a.Kind+" · "+a.Name));
                    list.Children.Add(new Border { Child=info,Background=Brush(24,132,175,255),CornerRadius=new CornerRadius(9) });
                }
                if(result.wireless.Networks.Count>0) { list.Children.Add(Divider());list.Children.Add(Text("WI-FI",11,true)); }
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
        panel.Children.Add(new CalendarView { Width=300,SelectionMode=CalendarViewSelectionMode.None,RequestedTheme=ElementTheme.Dark });
        panel.Children.Add(Divider());panel.Children.Add(Row(L.Text("Date and time"),L.Text("More settings"),"\uE713",()=> { Close();ShellLauncher.Execute("ms-settings:dateandtime",null,null); }));
        Show(anchor,panel,"calendar");
    }
    public void Dispose() { DockApp.Current.Running.NativeWindowChanged-=OnForegroundChanged;Close(immediately:true);_motion?.Dispose();if(_audioTick is not null)_audioTimer.Tick-=_audioTick;_popupWindow?.Close();_popupWindow=null; }
}



