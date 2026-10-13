using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Models;
using FloatingDock.Core.Tiling;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    private static async Task CheckBrowserRestore152Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var foreground=NativeMethods.GetForegroundWindow();
        var browsers=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"zen.exe","firefox.exe","chrome.exe","msedge.exe","brave.exe","opera.exe","vivaldi.exe"};
        await Task.Delay(400);app.Running.FullScan();await Task.Delay(200);
        var browser=app.Running.Snapshot().FirstOrDefault(w=>w.ExeFileName is { } exe&&browsers.Contains(exe));
        Check(browser is not null,"No existing browser window is available.");long id=browser!.Hwnd;var hwnd=new IntPtr(id);
        var original=new NativeTilingSession.Placement{Length=Marshal.SizeOf<NativeTilingSession.Placement>()};
        Check(GetFixturePlacement(hwnd,ref original),"Browser placement could not be preserved.");
        Window? companion=null;var records=new List<object>();IntPtr otherHandle=IntPtr.Zero;int styleChanges=0;
        MinimumProc styleProbe=(h,m,w,l,subclass,data)=>{if(m==0x7D&&w.ToInt64()==NativeMethods.GWL_EXSTYLE)styleChanges++;return DefSubclassProc(h,m,w,l);};
        async Task Wait(Func<bool> ready,string error)
        {var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<4500)await Task.Delay(12);Check(ready(),error);}
        try
        {
            app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);
            companion=new Window{Title="Owned browser restore companion",Content=new Grid{Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,38,108,156))}};
            var other=WindowSetup.Hwnd(companion);otherHandle=other;var ids=new HashSet<long>{id,other.ToInt64()};
            app.Dock.HoldFullscreenStateForDiagnostics=true;app.Tiling.SkipHotkeyRegistrationForDiagnostics=true;app.Tiling.DiagnosticWindows=ids;
            WindowStyler.SetBounds(other,200,200,800,650);companion.Activate();app.Running.IncludeOwnWindow(other);
            NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);await Wait(()=>!NativeMethods.IsIconic(hwnd),"Browser did not restore initially.");
            app.Settings.Update(s=>{s.TilingEnabled=true;s.TilingExcludedApps="";s.TilingAnimationMs=110;s.IconAnimationMs=240;s.TilingStartupDelayMs=80;s.TilingAnimationFps=144;});app.Tiling.Apply(app.Settings.Current);
            await Wait(()=>app.Tiling.Session?.Targets().ContainsKey(id)==true&&!app.Tiling.Animating,"Browser did not join the initial tiles.");
            for(int cycle=0;cycle<3;cycle++)
            {
                Check(app.Tiling.MinimizeToDock(hwnd),"Browser minimization was refused.");await Wait(()=>NativeMethods.IsIconic(hwnd)&&!app.Tiling.Animating,"Browser minimization did not settle.");
                var clock=Stopwatch.StartNew();var frames=new List<TileRect>();double firstSurface=-1,nativeRestored=-1,maxGap=0,last=0,prepareMs=0;
                var activation=WindowActivator.ActivateOrMinimizeAsync(new[]{browser with{IsMinimized=true}},true,app.Tiling.MinimizeToDock,w=>{var start=clock.Elapsed.TotalMilliseconds;app.Tiling.PrepareActivation(new[]{w});prepareMs=clock.Elapsed.TotalMilliseconds-start;});
                do
                {
                    double now=clock.Elapsed.TotalMilliseconds;maxGap=Math.Max(maxGap,now-last);last=now;
                    if(!NativeMethods.IsIconic(hwnd)&&nativeRestored<0)nativeRestored=now;
                    if(app.Tiling.PresentationsForDiagnostics.TryGetValue(id,out var p)){if(firstSurface<0)firstSurface=now;frames.Add(p.Bounds);}
                    await Task.Delay(8);
                }while((!activation.IsCompleted||app.Tiling.Animating)&&clock.ElapsedMilliseconds<4500);
                Check(await activation==WindowActivator.ActivationResult.Activated,"Browser dock activation failed.");
                Check(!app.Tiling.Animating&&!NativeMethods.IsIconic(hwnd),"Browser restoration did not finish.");
                Check(frames.Distinct().Count()>=12,"Browser restoration did not produce continuous animation frames.");
                if(GetLayeredWindowAttributes(hwnd,out _,out byte alpha,out _))Check(alpha!=0,"Browser stayed transparent after restore.");
                records.Add(new{cycle,prepareMs,firstSurface,nativeRestored,maxGap,total=clock.Elapsed.TotalMilliseconds,frames=frames.Distinct().Count(),first=frames.FirstOrDefault(),last=frames.LastOrDefault()});
            }
            SetWindowSubclass(other,styleProbe,new(152),UIntPtr.Zero);
            Check(app.Tiling.MinimizeToDock(other),"Companion minimization was refused.");await Wait(()=>NativeMethods.IsIconic(other)&&!app.Tiling.Animating,"Companion minimization did not settle.");
            styleChanges=0;
            app.Tiling.PrepareActivation(new[]{new WindowInfo(other.ToInt64(),Environment.ProcessId,Environment.ProcessPath,null,"Owned composition fixture",null,true)});
            Check(styleChanges==1,"Restore preparation restarted the source's layered composition more than once.");
            checks["restorePreparationStyleChanges"]=styleChanges;
            await Wait(()=>!app.Tiling.Animating&&!NativeMethods.IsIconic(other),"Companion restoration did not settle.");
            Check(app.Tiling.MinimizeToDock(hwnd),"Rapid browser minimization was refused.");await Task.Delay(65);
            var reversing=app.Tiling.PresentationsForDiagnostics[id];
            var rapid=WindowActivator.ActivateOrMinimizeAsync(new[]{browser with{IsMinimized=true}},true,app.Tiling.MinimizeToDock,w=>app.Tiling.PrepareActivation(new[]{w}));
            Check(app.Tiling.PresentationsForDiagnostics.TryGetValue(id,out var reversed)&&reversing.Handle==reversed.Handle,"Rapid browser restoration discarded the moving compositor surface.");
            Check(await rapid==WindowActivator.ActivationResult.Activated,"Rapid browser activation failed.");
            await Wait(()=>!app.Tiling.Animating&&!NativeMethods.IsIconic(hwnd),"Rapid browser reversal did not settle.");
            checks["rapidBrowserReversal"]=true;
            checks["browserDockRestores"]=records;checks["browserExecutable"]=browser.ExeFileName!;
        }
        finally
        {
            app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);
            if(companion is not null){if(otherHandle!=IntPtr.Zero)RemoveWindowSubclass(otherHandle,styleProbe,new(152));app.Running.ExcludeOwnWindow(WindowSetup.Hwnd(companion));companion.Close();}
            var placement=original;placement.Flags|=4;SetFixturePlacement(hwnd,ref placement);
            await Wait(()=>{var p=new NativeTilingSession.Placement{Length=Marshal.SizeOf<NativeTilingSession.Placement>()};return GetFixturePlacement(hwnd,ref p)&&p.Show==original.Show&&p.Normal.Left==original.Normal.Left&&p.Normal.Top==original.Normal.Top&&p.Normal.Right==original.Normal.Right&&p.Normal.Bottom==original.Normal.Bottom;},"Browser placement was not restored after diagnostics.");
            app.Dock.HoldFullscreenStateForDiagnostics=false;app.Tiling.DiagnosticWindows=null;
            app.Settings.Update(s=>{foreach(var property in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(s,property.GetValue(saved));});
            NativeMethods.SetForegroundWindow(foreground);checks["browserOriginalPlacementRestored"]=true;
        }
    }
}
