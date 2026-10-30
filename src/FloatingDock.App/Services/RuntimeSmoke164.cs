using System.Diagnostics;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FloatingDock.UI.Services;

internal static partial class RuntimeSmoke
{
    private static async Task CheckState164Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();Window? owned=null;IntPtr hwnd=IntPtr.Zero;
        var records=new List<object>();checks["windowStateCycles"]=records;
        async Task Wait(Func<bool> ready,string error)
        {var timer=Stopwatch.StartNew();while(!ready()&&timer.ElapsedMilliseconds<4000)await Task.Delay(10);Check(ready(),error);}
        try
        {
            app.Settings.Update(s=>{s.TilingEnabled=false;s.IconAnimation=true;});await Task.Delay(150);
            var args=Environment.GetCommandLineArgs();int external=Array.IndexOf(args,"--test-state-window");
            if(external>=0)
            {
                hwnd=new(long.Parse(args[external+1]));var identity=WindowIdentityResolver.ResolveBasic(hwnd);
                Check(identity.ExeFileName.Equals("zen.exe",StringComparison.OrdinalIgnoreCase)&&identity.Title.Contains("FloatingDock state probe 164",StringComparison.Ordinal),"State probe may only target the dedicated Zen test window.");
            }
            else
            {
                owned=new Window{Title="FloatingDock maximized state probe 164",Content=new Grid()};owned.Activate();hwnd=WindowSetup.Hwnd(owned);
                app.Running.IncludeOwnWindow(hwnd);NativeMethods.ShowWindow(hwnd,NativeMethods.SW_MAXIMIZE);
            }
            await Wait(()=>!NativeMethods.IsIconic(hwnd),"State probe must start visible.");await Task.Delay(450);
            var info=WindowIdentityResolver.ResolveBasic(hwnd);bool zoomed=NativeMethods.IsZoomed(hwnd);
            Check(NativeMethods.GetWindowRect(hwnd,out var before),"Cannot read state probe bounds.");
            bool fullscreen=FullscreenDetector.IsWindowFullscreenOn(hwnd,NativeMethods.MonitorFromWindow(hwnd,NativeMethods.MONITOR_DEFAULTTONEAREST),0);
            checks["initialState"]=new{app=info.ExeFileName,zoomed,fullscreen,bounds=new{before.Left,before.Top,before.Right,before.Bottom}};
            Check(zoomed||fullscreen,"State probe must start maximized or fullscreen.");
            foreach(string path in new[]{"dock","caption","preview","launch"})
            for(int cycle=0;cycle<3;cycle++)
            {
                if(path=="caption")NativeMethods.PostMessage(hwnd,0x112,new(0xF020),IntPtr.Zero);
                else Check(app.Tiling.MinimizeToDock(hwnd),"State probe minimize was rejected.");
                await Wait(()=>NativeMethods.IsIconic(hwnd),"State probe did not minimize.");await Task.Delay(380);
                if(path=="launch")app.Tiling.NoteLaunch(new AppRef{Name="State probe",ExePath=info.ExePath});
                else
                {
                    if(path=="preview")app.Tiling.PrepareActivation(new[]{info});
                    await WindowActivator.ActivateOrMinimizeAsync(new[]{info},false,null,path=="preview"?null:w=>app.Tiling.PrepareActivation(new[]{w}));
                }
                await Wait(()=>!NativeMethods.IsIconic(hwnd),"State probe did not restore.");await Task.Delay(400);
                Check(NativeMethods.GetWindowRect(hwnd,out var after),"Cannot read restored bounds.");
                bool sameZoom=NativeMethods.IsZoomed(hwnd)==zoomed;
                bool sameBounds=Math.Abs(after.Left-before.Left)<=2&&Math.Abs(after.Top-before.Top)<=2&&Math.Abs(after.Right-before.Right)<=2&&Math.Abs(after.Bottom-before.Bottom)<=2;
                bool sameFullscreen=FullscreenDetector.IsWindowFullscreenOn(hwnd,NativeMethods.MonitorFromWindow(hwnd,NativeMethods.MONITOR_DEFAULTTONEAREST),0)==fullscreen;
                records.Add(new{path,cycle,sameZoom,sameBounds,sameFullscreen,zoomed=NativeMethods.IsZoomed(hwnd),bounds=new{after.Left,after.Top,after.Right,after.Bottom}});
                Check(sameZoom&&sameBounds&&sameFullscreen,"Minimize/restore changed the maximized or fullscreen state.");
            }
        }
        finally
        {
            if(owned is not null){app.Running.ExcludeOwnWindow(hwnd);owned.Close();}
            app.Settings.Update(s=>{foreach(var p in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
        }
    }
}
