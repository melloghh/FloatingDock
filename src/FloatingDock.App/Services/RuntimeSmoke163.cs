using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Models;
using FloatingDock.UI.Dock;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;
using FloatingDock.Windows.Shell;
using FloatingDock.Windows.WindowManagement;

namespace FloatingDock.UI.Services;

internal static partial class RuntimeSmoke
{
    private static async Task CheckNative163Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var foreground=NativeMethods.GetForegroundWindow();
        var created=new HashSet<IntPtr>();var records=new List<object>();
        bool probeOnly=Environment.GetCommandLineArgs().Contains("--test-probe-only");
        async Task Wait(Func<bool> ready,string error)
        {var timer=Stopwatch.StartNew();while(!ready()&&timer.ElapsedMilliseconds<8000)await Task.Delay(10);Check(ready(),error);}
        var monitor=app.Dock.PrimaryWindowForDiagnostics!.Monitor;
        using var viewport=new NativeDockWindow();using var alpha=new AlphaWindow(viewport.Handle);using var graphics=new SystemDockGraphics(viewport.Handle);
        WindowStyler.SetBounds(viewport.Handle,monitor.Bounds.Left,monitor.Bounds.Top,monitor.Bounds.Width,monitor.Bounds.Height,topmost:false);
        NativeMethods.SetWindowLongPtr(viewport.Handle,NativeMethods.GWL_EXSTYLE,new(NativeMethods.GetExStyle(viewport.Handle)|NativeMethods.WS_EX_TRANSPARENT));
        NativeMethods.ShowWindow(viewport.Handle,NativeMethods.SW_SHOWNA);
        try
        {
            app.Settings.Update(s=>{s.TilingEnabled=false;s.IconAnimation=true;s.MenuAnimations=true;s.AnimationFps=144;s.Edge=FloatingDock.Core.Configuration.DockEdge.Bottom;s.AutoHide=false;s.ShowRunning=true;});
            await Task.Delay(200);app.Dock.HoldFullscreenStateForDiagnostics=true;
            var explorer=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe");
            var chrome=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Google/Chrome/Application/chrome.exe");
            Check(File.Exists(chrome),"The installed Chrome fixture is unavailable.");
            var folder=Path.Combine(app.Paths.Root,"FloatingDock Explorer startup probe");Directory.CreateDirectory(folder);
            var browserProfile=Path.Combine(app.Paths.Root,"BrowserProfile");
            var page="data:text/html,"+Uri.EscapeDataString("<title>FloatingDock browser render probe</title><style>html{background:#266c9c;color:white;font:40px sans-serif}body{margin:60px}</style>FloatingDock render probe");
            var fixtures=new[]{new AppRef{Name="Explorer startup probe",ExePath=explorer,Arguments="/n,\""+folder+"\""},new AppRef{Name="Browser startup probe",ExePath=chrome,Arguments="--no-first-run --no-default-browser-check --disable-background-networking --disable-sync --disable-extensions --user-data-dir=\""+browserProfile+"\" --new-window \""+page+"\""}};
            foreach(var fixture in fixtures)
            {
                app.Pinned.Add(fixture);await Task.Delay(150);
                var existing=new HashSet<long>();NativeMethods.EnumWindows((h,_)=>{existing.Add(h.ToInt64());return true;},IntPtr.Zero);
                app.Tiling.NoteLaunch(fixture,restoreExisting:false);
                int browserPid=0;
                var launch=app.Worker.Run(()=>
                {
                    if(fixture.ExePath==explorer)return ShellLauncher.Launch(fixture,true);
                    using var process=Process.Start(new ProcessStartInfo(fixture.ExePath!,fixture.Arguments!){UseShellExecute=false});
                    if(process is null)return false;Volatile.Write(ref browserPid,process.Id);return true;
                });
                IntPtr target=IntPtr.Zero;var clock=Stopwatch.StartNew();var captureStart=-1d;
                var frames=new List<(double Ms,OwnWindowCapture.Frame Frame)>();int layered=0,hidden=0,presentations=0;
                NativeMethods.GetWindowRect(app.Dock.PrimaryWindowForDiagnostics.Handle,out var dockRect);
                var crop=new RECT {Left=Math.Max(monitor.Bounds.Left,dockRect.Left-24),Right=Math.Min(monitor.Bounds.Right,dockRect.Right+24),Top=Math.Max(monitor.Bounds.Top,dockRect.Top-120),Bottom=Math.Min(monitor.Bounds.Bottom,dockRect.Bottom)};
                while(clock.ElapsedMilliseconds<8000&&(target==IntPtr.Zero||clock.Elapsed.TotalMilliseconds-captureStart<650))
                {
                    if(target==IntPtr.Zero)
                    {
                        NativeMethods.EnumWindows((h,_)=>
                        {
                            if(existing.Contains(h.ToInt64())||!WindowFilter.IsCandidate(h,(uint)Environment.ProcessId))return true;
                            if(!AppMatching.Matches(fixture,WindowIdentityResolver.ResolveBasic(h)))return true;
                            NativeMethods.GetWindowThreadProcessId(h,out var owner);
                            bool owned=fixture.ExePath==explorer?NativeMethods.GetTitle(h).Contains("FloatingDock Explorer startup probe",StringComparison.OrdinalIgnoreCase):owner==Volatile.Read(ref browserPid);
                            if(owned){target=h;created.Add(h);captureStart=clock.Elapsed.TotalMilliseconds;return false;}
                            return true;
                        },IntPtr.Zero);
                    }
                    if(target!=IntPtr.Zero)
                    {
                        if((NativeMethods.GetExStyle(target)&NativeMethods.WS_EX_LAYERED)!=0)layered++;
                        if(GetLayeredWindowAttributes(target,out _,out byte a,out _)&&a==0)hidden++;
                        presentations+=app.Tiling.PresentationsForDiagnostics.ContainsKey(target.ToInt64())?1:0;
                    }
                    frames.Add((clock.Elapsed.TotalMilliseconds,OwnWindowCapture.Capture(viewport.Handle,crop)));
                    await Task.Delay(7);
                }
                Check(await launch&&target!=IntPtr.Zero,"A real application did not open a fresh fixture window.");
                app.Tiling.DiagnosticWindows=created.Select(h=>h.ToInt64()).ToHashSet();
                await Wait(()=>!app.Tiling.Animating,"Startup transition did not settle.");
                foreach(var stage in new[]{"startup","restore"})
                {
                    if(stage=="restore")
                    {
                        app.Tiling.MinimizeToDock(target);await Wait(()=>NativeMethods.IsIconic(target),"Real fixture did not minimize.");await Task.Delay(420);
                        frames.Clear();clock.Restart();layered=hidden=presentations=0;
                        var info=WindowIdentityResolver.ResolveBasic(target);
                        var activation=WindowActivator.ActivateOrMinimizeAsync(new[]{info},false,null,w=>app.Tiling.PrepareActivation(new[]{w}));
                        while(clock.ElapsedMilliseconds<650)
                        {
                            if((NativeMethods.GetExStyle(target)&NativeMethods.WS_EX_LAYERED)!=0)layered++;
                            if(GetLayeredWindowAttributes(target,out _,out byte a,out _)&&a==0)hidden++;
                            presentations+=app.Tiling.PresentationsForDiagnostics.ContainsKey(target.ToInt64())?1:0;
                            frames.Add((clock.Elapsed.TotalMilliseconds,OwnWindowCapture.Capture(viewport.Handle,crop)));await Task.Delay(7);
                        }
                        await activation;
                    }
                    string prefix=(fixture.ExePath==explorer?"explorer":"browser")+"-"+stage;
                    var output=Path.Combine(app.Paths.Root,prefix);Directory.CreateDirectory(output);
                    for(int i=0;i<frames.Count;i++)frames[i].Frame.Save(Path.Combine(output,i.ToString("D3")+".png"));
                    records.Add(new{app=Path.GetFileName(fixture.ExePath),stage,frames=frames.Count,layeredSamples=layered,hiddenSamples=hidden,thumbnailSamples=presentations,crop=new{crop.Left,crop.Top,crop.Right,crop.Bottom},times=frames.Select(f=>f.Ms).ToArray()});
                    checks["realStartupAndRestoreFrames"]=records;
                    if(!probeOnly)Check(layered==0&&hidden==0&&presentations==0,"A normal application was still hidden behind a replacement thumbnail.");
                }
                NativeMethods.PostMessage(target,0x10,IntPtr.Zero,IntPtr.Zero);await Wait(()=>!NativeMethods.IsWindow(target),"The owned real fixture did not close.");created.Remove(target);
            }
            checks["nativeCaptionRouterAbsent"]=!app.Tiling.CaptionRoutingInstalled;
            if(!probeOnly)Check(!app.Tiling.CaptionRoutingInstalled,"A global caption hook remains active without tiling.");
        }
        finally
        {
            app.Tiling.HidePresentationsForCapture();
            foreach(var hwnd in created)if(NativeMethods.IsWindow(hwnd))NativeMethods.PostMessage(hwnd,0x10,IntPtr.Zero,IntPtr.Zero);
            app.Tiling.DiagnosticWindows=null;app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var p in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
            NativeMethods.SetForegroundWindow(foreground);
        }
    }
}
