using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using FloatingDock.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Dock;

/// <summary>Live native thumbnails. Polls window lifetime only while a preview is visible; never captures frames.</summary>
internal sealed class DockWindowPreview : IDisposable
{
    internal const int HoverDelayMs=750;
    [StructLayout(LayoutKind.Sequential)] private struct Properties { public uint Flags; public RECT Destination,Source; public byte Opacity; public int Visible,ClientOnly; }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width,Height; }
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr destination,IntPtr source,out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail,out Size size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail,ref Properties properties);
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _closeTimer;
    private sealed class CloseRequest(WindowInfo source,StackPanel card,Button button,Button target)
    {
        internal readonly WindowInfo Source=source;
        internal readonly StackPanel Card=card;
        internal readonly Button Button=button,Target=target;
        internal readonly long Started=Stopwatch.GetTimestamp();
        internal bool Requested;
    }
    private readonly List<CloseRequest> _closeRequests=new();
    private readonly Dictionary<long,long> _closingWindows=new(),_restoringWindows=new();
    private readonly List<(IntPtr Thumbnail,WindowInfo Source,Button Target,TextBlock Title)> _thumbnails=new();
    private readonly List<Button> _closeButtons=new();
    private readonly Dictionary<long,(int Width,int Height)> _sourceSizes=new();
    private Window? _window;
    private readonly GlassBackdrop _glass=new();
    private Border? _root;
    private DockEntry? _entry;
    private string? _shownKey;
    private RECT _anchor;
    private DockEdge _edge;
    private long _deadline;
    private int _capacity=3;
    private bool _hidePending,_disposed,_layoutPending,_activating;
    private bool _diagnosticHold;
    private long _appeared;
    private string? _dismissedKey;
    private readonly Dictionary<long,bool> _minimized=new();
    internal event Action? VisibilityChanged;
    internal IntPtr Handle=>_window is null?IntPtr.Zero:WindowSetup.Hwnd(_window);
    internal bool Visible=>Handle!=IntPtr.Zero&&NativeMethods.IsWindowVisible(Handle);
    internal int ThumbnailCount=>_thumbnails.Count;
    internal IReadOnlyList<Button> CloseButtons=>_closeButtons;
    internal int PendingCloseCount=>_closeRequests.Count;
    internal DockWindowPreview()
    {
        _closeTimer=DockApp.Current.UiQueue.CreateTimer();_closeTimer.Interval=TimeSpan.FromMilliseconds(16);
        _closeTimer.Tick+=(_,_)=>FinishCloseRequests();
        _timer=DockApp.Current.UiQueue.CreateTimer();_timer.Interval=TimeSpan.FromMilliseconds(50);
        _timer.Tick+=(_,_)=>
        {
            if(_disposed){_timer.Stop();return;}
            if(_hidePending&&PointerInside){_hidePending=false;}
            if(_hidePending)
            {
                if(Stopwatch.GetTimestamp()>=_deadline)Hide();
                return;
            }
            if(Visible)
            {
                if(_thumbnails.Any(t=>_minimized.TryGetValue(t.Source.Hwnd,out var old)&&old!=NativeMethods.IsIconic(new IntPtr(t.Source.Hwnd)))) { Show();return; }
                if(_thumbnails.Any(t=>!WindowActivator.IsLive(t.Source))) { Show();return; }
                foreach(var t in _thumbnails)
                {
                    if(DwmQueryThumbnailSourceSize(t.Thumbnail,out var r)==0&&r.Width>0&&r.Height>0)
                    {
                        var size=(r.Width,r.Height);
                        if(!_sourceSizes.TryGetValue(t.Source.Hwnd,out var old)||old!=size)
                        { _sourceSizes[t.Source.Hwnd]=size;_layoutPending=true; }
                    }
                    if(t.Title.Text!=t.Source.Title)t.Title.Text=t.Source.Title??_entry?.Name??"";
                }
                if(_closingWindows.Count>0||_restoringWindows.Count>0||SurfaceMotion.Enabled&&Stopwatch.GetElapsedTime(_appeared).TotalMilliseconds<180) _layoutPending=true;
                else if(!_hidePending)_timer.Interval=TimeSpan.FromMilliseconds(200);
                if(_layoutPending)UpdateThumbnails();
                return;
            }
            if(_entry is null){_timer.Stop();return;}
            if(Stopwatch.GetTimestamp()>=_deadline)Show();
        };
    }
    internal bool PointerInside=>Visible&&NativeMethods.GetCursorPos(out var p)&&NativeMethods.GetWindowRect(Handle,out var r)&&p.X>=r.Left&&p.X<r.Right&&p.Y>=r.Top&&p.Y<r.Bottom;
    internal void Track(DockEntry? entry,RECT anchor,bool diagnostic=false,DockEdge edge=DockEdge.Bottom)
    {
        if(_diagnosticHold&&!diagnostic)return;
        if(diagnostic)_diagnosticHold=true;
        if(_disposed)return;
        if(!diagnostic&&!DockApp.Current.Settings.Current.ShowWindowPreviews){Hide();return;}
        if(entry is null&&diagnostic)_dismissedKey=null;
        else if(entry is not null&&_dismissedKey==entry.Key)return;
        else if(entry is not null)_dismissedKey=null;
        if(entry is null||!entry.Windows.Any(WindowActivator.IsLive))
        {
            if(Visible&&!PointerInside&&!_hidePending){_hidePending=true;_deadline=Stopwatch.GetTimestamp()+Stopwatch.Frequency*3/10;_timer.Interval=TimeSpan.FromMilliseconds(50);_timer.Start();}
            else if(!Visible){_timer.Stop();_entry=null;}
            return;
        }
        _anchor=anchor;_edge=edge;
        bool same=_entry?.Key==entry.Key;
        _entry=entry;
        if(same&&!_hidePending)
        {
            if(Visible&&!_thumbnails.Select(t=>t.Source.Hwnd).SequenceEqual(entry.Windows.Where(WindowActivator.IsLive).Take(_capacity).Select(w=>w.Hwnd)))Show();
            else if(Visible)
            {
                for(int i=0;i<_thumbnails.Count;i++)
                { var t=_thumbnails[i];var latest=entry.Windows.First(w=>w.Hwnd==t.Source.Hwnd);_thumbnails[i]=(t.Thumbnail,latest,t.Target,t.Title); }
            }
            return;
        }
        _hidePending=false;
        if(_shownKey==entry.Key&&Visible){_timer.Interval=TimeSpan.FromMilliseconds(200);_timer.Start();return;}
        HideSurface();_deadline=Stopwatch.GetTimestamp()+Stopwatch.Frequency*DockApp.Current.Settings.Current.PreviewDelayMs/1000;
        _timer.Interval=TimeSpan.FromMilliseconds(50);_timer.Start();
    }
    private void Show()
    {
        if(_entry is null||_disposed)return;
        var windows=_entry.Windows.Where(WindowActivator.IsLive).Take(3).ToArray();
        if(windows.Length==0){Hide();return;}
        var monitor=FloatingDock.Windows.Display.MonitorHelper.Describe(NativeMethods.MonitorFromPoint(new POINT { X=(_anchor.Left+_anchor.Right)/2,Y=(_anchor.Top+_anchor.Bottom)/2 },NativeMethods.MONITOR_DEFAULTTONEAREST))!;
        if(_window is null)
        {
            _window=new Window { Title="FloatingDock window previews",SystemBackdrop=_glass };
            WindowSetup.MakeBorderless(_window);WindowStyler.MakeOverlayWindow(Handle,noActivate:true);
        }
        ClearThumbnails();
        double scale=monitor.Scale;
        _capacity=Math.Min(3,Math.Max(1,(monitor.Bounds.Height-40)/(int)(200*scale)));
        int count=Math.Min(windows.Length,_capacity);
        var content=new StackPanel { Spacing=10 };
        var targets=new List<(WindowInfo Window,Button Target,TextBlock Title)>();
        for(int i=0;i<count;i++)
        {
            var source=windows[i];
            var title=new TextBlock { Text=source.Title??_entry.Name,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center };
            var header=new Grid { Height=28,ColumnSpacing=8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });header.Children.Add(title);
            var close=new Button { Content=new FontIcon { Glyph="\uE8BB",FontSize=10 },Width=28,Height=28,MinWidth=28,MinHeight=28,Padding=new Thickness(0),BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(8),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close,L.Text("Close")+" "+title.Text);ToolTipService.SetToolTip(close,L.Text("Close window"));
            Grid.SetColumn(close,1);header.Children.Add(close);_closeButtons.Add(close);
            var card=new StackPanel { Spacing=6 };card.Children.Add(header);
            var target=new Button { Content=new Grid { Width=248,Height=142 },Width=248,Height=142,MinWidth=0,MinHeight=0,Padding=new Thickness(0),BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(10),Background=new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(12,255,255,255)) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(target,L.Text("Open")+" "+title.Text);
            target.Click+=async(_,_)=>
            {
                if(_activating)return;_activating=true;Hide();
                try { DockApp.Current.Tiling.PrepareActivation(new[]{source});await WindowActivator.ActivateOrMinimizeAsync(new[]{source},false); }
                catch(Exception ex) { DockApp.Current.Log.Warn("Preview activation failed: "+ex.Message); }
                finally { _activating=false; }
            };
            card.Children.Add(target);content.Children.Add(card);targets.Add((source,target,title));
            close.Click+=(_,_)=>BeginClose(source,card,close,target);
        }
        _root=new Border { RequestedTheme=ElementTheme.Dark,Child=content,Padding=new Thickness(12),CornerRadius=new CornerRadius(20),BorderThickness=new Thickness(0),Background=new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(148,18,21,27)) };
        _root.PointerEntered+=(_,_)=> { _hidePending=false;_timer.Interval=TimeSpan.FromMilliseconds(200);_timer.Start(); };
        _root.PointerExited+=(_,_)=> { _hidePending=true;_deadline=Stopwatch.GetTimestamp()+Stopwatch.Frequency*3/10;_timer.Interval=TimeSpan.FromMilliseconds(50);_timer.Start(); };
        _root.Loaded+=(_,_)=> { _layoutPending=true;UpdateThumbnails(); };
        _root.SizeChanged+=(_,_)=> { _layoutPending=true; };
        _window.Content=_root;
        int width=(int)Math.Ceiling(272*scale),height=(int)Math.Ceiling((24+count*176+(count-1)*10)*scale);
        int x,y;
        if(_edge==DockEdge.Bottom) { x=(_anchor.Left+_anchor.Right-width)/2;y=_anchor.Top-height-(int)(14*scale); }
        else { x=_edge==DockEdge.Left?_anchor.Right+(int)(14*scale):_anchor.Left-width-(int)(14*scale);y=(_anchor.Top+_anchor.Bottom-height)/2; }
        x=Math.Clamp(x,monitor.Bounds.Left+8,Math.Max(monitor.Bounds.Left+8,monitor.Bounds.Right-width-8));
        y=Math.Clamp(y,monitor.Bounds.Top+8,Math.Max(monitor.Bounds.Top+8,monitor.Bounds.Bottom-height-8));
        WindowStyler.SetBounds(Handle,x,y,width,height);WindowStyler.SetRoundedRegion(Handle,width,height,(int)Math.Ceiling(20*scale));
        _glass.SetVisible(true);_window.AppWindow.Show(false);_shownKey=_entry.Key;
        _appeared=Stopwatch.GetTimestamp();SurfaceMotion.Enter(_root,180);
        _glass.AnimateOpacity(1,180,entering:true);
        foreach(var (source,target,title) in targets)
        {
            _minimized[source.Hwnd]=NativeMethods.IsIconic(new IntPtr(source.Hwnd));
            int registered=DwmRegisterThumbnail(Handle,new IntPtr(source.Hwnd),out var thumbnail);
            if(registered!=0){DockApp.Current.Log.Warn("DWM thumbnail registration: "+registered.ToString("X"));continue;}
            _thumbnails.Add((thumbnail,source,target,title));
        }
        if(_thumbnails.Count==0){Hide();return;}
        _layoutPending=true;UpdateThumbnails();_timer.Interval=TimeSpan.FromMilliseconds(SurfaceMotion.Enabled?16:200);_timer.Start();
        VisibilityChanged?.Invoke();
    }
    private void UpdateThumbnails()
    {
        if(_root is null||!_root.IsLoaded)return;
        _root.UpdateLayout();double scale=_root.XamlRoot.RasterizationScale;
        _layoutPending=false;
        foreach(var t in _thumbnails)
        {
            if(DwmQueryThumbnailSourceSize(t.Thumbnail,out var size)!=0||size.Width<1||size.Height<1)continue;
            // The DWM surface is authoritative. Client dimensions can differ from its cached surface
            // after fullscreen, restore or a DPI change; mixing them squeezed the live image.
            var origin=t.Target.TransformToVisual(_root).TransformPoint(new global::Windows.Foundation.Point());
            double factor=Math.Min(t.Target.ActualWidth/size.Width,t.Target.ActualHeight/size.Height);
            int w=(int)Math.Round(size.Width*factor*scale),h=(int)Math.Round(size.Height*factor*scale);
            int left=(int)Math.Round((origin.X+(t.Target.ActualWidth-size.Width*factor)/2)*scale),top=(int)Math.Round((origin.Y+(t.Target.ActualHeight-size.Height*factor)/2)*scale);
            float progress=SurfaceMotion.Enabled?(float)Math.Clamp(Stopwatch.GetElapsedTime(_appeared).TotalMilliseconds/180,0,1):1;
            double opacity=1-Math.Pow(1-progress,3);
            if(_closingWindows.TryGetValue(t.Source.Hwnd,out var closing))
            {
                double amount=Math.Clamp(Stopwatch.GetElapsedTime(closing).TotalMilliseconds/160,0,1);
                opacity*=Math.Pow(1-amount,2);
                // Scale the native image uniformly with its card, preserving the aspect ratio.
                double shrink=1-.06*(1-Math.Pow(1-amount,3));
                int sw=(int)Math.Round(w*shrink),sh=(int)Math.Round(h*shrink);
                left+=(w-sw)/2;top+=(h-sh)/2;w=sw;h=sh;
            }
            if(_restoringWindows.TryGetValue(t.Source.Hwnd,out var restoring))
            {
                double amount=Math.Clamp(Stopwatch.GetElapsedTime(restoring).TotalMilliseconds/180,0,1);
                opacity*=1-Math.Pow(1-amount,3);
                if(amount>=1)_restoringWindows.Remove(t.Source.Hwnd);
            }
            var p=new Properties { Flags=1|4|8|16,Destination=new RECT { Left=left,Top=top,Right=left+w,Bottom=top+h },Opacity=(byte)Math.Round(opacity*255),Visible=1,ClientOnly=1 };
            if(DwmUpdateThumbnailProperties(t.Thumbnail,ref p)!=0) { _layoutPending=true; }
        }
    }
    private void BeginClose(WindowInfo source,StackPanel card,Button button,Button target)
    {
        if(_disposed||!WindowActivator.IsLive(source)||_closeRequests.Any(r=>r.Source.Hwnd==source.Hwnd))return;
        var request=new CloseRequest(source,card,button,target);_closeRequests.Add(request);
        button.IsEnabled=false;card.IsHitTestVisible=false;
        var visual=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(card);
        visual.CenterPoint=new System.Numerics.Vector3((float)card.ActualWidth/2,(float)card.ActualHeight/2,0);
        SurfaceMotion.Animate(visual,0,.94f,160);
        if(_thumbnails.Count==1&&_root is not null)
        {
            SurfaceMotion.Animate(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_root),0,.985f,160);
            _glass.AnimateOpacity(0,160);
        }
        _closingWindows[source.Hwnd]=request.Started;_restoringWindows.Remove(source.Hwnd);
        _hidePending=false;_layoutPending=true;_timer.Interval=TimeSpan.FromMilliseconds(16);_timer.Start();
        _closeTimer.Start();
    }
    private void FinishCloseRequests()
    {
        for(int i=_closeRequests.Count-1;i>=0;i--)
        {
            var request=_closeRequests[i];double elapsed=Stopwatch.GetElapsedTime(request.Started).TotalMilliseconds;
            if(!request.Requested&&elapsed>=SurfaceMotion.Duration(160))
            {
                request.Requested=true;
                if(WindowActivator.IsLive(request.Source))WindowActivator.RequestClose(new[]{request.Source});
                DockApp.Current.Running.FullScan();
            }
            if(!request.Requested)continue;
            bool live=WindowActivator.IsLive(request.Source);
            if(live&&elapsed<500)continue;
            _closeRequests.RemoveAt(i);_closingWindows.Remove(request.Source.Hwnd);
            if(live&&Visible&&_thumbnails.Any(t=>t.Target==request.Target))
            {
                // WM_CLOSE may open a save/confirmation dialog. Never force termination.
                request.Button.IsEnabled=true;request.Card.IsHitTestVisible=true;
                SurfaceMotion.Animate(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(request.Card),1,1,180);
                if(_root is not null)
                {
                    SurfaceMotion.Animate(Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_root),1,1,180);
                    _glass.AnimateOpacity(1,180);
                }
                _restoringWindows[request.Source.Hwnd]=Stopwatch.GetTimestamp();_layoutPending=true;
                _timer.Interval=TimeSpan.FromMilliseconds(16);_timer.Start();
            }
        }
        if(_closeRequests.Count==0)_closeTimer.Stop();
    }
    private void ClearThumbnails(){foreach(var t in _thumbnails)DwmUnregisterThumbnail(t.Thumbnail);_thumbnails.Clear();_closeButtons.Clear();_sourceSizes.Clear();_closingWindows.Clear();_restoringWindows.Clear();_minimized.Clear();}
    private void HideSurface(){bool wasVisible=Visible;if(_window is not null){_window.AppWindow.Hide();WindowStyler.Hide(Handle);_glass.SetVisible(false);}ClearThumbnails();_shownKey=null;if(wasVisible)VisibilityChanged?.Invoke();}
    internal void DismissForInteraction(string key){bool hold=_diagnosticHold;Hide();_diagnosticHold=hold;_dismissedKey=key;}
    internal void ReleaseInteractionSuppression()=>_dismissedKey=null;
    internal void Hide(){_timer.Stop();HideSurface();_entry=null;_hidePending=false;_diagnosticHold=false;}
    public void Dispose(){_disposed=true;_closeTimer.Stop();_closeRequests.Clear();Hide();_window?.Close();}
}
