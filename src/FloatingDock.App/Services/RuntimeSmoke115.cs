using System.Diagnostics;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    private static async Task CheckUi115Async(DockApp app,Dictionary<string,object> checks)
    {
        var dock=app.Dock.PrimaryWindowForDiagnostics!;var bar=app.TopBar!.Primary!;
        var original=app.Settings.Current.Clone();
        app.Dock.HoldFullscreenStateForDiagnostics=true;app.Dock.HoldWindowListForDiagnostics=true;
        dock.IgnoreRealCursorForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;
        Window? probe=null;
        try
        {
            app.Settings.Update(s=> { s.Edge=DockEdge.Left;s.AutoHide=false;s.TopBarEnabled=true;s.TopBarAutoHide=false; });
            dock.SetFullscreenHidden(false);bar.SetFullscreen(false);await Task.Delay(450);
            probe=new Window { Title="Own fullscreen video / restore probe",Content=new Grid { Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,34,187,153)) } };
            WindowSetup.MakeBorderless(probe);var hwnd=WindowSetup.Hwnd(probe);var monitor=dock.Monitor;
            WindowStyler.SetBounds(hwnd,monitor.Bounds.Left,monitor.Bounds.Top,monitor.Bounds.Width,monitor.Bounds.Height,topmost:false);probe.AppWindow.Show(false);
            await Task.Delay(200);
            checks["fullscreenProbeGeometry"]=new { bounds=NativeRect(hwnd),monitor=monitor.Bounds,style=NativeMethods.GetStyle(hwnd),visible=NativeMethods.IsWindowVisible(hwnd),sourceMonitor=NativeMethods.MonitorFromWindow(hwnd,NativeMethods.MONITOR_DEFAULTTONEAREST).ToInt64(),targetMonitor=monitor.Handle.ToInt64() };
            Check(FullscreenDetector.IsWindowFullscreenOn(hwnd,monitor.Handle,0),"Own unframed fullscreen video window was not detected.");
            dock.SetFullscreenHidden(true,blockEdgeReveal:true);bar.SetFullscreen(true);await Task.Delay(90);
            Check(dock.HideAmount is >0 and <1,"Fullscreen concealment lost its exit animation.");
            await Task.Delay(650);
            Check(dock.HideAmount==1&&!WindowStyler.IsVisible(dock.Handle),"Fullscreen dock still exposes its edge strip.");
            dock.IgnoreRealCursorForDiagnostics=false;
            SendMessage(dock.Handle,0x200,IntPtr.Zero,IntPtr.Zero);dock.Reveal();bar.Reveal();bar.Target(false);
            await Task.Delay(250);
            Check(dock.HideAmount==1&&!WindowStyler.IsVisible(dock.Handle)&&bar.HideAmount==1&&!WindowStyler.IsVisible(bar.Handle),"Hover or reveal reopened a panel over fullscreen video.");
            checks["fullscreenVideo"]=new { ownUnframedWindowDetected=true,hiddenEdgeCannotReveal=true,topBarCannotReveal=true,animatedExit=true };
            WindowStyler.SetBounds(hwnd,monitor.Bounds.Left+450,monitor.Bounds.Top+220,640,360,topmost:false);
            await Task.Delay(250);Check(!FullscreenDetector.IsWindowFullscreenOn(hwnd,monitor.Handle,0),"A windowed video remained classified as fullscreen.");
            dock.IgnoreRealCursorForDiagnostics=true;dock.SetFullscreenHidden(false);bar.SetFullscreen(false);await Task.Delay(500);
            Check(dock.HideAmount==0&&WindowStyler.IsVisible(dock.Handle)&&bar.HideAmount==0,"Panels did not return after fullscreen ended.");
            for(int i=0;i<4;i++)
            {
                bar.OpenMenuForDiagnostics("main");await Task.Delay(220);
                Check(bar.Menus.ActiveKind=="main"&&WindowStyler.IsVisible(bar.Menus.PopupHandle),"Circle menu did not open.");
                if(i==0)OwnWindowCapture.Capture(bar.Menus.PopupHandle).Save(Path.Combine(app.Paths.Root,"main-menu.png"));
                bar.OpenMenuForDiagnostics("main");await Task.Delay(220);
                Check(bar.Menus.ActiveKind is null&&!WindowStyler.IsVisible(bar.Menus.PopupHandle),"Second circle invocation did not close the menu.");
            }
            bar.OpenMenuForDiagnostics("main");await Task.Delay(25);bar.OpenMenuForDiagnostics("main");await Task.Delay(25);
            bar.OpenMenuForDiagnostics("main");await Task.Delay(220);
            Check(bar.Menus.ActiveKind=="main"&&WindowStyler.IsVisible(bar.Menus.PopupHandle),"Old close completion hid a reopened menu.");
            bar.SetCaptureHidden(true);Check(!WindowStyler.IsVisible(bar.Menus.PopupHandle),"Capture suppression waited for a menu fade.");bar.SetCaptureHidden(false);bar.Target(false);await Task.Delay(250);
            checks["circleToggle"]=new { repeatedCycles=4,interruptedCloseSafe=true,captureDismissImmediate=true };
            await app.PrepareLaunchpadAsync();var launchpad=app.LaunchpadWin!;
            launchpad.Show();await Task.Delay(380);
            Check(launchpad.SearchSize.Width<=260.5&&launchpad.SearchSize.Height is >=33 and <=35,"Launchpad search is not compact.");
            OwnWindowCapture.Capture(launchpad.Handle).Save(Path.Combine(app.Paths.Root,"launchpad-compact.png"));
            int bindings=Launchpad.LaunchpadTile.BindingChanges,rebuilds=launchpad.ViewRebuilds;
            launchpad.Hide();await Task.Delay(230);launchpad.Show();await Task.Delay(380);
            Check(Launchpad.LaunchpadTile.BindingChanges==bindings&&launchpad.ViewRebuilds==rebuilds,"Reopening Launchpad rebuilt unchanged tiles or its search view.");
            var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var timer=Stopwatch.StartNew();await Task.Delay(2500);
            double oneCore=(process.TotalProcessorTime-cpu).TotalMilliseconds/timer.Elapsed.TotalMilliseconds*100;
            checks["launchpadReuse"]=new { searchWidth=launchpad.SearchSize.Width,searchHeight=launchpad.SearchSize.Height,unchangedBindings=true,unchangedView=true,realizedTiles=launchpad.RealizedTileCount,idleMeasurementMs=timer.Elapsed.TotalMilliseconds,cpuPercentOfOneCore=oneCore,cpuPercentOfMachine=oneCore/Environment.ProcessorCount };
            launchpad.Hide();await Task.Delay(40);launchpad.Show();await Task.Delay(50);launchpad.Hide();await Task.Delay(250);
            Check(!launchpad.IsOpen&&!WindowStyler.IsVisible(launchpad.Handle),"Interrupted Launchpad animation left a visible window.");
            launchpad.Show();await Task.Delay(380);Check(launchpad.IsOpen,"Launchpad could not reopen after interrupted transitions.");launchpad.Hide();await Task.Delay(350);
            var entry=new DockEntry { Kind=DockEntryKind.App,Key="preview115",Name="Video restore",Windows=new[]{new WindowInfo(hwnd.ToInt64(),Environment.ProcessId,Environment.ProcessPath,null,probe.Title,null,false)} };
            WindowStyler.BringToFront(hwnd);await Task.Delay(180);
            var source=OwnWindowCapture.Capture(hwnd);
            var preview=dock.PreviewForDiagnostics;preview.Track(entry,dock.PlateScreenForDiagnostics,diagnostic:true,edge:DockEdge.Left);await Task.Delay(1050);
            Check(preview.Visible,"Windowed preview after fullscreen did not open.");
            static (int Width,int Height) GreenBounds(OwnWindowCapture.Frame frame)
            {
                int minX=frame.Width,minY=frame.Height,maxX=-1,maxY=-1;
                for(int y=0;y<frame.Height;y++)for(int x=0;x<frame.Width;x++)
                {
                    int p=(y*frame.Width+x)*4;
                    if(Math.Abs(frame.Pixels[p+2]-34)<5&&Math.Abs(frame.Pixels[p+1]-187)<5&&Math.Abs(frame.Pixels[p]-153)<5) { minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y); }
                }
                return(maxX-minX+1,maxY-minY+1);
            }
            var destination=OwnWindowCapture.Capture(preview.Handle);
            var src=GreenBounds(source);var dst=GreenBounds(destination);
            Check(src.Width>500&&src.Height>250&&dst.Width>150&&dst.Height>80&&Math.Abs((double)dst.Width/dst.Height-(double)src.Width/src.Height)<.12,"Windowed thumbnail changed the source image proportions.");
            destination.Save(Path.Combine(app.Paths.Root,"preview-after-fullscreen.png"));
            checks["previewAspect"]=new { restoredFromFullscreen=true,sourceWidth=src.Width,sourceHeight=src.Height,thumbnailWidth=dst.Width,thumbnailHeight=dst.Height,ratioError=Math.Abs((double)dst.Width/dst.Height-(double)src.Width/src.Height) };
            int launchLeft=dock.PlateScreenForDiagnostics.Right;
            var launchOrigin=new RECT { Left=launchLeft,Top=300,Right=launchLeft+40,Bottom=340 };
            app.Dock.RememberLaunchOrigin(new AppRef { Name="Own new-window probe",ExePath=Environment.ProcessPath },launchOrigin);
            var resolved=app.Dock.AnimationRectFor(hwnd);
            Check(resolved is { } r&&r.Left==launchOrigin.Left&&r.Top==launchOrigin.Top&&r.Width==40&&r.Height==40,"A new untracked window lost its launch origin.");
            checks["newWindowAnimationOrigin"]=new { untrackedHwndMatchedByIdentity=true,recentLaunchOriginRetained=true,systemWindowAnimation=true };
        }
        finally
        {
            dock.PreviewForDiagnostics.Hide();app.LaunchpadWin?.Hide();probe?.Close();dock.SetFullscreenHidden(false);bar.SetFullscreen(false);
            app.Settings.Update(s=> { s.Edge=original.Edge;s.AutoHide=original.AutoHide;s.TopBarEnabled=original.TopBarEnabled;s.TopBarAutoHide=original.TopBarAutoHide; });
            dock.IgnoreRealCursorForDiagnostics=false;bar.IgnoreRealCursorForDiagnostics=false;
            app.Dock.HoldFullscreenStateForDiagnostics=false;app.Dock.HoldWindowListForDiagnostics=false;
        }
    }
}
