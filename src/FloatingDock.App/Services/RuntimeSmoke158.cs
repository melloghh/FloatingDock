using System.Diagnostics;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    private sealed record AnchorFrame(double Ms,int X,int Y,int W,int H);
    private static async Task CheckAnchor158Async(DockApp app,Dictionary<string,object> checks)
    {
        var args=Environment.GetCommandLineArgs();int arg=Array.IndexOf(args,"--test-anchor-launch");
        Check(arg>=0,"Supply the external fixture or browser executable.");string exe=Path.GetFullPath(args[arg+1]);
        bool browser=!Path.GetFileName(exe).StartsWith("MinimizeFixture",StringComparison.OrdinalIgnoreCase);
        var saved=app.Settings.Current.Clone();var monitor=app.Dock.PrimaryWindowForDiagnostics!.Monitor;
        var back=new Window {Title="FloatingDock search minimize background",Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,42,42,42))}};
        IntPtr target=IntPtr.Zero;var trace=new System.Collections.Concurrent.ConcurrentQueue<object>();var cycles=new List<object>();
        var replies=new System.Collections.Concurrent.ConcurrentQueue<(IntPtr Window,RECT? Rect)>();
        async Task Wait(Func<bool> ready,string error)
        {var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<6500)await Task.Delay(8);Check(ready(),error);}
        app.Msg.AnimationRectDiagnostic=(h,before,after)=>{replies.Enqueue((h,after));trace.Enqueue(new{hwnd=h.ToInt64(),iconic=NativeMethods.IsIconic(h),before=new{before.Left,before.Top,before.Right,before.Bottom},after=after is { } r?new{r.Left,r.Top,r.Right,r.Bottom}:null});};
        try
        {
            app.Dock.HoldFullscreenStateForDiagnostics=true;
            app.Settings.Update(s=>{s.TilingEnabled=false;s.Edge=FloatingDock.Core.Configuration.DockEdge.Left;s.AutoHide=false;s.ShowRunning=true;});app.Tiling.Apply(app.Settings.Current);
            back.Activate();WindowStyler.SetBounds(WindowSetup.Hwnd(back),monitor.Bounds.Left,monitor.Bounds.Top,monitor.Bounds.Width,monitor.Bounds.Height,topmost:!browser);
            var previous=new HashSet<long>();NativeMethods.EnumWindows((h,_)=>{previous.Add(h.ToInt64());return true;},IntPtr.Zero);
            app.Dock.HoldWindowListForDiagnostics=true;
            app.ToggleSearch();await Task.Delay(150);
            app.Palette!.OpenApplicationForDiagnostics(new AppRef{Name="Search launch fixture",ExePath=exe,Arguments=browser?"--new-window about:blank":null});
            await Wait(()=>
            {
                NativeMethods.EnumWindows((h,_)=>
                {
                    if(previous.Contains(h.ToInt64())||!WindowFilter.IsCandidate(h,(uint)Environment.ProcessId))return true;
                    if(!NativeMethods.GetWindowRect(h,out var rect)||rect.Width<500||rect.Height<300)return true;
                    var info=WindowIdentityResolver.ResolveBasic(h);
                    if(string.Equals(info.ExePath,exe,StringComparison.OrdinalIgnoreCase)){target=h;return false;}return true;
                },IntPtr.Zero);return target!=IntPtr.Zero;
            },"Search did not open a new window.");
            // Wait for application initialization, without clicking its dock icon.
            WindowActivator.Activate(target);await Task.Delay(400);
            if(!browser)OwnWindowCapture.Capture(WindowSetup.Hwnd(back));
            for(int cycle=0;cycle<3;cycle++)
            {
                if(cycle>0){app.Dock.HoldWindowListForDiagnostics=false;app.Dock.ViewModel.SetWindows(app.Running.Snapshot(),app.Running.ForegroundRoot);NativeMethods.ShowWindowAsync(target,NativeMethods.SW_RESTORE);await Wait(()=>!NativeMethods.IsIconic(target),"Search window did not restore.");WindowActivator.Activate(target);await Task.Delay(350);}
                if(cycle==2){app.Settings.Update(s=>s.Edge=FloatingDock.Core.Configuration.DockEdge.Right);await Task.Delay(300);}
                if(!browser)WindowStyler.SetTopmost(target,true);
                var anchor=app.Msg.AnimationRectProvider?.Invoke(target);int first=trace.Count,replyStart=replies.Count;
                Check(anchor is not null,"Search window has no dock anchor.");
                if(cycle==0)Check(app.Dock.CurrentAnimationRectFor(target) is null,"The first-minimize race did not withhold the new HWND from the dock model.");
                var samples=new List<AnchorFrame>();
                NativeMethods.PostMessage(target,0x112,new(0xF020),IntPtr.Zero);
                if(cycle==1)Thread.Sleep(180); // Reproduce a busy search/layout UI thread.
                var timer=Stopwatch.StartNew();
                while(timer.ElapsedMilliseconds<350)
                {
                    if(!browser)
                    {
                        var frame=OwnWindowCapture.Capture(WindowSetup.Hwnd(back));int minX=frame.Width,minY=frame.Height,maxX=-1,maxY=-1,count=0;
                        for(int y=0;y<frame.Height;y+=2)for(int x=0;x<frame.Width;x+=2)
                        {int p=(y*frame.Width+x)*4;if(Math.Abs(frame.Pixels[p]-203)<4&&Math.Abs(frame.Pixels[p+1]-7)<4&&Math.Abs(frame.Pixels[p+2]-239)<4){count++;minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);}}
                        if(count>5)samples.Add(new(timer.Elapsed.TotalMilliseconds,monitor.Bounds.Left+minX,monitor.Bounds.Top+minY,maxX-minX+1,maxY-minY+1));
                    }
                    await Task.Delay(8);
                }
                await Wait(()=>NativeMethods.IsIconic(target),"Search window did not minimize.");
                Check(!app.Tiling.PresentationsForDiagnostics.ContainsKey(target.ToInt64()),"Native minimization also created a duplicate presentation.");
                var destinations=replies.ToArray().Skip(replyStart).Where(r=>r.Window==target).ToArray();
                Check(destinations.Length>0&&destinations.All(r=>r.Rect is { } actual&&Math.Abs(actual.Left-anchor!.Value.Left)<3&&Math.Abs(actual.Top-anchor.Value.Top)<3),"The native shell did not receive the current dock destination.");
                if(!browser&&cycle!=1)
                {
                    Check(samples.Count>=3&&samples.Max(s=>s.W)-samples.Min(s=>s.W)>150,"The native shrink path was not observed.");
                    double width=samples.Average(s=>(double)s.W),cx=samples.Average(s=>s.X+s.W/2d),cy=samples.Average(s=>s.Y+s.H/2d);
                    double variance=samples.Sum(s=>(s.W-width)*(s.W-width));
                    double endX=cx-width*samples.Sum(s=>(s.W-width)*(s.X+s.W/2d-cx))/variance;
                    double endY=cy-width*samples.Sum(s=>(s.W-width)*(s.Y+s.H/2d-cy))/variance;
                    Check(Math.Abs(endX-(anchor!.Value.Left+anchor.Value.Width/2d))<100&&Math.Abs(endY-(anchor.Value.Top+anchor.Value.Height/2d))<100,"Visible native animation still heads toward the taskbar instead of the dock.");
                    checks["nativeShrinkProjection"+cycle]=new{endX,endY,dockX=anchor.Value.Left+anchor.Value.Width/2d,dockY=anchor.Value.Top+anchor.Value.Height/2d};
                }
                cycles.Add(new{cycle,anchor,requests=trace.Skip(first).ToArray(),samples});
                checks["searchMinimizeCycles"]=cycles;
            }
            checks["allShellRequests"]=trace;checks["browser"]=browser;
        }
        finally
        {
            checks["allShellRequests"]=trace;
            app.Msg.AnimationRectDiagnostic=null;app.Palette?.Hide();
            if(target!=IntPtr.Zero)NativeMethods.PostMessage(target,0x10,IntPtr.Zero,IntPtr.Zero);
            back.Close();app.Dock.HoldFullscreenStateForDiagnostics=false;app.Dock.HoldWindowListForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var p in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
        }
    }
}
