using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Tiling;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    private static async Task CheckSearch149Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;NativeMethods.GetCursorPos(out var cursor);
        app.Dock.HoldFullscreenStateForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;
        try
        {
            app.Settings.Update(s=>{s.TopBarEnabled=true;s.TopBarSearchEnabled=true;s.TopBarAutoHide=false;s.TilingEnabled=false;s.MenuAnimations=true;});
            bar.SetFullscreen(false);bar.Target(false);await Task.Delay(200);
            var button=bar.SearchButtonForDiagnostics;
            var point=button.TransformToVisual(button.XamlRoot.Content).TransformPoint(new(button.ActualWidth/2,button.ActualHeight/2));
            NativeMethods.GetWindowRect(bar.Handle,out var bounds);double scale=button.XamlRoot.RasterizationScale;
            int x=bounds.Left+(int)Math.Round(point.X*scale),y=bounds.Top+(int)Math.Round(point.Y*scale);
            async Task Click(int hold=35)
            {
                SetCursorPos(x,y);
                Check(NativeMethods.GetAncestor(WindowFromPoint(new POINT{X=x,Y=y}),NativeMethods.GA_ROOT)==bar.InputHandle,"The search test button is covered by another window.");
                var clicked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Microsoft.UI.Xaml.RoutedEventHandler acknowledged=(_,_)=>clicked.TrySetResult();
                button.Click+=acknowledged;
                bar.IgnoreRealCursorForDiagnostics=false;
                try
                {
                    var down=new INPUT{U=new(){mi=new(){dwFlags=2}}};
                    var up=new INPUT{U=new(){mi=new(){dwFlags=4}}};
                    NativeMethods.SendInput(1,new[]{down},Marshal.SizeOf<INPUT>());await Task.Delay(hold);
                    NativeMethods.SendInput(1,new[]{up},Marshal.SizeOf<INPUT>());
                    // AutomationPeer.Invoke queues the routed Click. Do not turn
                    // on the smoke input guard before that event has actually run.
                    await clicked.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                finally {button.Click-=acknowledged;bar.IgnoreRealCursorForDiagnostics=true;}
            }
            await Click();Check(app.Palette is not null,"Actual search button did not create the palette.");var palette=app.Palette!;
            Check(palette.Visible&&palette.SurfaceVisible&&palette.MotionForDiagnostics.IsRunning,"Actual search-button click did not start the palette animation.");
            var early=MenuRegion148(palette.Handle);await Task.Delay(340);var full=MenuRegion148(palette.Handle);
            Check(early.Width<full.Width&&early.Height<full.Height,"Search appeared at full size rather than animating its glass surface.");
            OwnWindowCapture.Capture(palette.Handle).Save(Path.Combine(app.Paths.Root,"search-open.png"));
            // Hold the button longer than the old deactivation -> click race, then release it.
            await Click(180);Check(!palette.Visible&&palette.SurfaceVisible,"Second real mouse click reopened the palette instead of closing it.");
            await Task.Delay(300);Check(!palette.Visible&&!palette.SurfaceVisible&&!NativeMethods.IsWindowVisible(palette.Handle),"Search resurrected after its closing transition.");
            for(int cycle=0;cycle<3;cycle++)
            {
                await Click();await Task.Delay(330);Check(palette.Visible,"Repeated search opening failed.");
                await Click();await Task.Delay(250);Check(!palette.Visible&&!palette.SurfaceVisible,"Repeated search-button toggle reopened the palette.");
            }
            app.ToggleSearch();await Task.Delay(70);app.ToggleSearch();await Task.Delay(25);float before=palette.MotionForDiagnostics.Progress;app.ToggleSearch();
            Check(Math.Abs(palette.MotionForDiagnostics.Progress-before)<.08,"Reopening search jumped to its initial pose.");
            await Task.Delay(380);Check(palette.Visible&&palette.SurfaceVisible,"A stale close completion hid the reopened search.");
            var stale=palette.SearchForDiagnostics("unmatched-long-query");palette.Hide();await stale;
            Check(!palette.SurfaceVisible&&!palette.Visible,"Cancelled results reopened the palette.");
            app.Settings.Update(s=>s.MenuAnimations=false);app.ToggleSearch();Check(palette.MotionForDiagnostics.Progress==1&&!palette.MotionForDiagnostics.IsRunning,"Reduced motion did not show search immediately.");app.ToggleSearch();Check(!palette.SurfaceVisible,"Reduced motion did not close search immediately.");
            checks["actualSearchButtonToggle"]=new{cycles=4,longMousePress=true,noReopen=true};checks["searchGlassUnfolding"]=new{early,full};checks["searchReversalAndCancellation"]=true;
        }
        finally
        {
            app.Palette?.Hide();SetCursorPos(cursor.X,cursor.Y);bar.IgnoreRealCursorForDiagnostics=false;app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var property in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(s,property.GetValue(saved));});
        }
    }
    private static async Task CheckDockMotion149Async(DockApp app,long id,Dictionary<string,object> checks)
    {
        app.Settings.Update(s=>{s.TilingAnimationMs=110;s.IconAnimationMs=240;s.TilingAnimationFps=144;});
        var hwnd=new IntPtr(id);var records=new List<object>();
        async Task Wait(Func<bool> ready,string error)
        {var time=Stopwatch.StartNew();while(!ready()&&time.ElapsedMilliseconds<4000)await Task.Delay(10);Check(ready(),error);}
        async Task Record(string name,bool restoring)
        {
            var nativeFrames=new ConcurrentQueue<TileRect>();
            void Rendered(long window,double time,TileRect bounds,byte alpha){if(window==id)nativeFrames.Enqueue(bounds);}
            app.Tiling.PresentationRenderedForDiagnostics+=Rendered;
            try
            {
            await Wait(()=>app.Tiling.PresentationsForDiagnostics.ContainsKey(id),"No visible window animation: "+name);
            var frames=new List<TileRect>();var time=Stopwatch.StartNew();bool pixels=false;
            OwnWindowCapture.Frame? captured=null;
            while(app.Tiling.PresentationsForDiagnostics.TryGetValue(id,out var surface)&&time.ElapsedMilliseconds<2000)
            {
                frames.Add(surface.Bounds);
                if(restoring&&!NativeMethods.IsIconic(hwnd))
                    Check(GetLayeredWindowAttributes(hwnd,out _,out byte alpha,out _)&&alpha==0,"The real window appeared underneath its restoring representation.");
                if(!pixels&&time.ElapsedMilliseconds>55&&surface.Bounds.Width>80&&surface.Opacity>150)
                {
                    var image=OwnWindowCapture.Capture(surface.Handle);NativeMethods.GetWindowRect(surface.Handle,out var viewport);
                    int offset=((surface.Bounds.Y+surface.Bounds.Height/2-viewport.Top)*image.Width+surface.Bounds.X+surface.Bounds.Width/2-viewport.Left)*4;
                    Check(offset>=0&&offset+3<image.Pixels.Length&&image.Pixels[offset]>90&&image.Pixels[offset+1]>45,"The animated window contains no actual application pixels: "+name);
                    captured=image;pixels=true;
                }
                await Task.Delay(12);
            }
            // Native composition now continues while this dispatcher captures
            // pixels. Count submitted native frames, not UI sampling callbacks.
            Check(pixels&&nativeFrames.Distinct().Count()>=16,"A window snapped rather than drawing a continuous transition: "+name);
            Check(time.ElapsedMilliseconds>=220,"The dock transition finished before its unfolding was visible: "+name);
            captured?.Save(Path.Combine(app.Paths.Root,name+".png"));
            records.Add(new{name,frames=frames.Distinct().Count(),nativeFrames=nativeFrames.Distinct().Count(),milliseconds=time.ElapsedMilliseconds,pixels=true,first=frames[0],last=frames[^1]});
            await Wait(()=>!app.Tiling.Animating,"A window animation did not finish.");
            }
            finally {app.Tiling.PresentationRenderedForDiagnostics-=Rendered;}
        }
        WindowInfo Info()=>new(id,Environment.ProcessId,Environment.ProcessPath,null,"Dock motion fixture",null,NativeMethods.IsIconic(hwnd));
        for(int cycle=0;cycle<2;cycle++)
        {
            Check(app.Tiling.MinimizeToDock(hwnd),"Dock minimization was rejected.");await Record("tile-minimize-"+cycle,false);
            Check(NativeMethods.IsIconic(hwnd),"Minimization did not change native state.");
            app.Tiling.PrepareActivation(new[]{Info()});
            Check(GetLayeredWindowAttributes(hwnd,out _,out byte prepared,out _)&&prepared==0,"Dock activation did not acquire its invisible source before native restore.");
            NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);await Record("tile-restore-"+cycle,true);
            Check(!NativeMethods.IsIconic(hwnd),"Restoration did not reveal the native window.");
            if(GetLayeredWindowAttributes(hwnd,out _,out byte finalAlpha,out _))Check(finalAlpha!=0,"Restoration left the actual window transparent.");
        }
        app.Tiling.MinimizeToDock(hwnd);await Task.Delay(70);
        var before=app.Tiling.PresentationsForDiagnostics[id];app.Tiling.PrepareActivation(new[]{Info()});
        var after=app.Tiling.PresentationsForDiagnostics[id];
        var resumed=app.Tiling.TransitionStartForDiagnostics(id);
        // The native frame clock can advance between the two UI observations.
        Check(before.Handle==after.Handle&&Math.Abs(resumed.Width-before.Bounds.Width)<Math.Max(32,before.Bounds.Width/5)&&Math.Abs(resumed.X-before.Bounds.X)<Math.Max(32,before.Bounds.Width/5),"Rapid restore discarded or reset the moving window representation.");
        NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);await Wait(()=>!app.Tiling.Animating&&!NativeMethods.IsIconic(hwnd),"Rapid restore did not complete.");
        checks["dockWindowUnfolding"]=records;checks["rapidDockReversalReusesSurface"]=true;
    }
}
