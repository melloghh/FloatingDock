using System.Diagnostics;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    // Retain existing diagnostic entry points while checking the native policy.
    private static Task CheckRealWindows162Async(DockApp app,Dictionary<string,object> checks)=>CheckNative163Async(app,checks);
    private static async Task CheckLaunch162Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var windows=new List<Window>();
        var records=new List<object>();
        async Task Wait(Func<bool> ready,string error)
        {var timer=Stopwatch.StartNew();while(!ready()&&timer.ElapsedMilliseconds<3000)await Task.Delay(5);Check(ready(),error);}
        try
        {
            app.Settings.Update(s=>{s.TilingEnabled=false;s.IconAnimation=true;s.MenuAnimations=true;});await Task.Delay(180);
            foreach(string scenario in new[]{"resize","dispatcher","coldRestore"})
            {
                var window=new Window{Title="FloatingDock native regression "+scenario,Content=new Grid{Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,38,108,156))}};
                windows.Add(window);var hwnd=WindowSetup.Hwnd(window);long id=hwnd.ToInt64();
                WindowStyler.SetBounds(hwnd,240,180,900,620,topmost:true);
                NativeMethods.DwmGetWindowAttribute(hwnd,3,out int originalTransitions,4);
                long originalLayer=NativeMethods.GetExStyle(hwnd)&NativeMethods.WS_EX_LAYERED;
                int viewports=WindowTransitionSurface.CreatedViewportCount;
                if(scenario=="coldRestore")
                {window.Activate();NativeMethods.ShowWindow(hwnd,NativeMethods.SW_MINIMIZE);await Wait(()=>NativeMethods.IsIconic(hwnd),"Cold native fixture did not minimize.");}
                app.Running.IncludeOwnWindow(hwnd);
                if(scenario=="coldRestore")await Wait(()=>app.Running.Snapshot().Any(w=>w.Hwnd==id),"Cold fixture was not discovered.");
                app.Tiling.NoteLaunch(new AppRef{Name="Native opening",ExePath=Environment.ProcessPath});
                if(scenario!="coldRestore"){window.Activate();app.Running.IncludeOwnWindow(hwnd);}
                var timer=Stopwatch.StartNew();int samples=0;
                while(timer.ElapsedMilliseconds<450)
                {
                    if(scenario=="resize"&&samples==4)WindowStyler.SetBounds(hwnd,300,210,1060,680,topmost:true);
                    if(scenario=="dispatcher"&&samples==4)Thread.Sleep(130);
                    Check(!app.Tiling.PresentationsForDiagnostics.ContainsKey(id),"Native window has a duplicate compositor image.");
                    Check((NativeMethods.GetExStyle(hwnd)&NativeMethods.WS_EX_LAYERED)==originalLayer,"Native source composition style changed.");
                    NativeMethods.DwmGetWindowAttribute(hwnd,3,out int transitions,4);
                    Check(transitions==originalTransitions,"Native source transitions were suppressed.");
                    Check(!GetLayeredWindowAttributes(hwnd,out _,out byte a,out _)||a!=0,"Native source was hidden while painting.");
                    samples++;await Task.Delay(8);
                }
                Check(!NativeMethods.IsIconic(hwnd)&&!app.Tiling.Animating,"Native window did not settle.");
                Check(WindowTransitionSurface.CreatedViewportCount==viewports,"Native opening allocated a thumbnail viewport.");
                Check(!app.Tiling.CaptionRoutingInstalled,"Caption interception remained active without tiling.");
                records.Add(new{scenario,samples,milliseconds=timer.ElapsedMilliseconds,sourceStylePreserved=true,noThumbnail=true});
                app.Running.ExcludeOwnWindow(hwnd);window.Close();windows.Remove(window);
            }
            checks["nativeOpeningAndColdRestore"]=records;
        }
        finally
        {
            foreach(var window in windows){app.Running.ExcludeOwnWindow(WindowSetup.Hwnd(window));window.Close();}
            app.Settings.Update(s=>{foreach(var p in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
        }
    }
}
