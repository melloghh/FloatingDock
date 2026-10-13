using System.Diagnostics;
using FloatingDock.Core.Configuration;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    private static async Task CheckPerformance153Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;var dock=app.Dock.PrimaryWindowForDiagnostics!;
        var windows=new List<Window>();var ids=new HashSet<long>();
        app.Dock.HoldFullscreenStateForDiagnostics=true;dock.IgnoreRealCursorForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;app.Tiling.SkipHotkeyRegistrationForDiagnostics=true;
        async Task Measure(string name,Func<Task> workload)
        {
            int created=WindowTransitionSurface.CreatedViewportCount,reused=WindowTransitionSurface.ReusedViewportCount;
            using var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;long allocated=GC.GetTotalAllocatedBytes();var clock=Stopwatch.StartNew();
            await workload();double elapsed=clock.Elapsed.TotalMilliseconds;double used=(process.TotalProcessorTime-cpu).TotalMilliseconds;
            checks[name]=new{cpuMs=used,durationMs=elapsed,cpuPercentOfOneCore=100*used/elapsed,cpuPercentOfMachine=100*used/elapsed/Environment.ProcessorCount,allocatedBytes=GC.GetTotalAllocatedBytes()-allocated,logicalProcessors=Environment.ProcessorCount,createdViewports=WindowTransitionSurface.CreatedViewportCount-created,reusedViewports=WindowTransitionSurface.ReusedViewportCount-reused};
        }
        async Task Wait(Func<bool> ready,string error)
        {var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<5000)await Task.Delay(12);Check(ready(),error);}
        try
        {
            app.Settings.Update(s=>{s.TilingEnabled=false;s.TopBarEnabled=true;s.TopBarAutoHide=false;s.TopBarAnimationFps=144;s.MenuAnimations=true;s.MenuAnimationMs=200;});app.Tiling.Apply(app.Settings.Current);
            bar.SetFullscreen(false);bar.Target(false);await Task.Delay(500);
            bar.OpenMenuForDiagnostics("main");await Task.Delay(350);bar.Menus.Close();await Task.Delay(220);
            await Measure("panelIdle",()=>Task.Delay(2500));
            bar.OpenMenuForDiagnostics("main");await Task.Delay(350);
            await Measure("menuIdle",()=>Task.Delay(2500));bar.Menus.Close();await Task.Delay(220);
            await Measure("menuInteractions",async()=>
            {
                foreach(var kind in Enumerable.Range(0,4).SelectMany(_=>new[]{"main","sound","calendar"}))
                {bar.OpenMenuForDiagnostics(kind);await Task.Delay(330);bar.Menus.Close();await Task.Delay(190);}
            });
            app.Tiling.DiagnosticWindows=ids;
            app.Settings.Update(s=>{s.TilingEnabled=true;s.TilingExcludedApps="";s.TilingAnimationFps=144;s.TilingAnimationMs=110;s.TilingStartupDelayMs=80;});app.Tiling.Apply(app.Settings.Current);
            await Measure("tileOpenings",async()=>
            {
                for(int i=0;i<6;i++)
                {
                    var window=new Window{Title="Owned performance fixture",Content=new Grid{Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,38,108,156))}};
                    windows.Add(window);var hwnd=WindowSetup.Hwnd(window);long id=hwnd.ToInt64();ids.Add(id);
                    if(app.Tiling.Session is { } session)session.DiagnosticWindows=ids;
                    WindowStyler.SetBounds(hwnd,150+i*20,200,800,650);window.Activate();app.Running.IncludeOwnWindow(hwnd);
                    await Wait(()=>app.Tiling.Session?.IsManaged(id)==true&&!app.Tiling.Animating,"Opening tile did not settle.");
                    if(windows.Count>2){var old=windows[0];var oldHwnd=WindowSetup.Hwnd(old);app.Running.ExcludeOwnWindow(oldHwnd);old.Close();windows.RemoveAt(0);ids.Remove(oldHwnd.ToInt64());await Task.Delay(350);}
                }
            });
            await Measure("tileIdle",()=>Task.Delay(2000));
            Check(WindowTransitionSurface.ReusedViewportCount>=4,"Tile reflows did not reuse the transparent destination buffers.");
            Check(WindowTransitionSurface.CachedViewportCount<=1,"Idle tiling retained too many native destination buffers.");
            foreach(var window in windows)
                if(GetLayeredWindowAttributes(WindowSetup.Hwnd(window),out _,out byte alpha,out _))Check(alpha!=0,"A reused tile buffer left its source invisible.");
        }
        finally
        {
            bar.Menus.Close(immediately:true);app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);
            Check(WindowTransitionSurface.CachedViewportCount==0,"Disabling tiling did not release cached native buffers.");
            foreach(var window in windows){app.Running.ExcludeOwnWindow(WindowSetup.Hwnd(window));window.Close();}
            app.Tiling.DiagnosticWindows=null;app.Dock.HoldFullscreenStateForDiagnostics=false;dock.IgnoreRealCursorForDiagnostics=false;bar.IgnoreRealCursorForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var property in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(s,property.GetValue(saved));});
        }
    }
}
