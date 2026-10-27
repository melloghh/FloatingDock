using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
    private static async Task CheckLaunch162Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();
        var rendered=new ConcurrentQueue<(long Id,double Time)>();
        void Rendered(long id,double time,FloatingDock.Core.Tiling.TileRect bounds,byte alpha)=>rendered.Enqueue((id,time));
        app.Tiling.PresentationRenderedForDiagnostics+=Rendered;
        var fixtures=new List<Window>();var handles=new List<IntPtr>();
        async Task Wait(Func<bool> ready,string error,int milliseconds=3000)
        {var watch=Stopwatch.StartNew();while(!ready()&&watch.ElapsedMilliseconds<milliseconds)await Task.Delay(5);Check(ready(),error);}
        try
        {
            app.Settings.Update(s=>{s.TilingEnabled=false;s.IconAnimation=true;s.MenuAnimations=true;s.AnimationFps=144;});
            await Task.Delay(180);
            app.Dock.HoldFullscreenStateForDiagnostics=true;
            foreach(string scenario in new[]{"resize","dispatcher","identity","coldRestore"})
            {
                var window=new Window {Title="FloatingDock launch regression "+scenario,Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,38,108,156))}};
                fixtures.Add(window);var hwnd=WindowSetup.Hwnd(window);handles.Add(hwnd);long id=hwnd.ToInt64();
                WindowStyler.SetBounds(hwnd,240,180,900,620,topmost:true);
                if(scenario=="identity")
                {
                    var iid=new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                    NativeMethods.SHGetPropertyStoreForWindow(hwnd,ref iid,out var store);
                    var key=new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),5);
                    var value=new PROPVARIANT {vt=31,pointerValue=Marshal.StringToCoTaskMemUni("FloatingDock.LaunchRegression!App")};
                    try {Marshal.ThrowExceptionForHR(((IPropertyStore)store).SetValue(ref key,ref value));Marshal.ThrowExceptionForHR(((IPropertyStore)store).Commit());}
                    finally {NativeMethods.PropVariantClear(ref value);Marshal.FinalReleaseComObject(store);}
                }
                app.Tiling.DiagnosticWindows=handles.Select(h=>h.ToInt64()).ToHashSet();
                if(scenario=="coldRestore")
                {window.Activate();NativeMethods.ShowWindow(hwnd,NativeMethods.SW_MINIMIZE);await Wait(()=>NativeMethods.IsIconic(hwnd),"Cold fixture did not minimize.");}
                app.Running.IncludeOwnWindow(hwnd);
                if(scenario=="coldRestore")await Wait(()=>app.Running.Snapshot().Any(w=>w.Hwnd==id),"Cold minimized window was not discovered.");
                app.Tiling.NoteLaunch(scenario=="identity"?new AppRef{Name="Packaged identity",Aumid="FloatingDock.LaunchRegression!App"}:new AppRef{Name="Opening fixture",ExePath=Environment.ProcessPath});
                if(scenario!="coldRestore")
                {
                    window.Activate();
                    // Register the now-visible owned fixture as well; its own-
                    // process hook was first installed while it was hidden.
                    app.Running.IncludeOwnWindow(hwnd);
                }
                await Wait(()=>app.Tiling.PresentationsForDiagnostics.ContainsKey(id),"No opening animation: "+scenario);
                var watch=Stopwatch.StartNew();
                if(scenario=="resize")
                {
                    await Task.Delay(55);WindowStyler.SetBounds(hwnd,300,210,1060,680,topmost:true);
                    await Wait(()=>!app.Tiling.PresentationsForDiagnostics.ContainsKey(id),"Opening stayed at stale startup bounds.",700);
                    Check(watch.ElapsedMilliseconds<650,"Startup resize stalled the opening transition.");
                }
                else if(scenario is "dispatcher" or "coldRestore")
                {
                    await Task.Delay(40);var before=app.Tiling.PresentationsForDiagnostics[id];
                    double stalled=Stopwatch.GetTimestamp()*1000d/Stopwatch.Frequency;
                    Thread.Sleep(130);
                    var after=app.Tiling.PresentationsForDiagnostics[id];
                    Check(after.Bounds!=before.Bounds&&(scenario=="coldRestore"||after.Opacity>before.Opacity),"Window transition freezes while the dispatcher is busy.");
                    checks[scenario+"DuringDispatcherStall"]=new{before=before.Opacity,after=after.Opacity};
                    var frames=rendered.Where(f=>f.Id==id&&f.Time>=stalled).ToArray();
                    Check(frames.Length>=8,"Native compositor updates stopped during dispatcher work.");
                    checks[scenario+"NativeFramesDuring130msStall"]=frames.Length;
                }
                await Wait(()=>!app.Tiling.Animating,"Opening did not release its presentation.");
                Check((NativeMethods.GetExStyle(hwnd)&0x80000)==0,"Opening leaked source opacity: "+scenario);
                Check(!NativeMethods.IsIconic(hwnd),"Restored window is still minimized.");
                checks["launch_"+scenario+"Milliseconds"]=watch.ElapsedMilliseconds;
                app.Running.ExcludeOwnWindow(hwnd);window.Close();fixtures.Remove(window);
            }
        }
        finally
        {
            app.Tiling.PresentationRenderedForDiagnostics-=Rendered;
            foreach(var hwnd in handles)app.Running.ExcludeOwnWindow(hwnd);
            foreach(var window in fixtures)window.Close();
            app.Tiling.DiagnosticWindows=null;app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var p in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
        }
    }

    private static async Task CheckRealWindows162Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var foreground=NativeMethods.GetForegroundWindow();
        var placements=new List<(IntPtr Hwnd,NativeTilingSession.Placement Placement)>();
        var frames=new ConcurrentQueue<(long Id,double Time)>();
        void Rendered(long id,double time,FloatingDock.Core.Tiling.TileRect bounds,byte alpha)=>frames.Enqueue((id,time));
        async Task Wait(Func<bool> ready,string error)
        {var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<4000)await Task.Delay(5);Check(ready(),error);}
        try
        {
            app.Settings.Update(s=>{s.TilingEnabled=false;s.IconAnimation=true;s.MenuAnimations=true;s.AnimationFps=144;s.IconAnimationMs=280;});
            await Task.Delay(250);app.Running.FullScan();await Task.Delay(250);
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"zen.exe","chrome.exe","firefox.exe","msedge.exe","explorer.exe","telegram.exe"};
            var windows=app.Running.Snapshot().Where(w=>w.ExeFileName is { } name&&names.Contains(name)).GroupBy(w=>w.ExeFileName,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToArray();
            Check(windows.Length>=2,"At least two real app fixtures are required.");
            app.Tiling.DiagnosticWindows=windows.Select(w=>w.Hwnd).ToHashSet();app.Dock.HoldFullscreenStateForDiagnostics=true;
            app.Tiling.PresentationRenderedForDiagnostics+=Rendered;
            var results=new List<object>();
            foreach(var window in windows)
            {
                var hwnd=new IntPtr(window.Hwnd);var placement=new NativeTilingSession.Placement {Length=Marshal.SizeOf<NativeTilingSession.Placement>()};
                Check(GetFixturePlacement(hwnd,ref placement),"Could not preserve real fixture placement.");placements.Add((hwnd,placement));
                long originalStyle=NativeMethods.GetExStyle(hwnd);
                if(NativeMethods.IsIconic(hwnd))NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);
                await Wait(()=>!NativeMethods.IsIconic(hwnd),"Real fixture did not restore.");await Task.Delay(160);
                for(int cycle=0;cycle<2;cycle++)
                {
                    Check(app.Tiling.MinimizeToDock(hwnd),"Real minimize request failed.");
                    await Wait(()=>!app.Tiling.Animating&&NativeMethods.IsIconic(hwnd),"Real minimize did not settle.");
                    frames.Clear();var clock=Stopwatch.StartNew();
                    var activation=WindowActivator.ActivateOrMinimizeAsync(new[]{window with{IsMinimized=true}},false,null,w=>app.Tiling.PrepareActivation(new[]{w}));
                    await Wait(()=>app.Tiling.PresentationsForDiagnostics.ContainsKey(window.Hwnd),"Real restore has no animation.");
                    await Task.Delay(35);Thread.Sleep(130);
                    await activation;await Wait(()=>!app.Tiling.Animating&&!NativeMethods.IsIconic(hwnd),"Real restore did not settle.");
                    var times=frames.Where(f=>f.Id==window.Hwnd).Select(f=>f.Time).ToArray();
                    var gaps=times.Zip(times.Skip(1),(a,b)=>b-a).Order().ToArray();
                    Check(times.Length>=20,"Real restore produced too few native frames.");
                    Check((NativeMethods.GetExStyle(hwnd)&0x80000)==(originalStyle&0x80000),"Real restore leaked layered style.");
                    results.Add(new{app=window.ExeFileName,cycle,frames=times.Length,maxGap=gaps.DefaultIfEmpty().Max(),p95Gap=gaps.Length>0?gaps[(int)((gaps.Length-1)*.95)]:0,total=clock.ElapsedMilliseconds});
                }
            }
            checks["realApplicationRestores"]=results;
        }
        finally
        {
            app.Tiling.PresentationRenderedForDiagnostics-=Rendered;
            app.Tiling.HidePresentationsForCapture();
            foreach(var (hwnd,original) in placements)
            {
                if(!NativeMethods.IsWindow(hwnd))continue;
                var placement=original;placement.Flags|=4;SetFixturePlacement(hwnd,ref placement);
                await Wait(()=>
                {
                    var actual=new NativeTilingSession.Placement {Length=Marshal.SizeOf<NativeTilingSession.Placement>()};
                    return GetFixturePlacement(hwnd,ref actual)&&actual.Show==original.Show&&actual.Normal.Left==original.Normal.Left&&actual.Normal.Top==original.Normal.Top&&actual.Normal.Right==original.Normal.Right&&actual.Normal.Bottom==original.Normal.Bottom;
                },"Real application placement was not restored after diagnostics.");
            }
            app.Tiling.DiagnosticWindows=null;app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var p in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
            NativeMethods.SetForegroundWindow(foreground);
        }
    }
}
