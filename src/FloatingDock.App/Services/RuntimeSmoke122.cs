using System.Diagnostics;
using FloatingDock.Core.Configuration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    // Real HWNDs in an empty diagnostic profile; never tile the user's windows.
    private static async Task CheckUi122Async(DockApp app,Dictionary<string,object> checks)
    {
        var original=app.Settings.Current.Clone();var probes=new List<Window>();
        var originals=new Dictionary<long,RECT>();var sizes=new Dictionary<long,int>();
        var mon=app.Dock.PrimaryWindowForDiagnostics!.Monitor;
        app.Dock.HoldFullscreenStateForDiagnostics=true;
        try
        {
            Window Create(int offset)
            {
                var window=new Window { Title="FloatingDock opening fixture "+offset,Content=new Grid() };
                probes.Add(window);window.Activate();var hwnd=WindowSetup.Hwnd(window);
                WindowStyler.SetBounds(hwnd,mon.WorkArea.Left+240+offset,mon.WorkArea.Top+160,420,300,topmost:false);
                NativeMethods.GetWindowRect(hwnd,out var rect);originals[hwnd.ToInt64()]=rect;
                sizes[hwnd.ToInt64()]=0;window.SizeChanged+=(_,_)=>sizes[hwnd.ToInt64()]++;
                app.Running.IncludeOwnWindow(hwnd);return window;
            }
            Create(0);app.Tiling.DiagnosticWindows=originals.Keys.ToHashSet();
            app.Tiling.SkipHotkeyRegistrationForDiagnostics=true;
            app.Settings.Update(s=>{s.TilingEnabled=true;s.TilingModifier=TilingModifier.Alt;s.TilingExcludedApps="";s.TilingAnimationMs=400;s.TilingWorkspaces=3;});
            var ready=Stopwatch.StartNew();
            while(ready.ElapsedMilliseconds<3000&&(app.Tiling.Session?.ManagedCount!=1||app.Tiling.Animating))await Task.Delay(50);
            Check(app.Tiling.Session?.ManagedCount==1&&!app.Tiling.Animating,"First fixture did not settle.");
            sizes[originals.Keys.First()]=0;
            var second=Create(80);app.Tiling.Session!.DiagnosticWindows=originals.Keys.ToHashSet();
            ready.Restart();
            while(ready.ElapsedMilliseconds<3000&&app.Tiling.Session.ManagedCount!=2)await Task.Delay(20);
            Check(app.Tiling.Session.ManagedCount==2,"New fixture was not discovered.");
            // Simulate a burst of startup title/focus/model updates with unchanged targets.
            var timer=Stopwatch.StartNew();app.Tiling.AnimateLayout();
            while(timer.ElapsedMilliseconds<330){await Task.Delay(30);app.Tiling.AnimateLayout();}
            await Task.Delay(170);
            checks["opening"]=new {elapsedMs=timer.ElapsedMilliseconds,sizeEvents=sizes.Values.ToArray(),animationStopped=!app.Tiling.Animating};
            Check(!app.Tiling.Animating,"Unchanged startup targets restarted the animation deadline.");
            Check(sizes.Values.All(count=>count<=2),"Opening resized the application on every animation frame.");
            var session=app.Tiling.Session!;
            session.Reconcile(app.Running.Snapshot(),WindowSetup.Hwnd(second).ToInt64());
            session.ToggleDirection();app.Tiling.AnimateLayout();await Task.Delay(70);
            var secondHwnd=WindowSetup.Hwnd(second);NativeMethods.GetWindowRect(secondHwnd,out var startupRect);
            NativeMethods.SetWindowPos(secondHwnd,IntPtr.Zero,startupRect.Left,startupRect.Top,500,260,NativeMethods.SWP_NOZORDER|NativeMethods.SWP_NOACTIVATE);
            await Task.Delay(430);
            Check(!app.Tiling.Animating,"Startup size correction left an animation running.");
            checks["lateStartupResizeCorrected"]=true;
            foreach(var (id,target) in app.Tiling.Session.Targets())
            {
                NativeMethods.GetWindowRect(new(id),out var rect);
                Check(Math.Abs(rect.Left-target.X)<=2&&Math.Abs(rect.Top-target.Y)<=2&&Math.Abs(rect.Width-target.Width)<=2&&Math.Abs(rect.Height-target.Height)<=2,"Opening did not reach its final tile bounds.");
            }
            app.Settings.Update(s=>s.TilingAnimationMs=0);app.Tiling.Apply(app.Settings.Current);session.ToggleDirection();app.Tiling.AnimateLayout();
            Check(!app.Tiling.Animating,"Zero duration started the animation timer.");
            foreach(var (id,target) in session.Targets())
            {
                NativeMethods.GetWindowRect(new(id),out var rect);
                Check(rect.Left==target.X&&rect.Top==target.Y&&rect.Width==target.Width&&rect.Height==target.Height,"Immediate layout did not apply its final bounds.");
            }
            checks["zeroDurationPlacement"]=true;
            app.Settings.Update(s=>s.TilingEnabled=false);await Task.Delay(150);
            foreach(var (id,before) in originals)
            {
                NativeMethods.GetWindowRect(new(id),out var after);
                Check(Math.Abs(before.Left-after.Left)<=2&&Math.Abs(before.Top-after.Top)<=2&&Math.Abs(before.Width-after.Width)<=2&&Math.Abs(before.Height-after.Height)<=2,"Opening fix lost the original placement.");
            }
            checks["restoredBothWindows"]=true;
        }
        finally
        {
            app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);app.Tiling.DiagnosticWindows=null;
            foreach(var window in probes){app.Running.ExcludeOwnWindow(WindowSetup.Hwnd(window));window.Close();}
            app.Settings.Update(s=>{foreach(var p in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(original));});
            app.Dock.HoldFullscreenStateForDiagnostics=false;
        }
    }
}
