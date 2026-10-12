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
    private readonly DispatcherQueueTimer _closeTimer;
    private readonly GlassBackdrop _glass=new();
    private IntPtr _lastForeground;
    private IntPtr _returnFocus;
    private Window? _popupWindow;
    private Border? _popupRoot;
    private bool _showing;
    private Button? _anchor;
    private Action? _resizePopup;
    internal IntPtr PopupHandle => _popupWindow is null?IntPtr.Zero:WindowSetup.Hwnd(_popupWindow);
    private int _generation;
    internal bool SurfaceVisible { get; private set; }
    internal string? ActiveKind { get; private set; }
    internal Slider? VolumeSlider { get; private set; }
    internal ToggleButton? MuteButton { get; private set; }
    internal IReadOnlyList<Button> LanguageButtons { get; private set; } = Array.Empty<Button>();
    internal IntPtr LanguageTarget { get; private set; }
    internal bool NetworkReady { get; private set; }
    internal string NetworkSummary { get; private set; } = "";
    internal MenuBarMenus(Action<bool> opened)
    {
        _opened=opened;_audioTimer=DockApp.Current.UiQueue.CreateTimer();
        _closeTimer=DockApp.Current.UiQueue.CreateTimer();_closeTimer.IsRepeating=false;
        _closeTimer.Interval=TimeSpan.FromMilliseconds(150);
        _closeTimer.Tick+=(_,_)=> { if(ActiveKind is null)HidePopup(); };
        _audioTimer.Interval=TimeSpan.FromMilliseconds(700);
        _dismissTimer=DockApp.Current.UiQueue.CreateTimer();_dismissTimer.Interval=TimeSpan.FromMilliseconds(100);
        _dismissTimer.Tick+=(_,_)=>
        {
            var foreground=NativeMethods.GetForegroundWindow();
            bool pressed=(GetAsyncKeyState(1)&0x8000)!=0||(GetAsyncKeyState(2)&0x8000)!=0;
            bool inPopup=NativeMethods.GetCursorPos(out var p)&&NativeMethods.GetWindowRect(PopupHandle,out var r)&&p.X>=r.Left&&p.X<r.Right&&p.Y>=r.Top&&p.Y<r.Bottom;
            if(!_showing&&ActiveKind is not null&&!PointerOverAnchor()&&((foreground!=_lastForeground&&foreground!=PopupHandle)||(pressed&&!inPopup)))Close();
            _lastForeground=foreground;
        };
        DockApp.Current.Running.NativeWindowChanged+=OnForegroundChanged;
    }
    private void OnForegroundChanged(FloatingDock.Windows.RunningApps.WindowEventKind kind,IntPtr hwnd)
    {
        if(kind==FloatingDock.Windows.RunningApps.WindowEventKind.Foreground&&ActiveKind is not null&&!_showing&&NativeMethods.GetForegroundWindow()!=PopupHandle&&!PointerOverAnchor())Close();
    }
    private static SolidColorBrush Brush(byte a,byte r,byte g,byte b)=>new(ColorHelper.FromArgb(a,r,g,b));
    private static TextBlock Text(string text,double size=13,bool secondary=false)=>new() { Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=secondary?Brush(255,169,177,192):Brush(255,246,247,251) };
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
        Close(immediately:true);_returnFocus=previous;ActiveKind=kind;_anchor=anchor;_showing=true;
        try
        {
            if(_popupWindow is null)
            {
                _popupRoot=new Border { RequestedTheme=ElementTheme.Dark,Background=Brush(96,20,22,27),BorderBrush=Brush(26,255,255,255),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(16),Padding=new Thickness(16) };
                _popupWindow=new Window { Title="FloatingDock quick menu",Content=_popupRoot,SystemBackdrop=_glass };
                WindowSetup.MakeBorderless(_popupWindow);WindowStyler.MakeOverlayWindow(PopupHandle,noActivate:false);
                int corner=2;NativeMethods.DwmSetWindowAttribute(PopupHandle,33,ref corner,4);
                _popupWindow.Activated+=(_,e)=> { if(!_showing&&e.WindowActivationState==WindowActivationState.Deactivated&&NativeMethods.GetForegroundWindow()!=PopupHandle&&!PointerOverAnchor())Close(); };
                _popupRoot.KeyDown+=(_,e)=> { if(e.Key==global::Windows.System.VirtualKey.Escape) { e.Handled=true;Close(); } };
            }
            var monitor=FloatingDock.Windows.Display.MonitorHelper.Describe(NativeMethods.MonitorFromWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(anchor.XamlRoot.ContentIslandEnvironment.AppWindowId),NativeMethods.MONITOR_DEFAULTTONEAREST))!;
            double maxHeight=Math.Max(160,(monitor.Bounds.Height-70)/monitor.Scale);
            _popupRoot!.Child=new ScrollViewer { Content=content,MaxHeight=maxHeight-34,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            _popupRoot.IsHitTestVisible=true;
            _popupRoot.Measure(new global::Windows.Foundation.Size(334,maxHeight));
            int width=(int)Math.Ceiling(334*monitor.Scale),height=(int)Math.Ceiling(_popupRoot.DesiredSize.Height*monitor.Scale);
            var point=anchor.TransformToVisual(anchor.XamlRoot.Content).TransformPoint(new global::Windows.Foundation.Point(anchor.ActualWidth,anchor.ActualHeight));
            NativeMethods.GetWindowRect(Microsoft.UI.Win32Interop.GetWindowFromWindowId(anchor.XamlRoot.ContentIslandEnvironment.AppWindowId),out var owner);
            int x=Math.Clamp(owner.Left+(int)((kind=="main"?point.X-anchor.ActualWidth:point.X)*monitor.Scale)-(kind=="main"?0:width),monitor.Bounds.Left+8,monitor.Bounds.Right-width-8);
            int y=owner.Bottom+(int)Math.Ceiling(6*monitor.Scale);
            WindowStyler.SetBounds(PopupHandle,x,y,width,Math.Max(height,100));
            _glass.SetVisible(true);SurfaceVisible=true;
            _opened(true);_popupWindow.Activate();WindowStyler.BringToFront(PopupHandle);
            SurfaceMotion.Enter(_popupRoot,200);
            _glass.AnimateOpacity(1,200,entering:true);
            _lastForeground=NativeMethods.GetForegroundWindow();_dismissTimer.Start();
            int generation=_generation;
            _resizePopup=()=>DockApp.Current.UiQueue.TryEnqueue(()=>
            {
                if(generation!=_generation||ActiveKind!=kind)return;
                _popupRoot.UpdateLayout();content.Measure(new global::Windows.Foundation.Size(300,double.PositiveInfinity));
                int finalHeight=(int)Math.Ceiling(Math.Min(maxHeight,content.DesiredSize.Height+34)*monitor.Scale);
                WindowStyler.SetBounds(PopupHandle,x,y,width,Math.Max(finalHeight,100));
            });
            _resizePopup();

        }
        finally { _showing=false; }
    }
    internal void Close(bool immediately=false)
    {
        if(ActiveKind is null&&!immediately)return;
        ActiveKind=null;_generation++;_audioTimer.Stop();_dismissTimer.Stop();_resizePopup=null;
        _closeTimer.Stop();
        VolumeSlider=null;MuteButton=null;LanguageButtons=Array.Empty<Button>();
        if(_popupRoot is not null)_popupRoot.IsHitTestVisible=false;
        if(_popupWindow is not null)
        {
            if(immediately||!SurfaceMotion.Enabled)HidePopup();
            else
            {
                SurfaceMotion.Animate(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_popupRoot!),0,.985f,130);
                _glass.AnimateOpacity(0,130);
                // Native input callbacks do not necessarily carry a SynchronizationContext.
                // Finish on the owning dispatcher, never on Task.Delay's worker continuation.
                _closeTimer.Start();
            }
        }
        else _opened(false);
    }
    private void HidePopup()
    {
        _closeTimer.Stop();
        bool focused=NativeMethods.GetForegroundWindow()==PopupHandle;
        _popupWindow?.AppWindow.Hide();
        WindowStyler.Hide(PopupHandle);
        _glass.SetVisible(false);
        if(_popupRoot is not null)
        {
            SurfaceMotion.Reset(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_popupRoot),0);
            _popupRoot.Child=null;
        }
        SurfaceVisible=false;_opened(false);
        // Restore the app underneath only when this menu still owns focus. An outside
        // click, screenshot overlay, or another application's activation wins instead.
        if(focused&&NativeMethods.IsWindow(_returnFocus)&&NativeMethods.IsWindowVisible(_returnFocus)&&!NativeMethods.IsIconic(_returnFocus))NativeMethods.SetForegroundWindow(_returnFocus);
    }
    internal void Reassert()
    {
        if(SurfaceVisible&&NativeMethods.IsWindowVisible(PopupHandle))
            NativeMethods.SetWindowPos(PopupHandle,NativeMethods.HWND_TOPMOST,0,0,0,0,NativeMethods.SWP_NOMOVE|NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE);
    }
    internal void Main(Button anchor)
    {
        if(ActiveKind=="main") { Close();return; }
        var app=DockApp.Current;var panel=Panel();panel.Spacing=5;
        void Add(string name,string glyph,Action action,bool selected=false)=>panel.Children.Add(Row(name,"",glyph,()=> { Close(immediately:true);action(); },selected));
        Add("Launchpad","\uE80A",app.ToggleLaunchpad);
        Add("Настройки дока","\uE713",()=>app.ShowSettings());
        panel.Children.Add(Divider());
        Add("Автоскрытие верхней панели","\uE890",()=>app.Settings.Update(s=>s.TopBarAutoHide=!s.TopBarAutoHide),app.Settings.Current.TopBarAutoHide);
        Add("Скрыть верхнюю панель","\uE8F4",()=>app.Settings.Update(s=>s.TopBarEnabled=false));
        panel.Children.Add(Divider());Add("Выйти из FloatingDock","\uE7E8",app.Shutdown);
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
    internal void Sound(Button anchor)
    {
        if(ActiveKind=="sound") { Close();return; }
        var panel=Panel();panel.Children.Add(Header("Звук","\uE767"));
        var number=Text("—",27);number.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;
        panel.Children.Add(number);panel.Children.Add(Text("Громкость устройства вывода",12,true));
        var slider=new Slider { Minimum=0,Maximum=100,StepFrequency=1,SmallChange=1,LargeChange=10,HorizontalAlignment=HorizontalAlignment.Stretch };
        AutomationProperties.SetName(slider,"Громкость");panel.Children.Add(slider);
        var mute=new ToggleButton { Content="Выключить звук",CornerRadius=new CornerRadius(8),HorizontalAlignment=HorizontalAlignment.Stretch };
        panel.Children.Add(mute);var status=Text("",12,true);panel.Children.Add(status);
        bool syncing=false, pointerOver=false;
        slider.PointerEntered+=(_,_)=>pointerOver=true;slider.PointerExited+=(_,_)=>pointerOver=false;
        void Read()
        {
            try { var state=SystemAudio.Read();syncing=true;slider.Value=Math.Round(state.Volume*100);mute.IsChecked=state.Muted;number.Text=$"{Math.Round(state.Volume*100)}%";mute.Content=state.Muted?"Включить звук":"Выключить звук";slider.IsEnabled=mute.IsEnabled=true;status.Text=""; }
            catch(Exception) { slider.IsEnabled=mute.IsEnabled=false;status.Text="Устройство вывода звука недоступно."; }
            finally { syncing=false; }
        }
        Read();
        slider.ValueChanged+=(_,_)=> { if(syncing||ActiveKind!="sound")return;try { SystemAudio.SetVolume((float)slider.Value/100);number.Text=$"{slider.Value:0}%"; }catch(Exception){status.Text="Не удалось изменить громкость.";} };
        void MuteChanged(object sender,RoutedEventArgs args) { if(syncing||ActiveKind!="sound")return;try { SystemAudio.SetMuted(mute.IsChecked==true);Read(); }catch(Exception){status.Text="Не удалось переключить звук.";} }
        mute.Checked+=MuteChanged;mute.Unchecked+=MuteChanged;
        panel.Children.Add(Divider());panel.Children.Add(Row("Устройства звука","Дополнительные настройки","\uE713",()=> { Close();ShellLauncher.Execute("ms-settings:sound",null,null); }));
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
        uint thread=NativeMethods.GetWindowThreadProcessId(target,out _);
        var current=InputLanguages.GetKeyboardLayout(thread);
        var panel=Panel();panel.Children.Add(Header("Язык ввода","\uE8C1"));panel.Children.Add(Text("Раскладка текущего приложения",12,true));
        var rows=new StackPanel { Spacing=4 };var buttons=new List<Button>();
        foreach(var language in InputLanguages.Installed())
        {
            var row=Row(language.Name,language.Tag,"\uE765",()=> { Close();NativeMethods.SetForegroundWindow(target);if(!InputLanguages.Request(target,language.Handle))DockApp.Current.Notify("Язык ввода","Приложение не приняло смену раскладки.",true); },language.Handle==current);
            rows.Children.Add(row);buttons.Add(row);
        }
        if(buttons.Count==0)rows.Children.Add(Text("Нет доступных раскладок",13,true));
        panel.Children.Add(new ScrollViewer { Content=rows,MaxHeight=300,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        panel.Children.Add(Divider());panel.Children.Add(Text("Переключение также доступно через Win + Пробел",12,true));
        Show(anchor,panel,"language");LanguageButtons=buttons;
    }
    internal void Network(Button anchor)
    {
        if(ActiveKind=="network") { Close();return; }
        NetworkReady=false;NetworkSummary="";
        int generation=-1;var panel=Panel();panel.Children.Add(Header("Сеть","\uE701"));
        var state=Text("Проверяем подключение…",13,true);panel.Children.Add(state);
        var list=new StackPanel { Spacing=5 };panel.Children.Add(new ScrollViewer { Content=list,MaxHeight=320,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        var status=Text("",12,true);panel.Children.Add(status);panel.Children.Add(Divider());
        panel.Children.Add(Row("Обновить сети","Поиск Wi-Fi поблизости","\uE72C",()=>_=Refresh(true)));
        panel.Children.Add(Row("Все сети Windows","Подключить новую сеть с паролем","\uE774",()=> { Close();ShellLauncher.Execute("ms-availablenetworks:",null,null); }));
        Show(anchor,panel,"network");generation=_generation;_=Refresh(true);
        async Task Refresh(bool scan)
        {
            status.Text="Обновление…";
            try
            {
                var result=await Task.Run(()=>
                {
                    var adapters=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback&&n.NetworkInterfaceType!=NetworkInterfaceType.Tunnel).Select(n=>new { n.Name,Kind=n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211?"Wi-Fi":"Ethernet",Address=n.GetIPProperties().UnicastAddresses.FirstOrDefault(a=>a.Address.AddressFamily==AddressFamily.InterNetwork)?.Address.ToString() }).Where(n=>n.Address is not null).ToArray();
                    return (adapters,wireless:WirelessNetworks.Read(scan));
                });
                if(generation!=_generation||ActiveKind!="network")return;
                list.Children.Clear();state.Text=result.adapters.Length>0?"Подключение активно":"Нет активного подключения";
                foreach(var a in result.adapters)
                {
                    var info=new StackPanel { Spacing=4,Margin=new Thickness(10) };
                    info.Children.Add(Text(a.Kind+" · "+a.Name));
                    list.Children.Add(new Border { Child=info,Background=Brush(24,132,175,255),CornerRadius=new CornerRadius(9) });
                }
                if(result.wireless.Networks.Count>0) { list.Children.Add(Divider());list.Children.Add(Text("WI-FI",11,true)); }
                foreach(var network in result.wireless.Networks)
                {
                    string detail=!network.Available?"Сохранённая сеть · подключить":network.Connected?"Подключено · "+network.Signal+"%":(network.Secured?"Защищённая сеть":"Открытая сеть")+" · "+network.Signal+"%";
                    list.Children.Add(Row(network.Name,detail,"\uE701",async ()=>
                    {
                        if(!network.Connected&&network.Profile.Length==0) { Close();ShellLauncher.Execute("ms-availablenetworks:",null,null);return; }
                        status.Text=network.Connected?"Отключение…":"Подключение…";
                        try { await Task.Run(()=> { if(network.Connected)WirelessNetworks.Disconnect(network);else WirelessNetworks.ConnectSaved(network); });await Task.Delay(1200);if(generation==_generation)await Refresh(false); }
                        catch(Exception ex) { if(generation==_generation)status.Text="Не удалось: "+ex.Message; }
                    },network.Connected));
                }
                if(result.wireless.Networks.Count==0&&result.wireless.Error is null)list.Children.Add(Text("Доступных Wi-Fi сетей нет. Если поиск ещё идёт, нажмите «Обновить сети».",12,true));
                status.Text=result.wireless.Error??"";NetworkSummary=state.Text;NetworkReady=true;_resizePopup?.Invoke();
                if(result.wireless.Error is not null&&result.wireless.Networks.All(n=>!n.Available))
                    list.Children.Add(Row("Разрешить поиск Wi-Fi","Доступ к списку ближайших сетей","\uE72E",async ()=>
                    {
                        status.Text="Запрашиваем разрешение Windows…";
                        try
                        {
                            var access=await global::Windows.Devices.WiFi.WiFiAdapter.RequestAccessAsync();
                            if(access==global::Windows.Devices.WiFi.WiFiAccessStatus.Allowed)await Refresh(true);
                            else { status.Text="Windows требует включить доступ к местоположению для поиска Wi-Fi.";ShellLauncher.Execute("ms-settings:privacy-location",null,null); }
                        }
                        catch(Exception) { status.Text="Разрешите поиск Wi-Fi в параметрах местоположения Windows.";ShellLauncher.Execute("ms-settings:privacy-location",null,null); }
                    }));
            }
            catch(Exception ex) { if(generation==_generation) { status.Text="Не удалось получить сети: "+ex.Message;NetworkSummary=status.Text;NetworkReady=true;_resizePopup?.Invoke(); } }
        }
    }
    internal void Calendar(Button anchor)
    {
        if(ActiveKind=="calendar") { Close();return; }
        var panel=Panel();panel.Children.Add(Header("Календарь","\uE787"));panel.Children.Add(Text(DateTime.Now.ToString("D"),12,true));
        panel.Children.Add(new CalendarView { Width=300,SelectionMode=CalendarViewSelectionMode.None,RequestedTheme=ElementTheme.Dark });
        panel.Children.Add(Divider());panel.Children.Add(Row("Дата и время","Дополнительные настройки","\uE713",()=> { Close();ShellLauncher.Execute("ms-settings:dateandtime",null,null); }));
        Show(anchor,panel,"calendar");
    }
    public void Dispose() { DockApp.Current.Running.NativeWindowChanged-=OnForegroundChanged;Close(immediately:true);if(_audioTick is not null)_audioTimer.Tick-=_audioTick;_popupWindow?.Close();_popupWindow=null; }
}



