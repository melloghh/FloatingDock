using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Models;
using FloatingDock.Core.Tiling;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    [DllImport("user32.dll",EntryPoint="NotifyWinEvent")] private static extern void NotifyFixtureEvent(uint kind,IntPtr hwnd,int objectId,int childId);
    private static async Task CheckUi143Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var mon=app.Dock.PrimaryWindowForDiagnostics!.Monitor;
        var back=new Window {Title="FloatingDock minimize background",Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,42,42,42))}};
        var front=new Window {Title="FloatingDock untiled minimize probe",Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,38,108,156))}};
        var hwnd=WindowSetup.Hwnd(front);long id=hwnd.ToInt64();var frames=new List<object>();
        async Task Wait(Func<bool> ready,string error,int limit=3500)
        {var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<limit)await Task.Delay(10);Check(ready(),error);}
        int BlueAtIcon(OwnWindowCapture.Frame frame,RECT icon)
        {
            int count=0;
            for(int y=Math.Max(0,icon.Top-mon.Bounds.Top-70);y<Math.Min(frame.Height,icon.Bottom-mon.Bounds.Top+70);y++)
                for(int x=Math.Max(0,icon.Left-mon.Bounds.Left-70);x<Math.Min(frame.Width,icon.Right-mon.Bounds.Left+70);x++)
                {int p=(y*frame.Width+x)*4;if(Math.Abs(frame.Pixels[p]-156)<8&&Math.Abs(frame.Pixels[p+1]-108)<8&&Math.Abs(frame.Pixels[p+2]-38)<8)count++;}
            return count;
        }
        try
        {
            app.Dock.HoldFullscreenStateForDiagnostics=true;
            app.Settings.Update(s=>{s.TilingEnabled=false;s.IconAnimation=true;s.IconAnimationMs=240;s.AutoHide=false;s.AnimationFps=144;s.TilingAnimationFps=30;if(Environment.GetCommandLineArgs().Contains("--test-side-dock"))s.Edge=FloatingDock.Core.Configuration.DockEdge.Left;});app.Tiling.Apply(app.Settings.Current);
            back.Activate();WindowStyler.SetBounds(WindowSetup.Hwnd(back),mon.Bounds.Left,mon.Bounds.Top,mon.Bounds.Width,mon.Bounds.Height,topmost:true);
            front.Activate();WindowStyler.SetBounds(hwnd,mon.WorkArea.Left+220,mon.WorkArea.Top+150,900,650,topmost:true);
            app.Running.IncludeOwnWindow(hwnd);await Wait(()=>app.Dock.AnimationRectFor(hwnd) is not null,"Fixture has no dock icon.");await Task.Delay(300);
            for(int cycle=0;cycle<3;cycle++)
            {
                NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);front.Activate();await Wait(()=>!NativeMethods.IsIconic(hwnd),"Fixture did not restore.");await Task.Delay(140);
                var icon=app.Dock.CurrentAnimationRectFor(hwnd)!.Value;
                // A preceding click must not override today's icon geometry.
                var stale=icon;stale.Left+=450;stale.Right+=450;
                app.Dock.RememberAnimationOrigin(app.Running.Snapshot().Where(w=>w.Hwnd==id).ToArray(),stale);
                int baseline=BlueAtIcon(OwnWindowCapture.Capture(WindowSetup.Hwnd(back)),icon);
                if(cycle==1)app.Tiling.MinimizeToDock(hwnd);
                else NativeMethods.PostMessage(hwnd,0x112,new(0xF020),IntPtr.Zero); // Actual titlebar/system-menu path.
                await Wait(()=>app.Tiling.PresentationsForDiagnostics.ContainsKey(id),"Untiled minimize has no presentation.");
                var motion=new List<TileRect>();var timer=Stopwatch.StartNew();
                while(app.Tiling.Animating&&timer.ElapsedMilliseconds<1800)
                {if(app.Tiling.PresentationsForDiagnostics.TryGetValue(id,out var p))motion.Add(p.Bounds);await Task.Delay(8);}
                Check(NativeMethods.IsIconic(hwnd),"Fixture did not remain minimized.");
                Check(motion.Count>0&&Math.Abs(motion[^1].CenterX-(icon.Left+icon.Width/2d))<12&&Math.Abs(motion[^1].CenterY-(icon.Top+icon.Height/2d))<12,"Minimize used a stale dock-click destination.");
                int worst=0;for(int sample=0;sample<10;sample++){await Task.Delay(35);worst=Math.Max(worst,BlueAtIcon(OwnWindowCapture.Capture(WindowSetup.Hwnd(back)),icon)-baseline);}
                frames.Add(new{cycle,distinctFrames=motion.Distinct().Count(),tailBlueExcess=worst,last=motion.LastOrDefault()});
                checks["untiledMinimizeCycles"]=frames;
                Check(worst<100,"The application image reappeared near its dock icon after minimization.");
                NotifyFixtureEvent(NativeMethods.EVENT_SYSTEM_MINIMIZESTART,hwnd,0,0);
                await Task.Delay(90);
                Check(!app.Tiling.PresentationsForDiagnostics.ContainsKey(id),"A delayed duplicate minimize acknowledgement restarted the shrinking image.");
                Check(motion.Distinct().Count()>10,"Untiled minimization used the disabled tiling frame rate.");
                Check((NativeMethods.GetExStyle(hwnd)&0x80000)==0,"Minimize leaked a layered style.");
            }
            NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);front.Activate();await Task.Delay(140);
            app.Tiling.MinimizeToDock(hwnd);await Task.Delay(45);NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);
            await Wait(()=>!app.Tiling.Animating&&!NativeMethods.IsIconic(hwnd),"Rapid restore left a stale shrinking image.");
            Check((NativeMethods.GetExStyle(hwnd)&0x80000)==0,"Rapid restore leaked an opacity lease.");checks["untiledRapidRestore"]=true;
            checks["delayedMinimizeAcknowledgementsIgnored"]=true;
            checks["currentDockDestination"]=true;
        }
        finally
        {
            app.Running.ExcludeOwnWindow(hwnd);front.Close();back.Close();app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var p in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
        }
    }
}
