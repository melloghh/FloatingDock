using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    private static async Task CheckBarInput154Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;NativeMethods.GetCursorPos(out var cursor);
        app.Dock.HoldFullscreenStateForDiagnostics=true;app.Tiling.SkipHotkeyRegistrationForDiagnostics=true;
        async Task Click(Button button)
        {
            var point=button.TransformToVisual(button.XamlRoot.Content).TransformPoint(new(button.ActualWidth/2,button.ActualHeight/2));
            NativeMethods.GetWindowRect(bar.Handle,out var bounds);double scale=button.XamlRoot.RasterizationScale;
            int x=bounds.Left+(int)Math.Round(point.X*scale),y=bounds.Top+(int)Math.Round(point.Y*scale);
            SetCursorPos(x,y);
            Check(NativeMethods.GetAncestor(WindowFromPoint(new POINT{X=x,Y=y}),NativeMethods.GA_ROOT)==bar.InputHandle,"The native button is covered.");
            NativeMethods.SendInput(1,new[]{new INPUT{U=new(){mi=new(){dwFlags=2}}}},Marshal.SizeOf<INPUT>());await Task.Delay(25);
            NativeMethods.SendInput(1,new[]{new INPUT{U=new(){mi=new(){dwFlags=4}}}},Marshal.SizeOf<INPUT>());await Task.Delay(30);
        }
        try
        {
            app.Settings.Update(s=>{s.TopBarEnabled=true;s.TopBarAutoHide=false;s.MenuAnimations=true;s.TilingEnabled=false;});
            bar.SetFullscreen(false);bar.Target(false);await Task.Delay(300);
            foreach(var kind in new[]{"main","sound","network","language","calendar","media","tray"})
            {
                await Click(bar.ButtonForDiagnostics(kind));
                var clock=Stopwatch.StartNew();while(bar.Menus.ActiveKind!=kind&&clock.ElapsedMilliseconds<3000)await Task.Delay(15);
                Check(bar.Menus.ActiveKind==kind&&bar.Menus.SurfaceVisible,"Native click did not open "+kind);
                await Task.Delay(350);await Click(bar.ButtonForDiagnostics(kind));await Task.Delay(260);
                Check(bar.Menus.ActiveKind is null&&!bar.Menus.SurfaceVisible,"Native click did not close "+kind);
            }
            string? openedUri=null;bar.OpenUriForDiagnostics=uri=>openedUri=uri;
            await Click(bar.ButtonForDiagnostics("settings"));bar.OpenUriForDiagnostics=null;
            Check(openedUri=="ms-settings:","The settings gear did not open Windows settings.");checks["settingsGearUri"]=openedUri!;
            var search=bar.ButtonForDiagnostics("search");var searchPoint=search.TransformToVisual(search.XamlRoot.Content).TransformPoint(new(search.ActualWidth/2,search.ActualHeight/2));
            NativeMethods.GetWindowRect(bar.Handle,out var barBounds);double searchScale=search.XamlRoot.RasterizationScale;
            int tipX=barBounds.Left+(int)(searchPoint.X*searchScale),tipY=barBounds.Top+(int)(searchPoint.Y*searchScale);
            var tipMove=new INPUT{U=new(){mi=new(){dx=(int)((tipX-GetSystemMetrics(76)+.5)*65536/GetSystemMetrics(78)),dy=(int)((tipY-GetSystemMetrics(77)+.5)*65536/GetSystemMetrics(79)),dwFlags=0xC001}}};
            NativeMethods.SendInput(1,new[]{tipMove},Marshal.SizeOf<INPUT>());await Task.Delay(850);
            Check(NativeMethods.IsWindowVisible(bar.TooltipHandleForDiagnostics),"The bar tooltip did not appear: "+bar.TooltipStateForDiagnostics);
            OwnWindowCapture.Capture(bar.TooltipHandleForDiagnostics).Save(Path.Combine(app.Paths.Root,"bar-tooltip.png"));
            app.Tiling.DiagnosticWindows=new HashSet<long>();
            app.Settings.Update(s=>{s.TilingEnabled=true;s.TilingWorkspaces=3;});app.Tiling.Apply(app.Settings.Current);await Task.Delay(250);
            Check(bar.WorkspaceButtonsForDiagnostics.Count==3,"Workspace controls did not appear.");
            await Click(bar.WorkspaceButtonsForDiagnostics[1]);await Task.Delay(150);
            Check(app.Tiling.Session!.ActiveWorkspace(bar.Monitor.DeviceName)==2,"Native workspace click did not switch the workspace.");
            bar.SetSuspended(true);Check(!NativeMethods.IsWindowVisible(bar.InputHandle),"Suspending the bar left an input blocker.");
            bar.SetSuspended(false);await Task.Delay(100);
            await bar.OpenTrayForDiagnostics();await Task.Delay(350);
            var dock=app.Dock.PrimaryWindowForDiagnostics!;
            var panelBounds=WindowStyler.GetRect(bar.Handle);var dockBounds=WindowStyler.GetRect(dock.Handle);
            bool Topmost(IntPtr handle)=>(NativeMethods.GetExStyle(handle)&NativeMethods.WS_EX_TOPMOST)!=0;
            app.Dock.BeginCaptureShortcut();
            Window? selection=null;
            try
            {
                Check(NativeMethods.IsWindowVisible(bar.Handle)&&!NativeMethods.IsWindowVisible(bar.InputHandle),"Capture left the bar input blocker visible.");
                Check(!Topmost(bar.Handle)&&!Topmost(dock.Handle)&&!Topmost(bar.Menus.PopupHandle),"Capture left a panel or menu above selection UI.");
                selection=new Window {Title="FloatingDock capture selection fixture",Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,72,98,120))}};
                var overlay=WindowSetup.Hwnd(selection);WindowStyler.MakeOverlayWindow(overlay,noActivate:true);
                var monitor=bar.Monitor.Bounds;WindowStyler.SetBounds(overlay,monitor.Left,monitor.Top,monitor.Width,monitor.Height);WindowStyler.Show(overlay,false);
                // A layout update must not promote panels while the selection overlay is active.
                bar.Apply(app.Settings.Current,bar.Monitor);await Task.Delay(120);
                foreach(var handle in new[]{bar.Handle,dock.Handle,bar.Menus.PopupHandle})
                {
                    Check(!Topmost(handle),"Layout promoted a capture surface.");
                    NativeMethods.GetWindowRect(handle,out var rect);var point=new POINT{X=rect.Left+rect.Width/2,Y=rect.Top+rect.Height/2};
                    Check(NativeMethods.GetAncestor(WindowFromPoint(point),NativeMethods.GA_ROOT)==overlay,"A panel intercepts screenshot selection.");
                }
                Check(WindowStyler.GetRect(bar.Handle)==panelBounds&&WindowStyler.GetRect(dock.Handle)==dockBounds,"Capture moved the desktop panels.");
            }
            finally {selection?.Close();bar.SetCaptureHidden(false);dock.SetCaptureHidden(false);bar.Menus.Close(immediately:true);}
            await Task.Delay(2200);
            Check(Topmost(bar.Handle)&&Topmost(dock.Handle)&&NativeMethods.IsWindowVisible(bar.InputHandle),"Capture did not restore panel stacking and input.");
            checks["captureSelectionStacking"]=new{panelsAndMenuBelowSelection=true,inputReleased=true,geometryPreserved=true,restored=true};
            SetCursorPos(bar.Monitor.Bounds.Left+200,bar.Monitor.Bounds.Top+300);await Task.Delay(40);
            bar.Target(true);await Task.Delay(400);Check(bar.HideAmount>=.99,"The bar did not hide.");
            NativeMethods.GetWindowRect(bar.Handle,out var host);NativeMethods.GetWindowRect(bar.InputHandle,out var input);
            Check(host.Left==input.Left&&host.Top==input.Top&&host.Width==input.Width&&host.Height==input.Height,"The hidden bar left its input surface behind.");
            bar.Target(false);await Task.Delay(400);Check(bar.HideAmount==0,"The bar did not reveal.");
            OwnWindowCapture.Capture(bar.Handle).Save(Path.Combine(app.Paths.Root,"bar-native-input.png"));
            checks["actualNativeBarClicks"]=new{menus=7,sameButtonCloses=true,workspaces=true,suspendAndCapture=true,hideAndReveal=true};
        }
        finally
        {
            bar.OpenUriForDiagnostics=null;bar.SetCaptureHidden(false);app.Dock.PrimaryWindowForDiagnostics?.SetCaptureHidden(false);bar.Menus.Close(immediately:true);SetCursorPos(cursor.X,cursor.Y);app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);app.Tiling.DiagnosticWindows=null;
            app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var property in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(s,property.GetValue(saved));});
        }
    }
    private static async Task CheckHover154Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;
        NativeMethods.GetCursorPos(out var cursor);app.Dock.HoldFullscreenStateForDiagnostics=true;
        var results=new List<object>();
        try
        {
            app.Settings.Update(s=>{s.TilingEnabled=false;s.TopBarEnabled=true;s.TopBarAutoHide=false;});
            bar.SetFullscreen(false);bar.Target(false);await Task.Delay(800);
            NativeMethods.GetWindowRect(bar.Handle,out var bounds);
            int left=GetSystemMetrics(76),top=GetSystemMetrics(77),width=GetSystemMetrics(78),height=GetSystemMetrics(79),y=bounds.Top+bounds.Height/2;
            void Move(int x)
            {
                var move=new INPUT{U=new(){mi=new(){dx=(int)((x-left+.5)*65536/width),dy=(int)((y-top+.5)*65536/height),dwFlags=0xC001}}};
                Check(NativeMethods.SendInput(1,new[]{move},Marshal.SizeOf<INPUT>())==1,"Mouse input failed.");
            }
            // Discard shader/startup work and actually exercise the native input route before measuring.
            var warmup=Stopwatch.StartNew();
            while(warmup.ElapsedMilliseconds<3000){Move(bounds.Right-310+(int)(280*(.5+.49*Math.Sin(warmup.ElapsedMilliseconds/170.0))));await Task.Delay(8);}
            checks["uiThread"]=NativeMethods.GetWindowThreadProcessId(bar.Handle,out _);
            foreach(var mode in new[]{"empty","buttons","stationary","noHitTesting","buttonsRepeat"})
            {
                bar.SetHitTestingForDiagnostics(mode!="noHitTesting");
                Move(bounds.Right-160);await Task.Delay(500);
                using var process=Process.GetCurrentProcess();
                var cpu=process.TotalProcessorTime;int entries=bar.ButtonPointerEntriesForDiagnostics,moves=bar.PointerMovesForDiagnostics,xamlMoves=bar.XamlPointerMovesForDiagnostics;
                var clock=Stopwatch.StartNew();
                while(clock.ElapsedMilliseconds<3000)
                {
                    int x=mode=="empty"?bounds.Left+300+(int)(300*(.5+.49*Math.Sin(clock.ElapsedMilliseconds/170.0))):bounds.Right-310+(int)(280*(.5+.49*Math.Sin(clock.ElapsedMilliseconds/170.0)));
                    if(mode!="stationary")Move(x);
                    Check(NativeMethods.GetAncestor(WindowFromPoint(new POINT{X=x,Y=y}),NativeMethods.GA_ROOT)==bar.InputHandle,"Bar is covered.");
                    await Task.Delay(8);
                }
                double elapsed=clock.Elapsed.TotalMilliseconds,used=(process.TotalProcessorTime-cpu).TotalMilliseconds;
                Check(bar.XamlPointerMovesForDiagnostics==xamlMoves,"Mouse movement reentered the XAML bar input site.");
                results.Add(new{mode,cpuMs=used,machinePercent=100*used/elapsed/Environment.ProcessorCount,pointerEntries=bar.ButtonPointerEntriesForDiagnostics-entries,pointerMoves=bar.PointerMovesForDiagnostics-moves,xamlPointerMoves=bar.XamlPointerMovesForDiagnostics-xamlMoves,includesInputDriverCpu=true});
            }
            checks["realPointerWorkloads"]=results;
            OwnWindowCapture.Capture(bar.Handle).Save(Path.Combine(app.Paths.Root,"bar-hover.png"));
        }
        finally
        {
            bar.SetHitTestingForDiagnostics(true);SetCursorPos(cursor.X,cursor.Y);app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var property in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(s,property.GetValue(saved));});
        }
    }
}
