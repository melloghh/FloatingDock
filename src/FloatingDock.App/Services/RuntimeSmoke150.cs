using System.Diagnostics;
using FloatingDock.Core.Configuration;
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
    private static async Task CheckWorkspaces150Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var mon=app.Dock.PrimaryWindowForDiagnostics!.Monitor;
        var windows=new List<Window>();var ids=new HashSet<long>();var results=new List<object>();
        app.Dock.HoldFullscreenStateForDiagnostics=true;app.Tiling.SkipHotkeyRegistrationForDiagnostics=true;
        async Task Wait(Func<bool> ready,string error)
        {var time=Stopwatch.StartNew();while(!ready()&&time.ElapsedMilliseconds<4500)await Task.Delay(15);Check(ready(),error);}
        async Task Settled()=>await Wait(()=>!app.Tiling.Animating,"Workspace transition did not settle.");
        long Create(string title)
        {
            var window=new Window{Title=title,Content=new Grid{Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,38,108,156))}};
            windows.Add(window);var hwnd=WindowSetup.Hwnd(window);long id=hwnd.ToInt64();ids.Add(id);
            app.Tiling.DiagnosticWindows=ids;if(app.Tiling.Session is { } s)s.DiagnosticWindows=ids;
            WindowStyler.SetBounds(hwnd,mon.WorkArea.Left+100,mon.WorkArea.Top+100,820,650);window.Activate();app.Running.IncludeOwnWindow(hwnd);return id;
        }
        WindowInfo Info(long id)=>new(id,Environment.ProcessId,Environment.ProcessPath,null,"Owned workspace activation fixture",null,NativeMethods.IsIconic(new(id)));
        async Task Switch(int workspace)
        {app.Tiling.SwitchWorkspace(workspace,mon.DeviceName);await Settled();}
        async Task Minimize(long id)
        {Check(app.Tiling.MinimizeToDock(new(id)),"Owned user minimize was refused: "+app.Tiling.Session!.DescribeForDiagnostics(id));await Wait(()=>NativeMethods.IsIconic(new(id)),"Owned minimize did not reach native state.");await Settled();}
        try
        {
            foreach(bool bring in new[]{true,false})
            {
                checks["stage"]="initial "+bring;
                long browser=Create("Browser workspace one fixture");
                app.Settings.Update(s=>{s.TilingEnabled=true;s.TilingWorkspaces=3;s.TilingBringActivatedWindows=bring;s.TilingAnimationMs=110;s.TilingStartupDelayMs=80;s.TilingExcludedApps="";});app.Tiling.Apply(app.Settings.Current);
                await Wait(()=>app.Tiling.Session?.Targets().ContainsKey(browser)==true&&!app.Tiling.Animating,"Browser did not join workspace one.");
                var session=app.Tiling.Session!;
                await Switch(2);long explorer=Create("Explorer workspace two fixture");
                await Wait(()=>session.Targets().ContainsKey(explorer)&&!app.Tiling.Animating,"Explorer did not join workspace two.");
                await Switch(1);await Minimize(browser);await Switch(2);await Minimize(explorer);await Switch(1);
                checks["stage"]="activation "+bring;
                Check(NativeMethods.IsIconic(new(browser))&&NativeMethods.IsIconic(new(explorer)),"Switching areas restored a user-minimized window.");
                var selected=new List<long>();
                var activation=await WindowActivator.ActivateOrMinimizeAsync(new[]{Info(explorer)},true,app.Tiling.MinimizeToDock,w=>
                {
                    selected.Add(w.Hwnd);app.Tiling.PrepareActivation(new[]{w});
                    // Simulate the workspace preparation handing focus to the restored target before activation.
                    NativeMethods.ShowWindow(new(w.Hwnd),NativeMethods.SW_RESTORE);NativeMethods.SetForegroundWindow(new(w.Hwnd));
                });
                Check(activation==WindowActivator.ActivationResult.Activated,"Explorer activation toggled it closed after workspace preparation: "+activation);
                await Wait(()=>!app.Tiling.Animating&&!NativeMethods.IsIconic(new(explorer))&&session.Targets().ContainsKey(explorer),"The user's cross-workspace Explorer restore failed.");
                Check(session.ActiveWorkspace(mon.DeviceName)==(bring?1:2),"Dock activation applied the wrong workspace policy.");
                Check(NativeMethods.IsIconic(new(browser))&&session.IsUserMinimized(browser),"Restoring Explorer also restored the user-minimized browser.");
                Check(selected.Count==1,"A single-window activation prepared the target more than once.");
                if(GetLayeredWindowAttributes(new(explorer),out _,out byte alpha,out _))Check(alpha!=0,"Cross-workspace restore left Explorer invisible.");
                results.Add(new{bring,exactUserScenario=true,activation=activation.ToString(),nativeVisible=true,browserStaysMinimized=true});
                // An app group contains several windows. Only the chosen owner may move or restore.
                await Switch(2);long other=Create("Second Explorer fixture");
                checks["stage"]="group "+bring;
                await Wait(()=>session.Targets().ContainsKey(other)&&!app.Tiling.Animating,"Second Explorer did not settle.");
                await WindowActivator.ActivateOrMinimizeAsync(new[]{Info(explorer)},false,null,w=>app.Tiling.PrepareActivation(new[]{w}));
                await Wait(()=>!NativeMethods.IsIconic(new(explorer))&&!app.Tiling.Animating,"First Explorer did not join the group workspace.");
                await Minimize(explorer);await Minimize(other);await Switch(1);selected.Clear();
                activation=await WindowActivator.ActivateOrMinimizeAsync(new[]{Info(explorer),Info(other)},true,app.Tiling.MinimizeToDock,w=>{selected.Add(w.Hwnd);app.Tiling.PrepareActivation(new[]{w});});
                await Settled();Check(activation==WindowActivator.ActivationResult.Activated&&selected.Count==1,"Grouped Explorer activation did not choose exactly one live owner.");
                long chosen=selected.Single(),unchosen=chosen==explorer?other:explorer;
                Check(!NativeMethods.IsIconic(new(chosen))&&NativeMethods.IsIconic(new(unchosen))&&session.IsUserMinimized(unchosen),"Grouped activation restored another Explorer window.");
                for(int i=0;i<8;i++){app.Tiling.SwitchWorkspace(i%2+1,mon.DeviceName);await Task.Delay(25);}
                app.Tiling.SwitchWorkspace(1,mon.DeviceName);await Settled();
                Check(session.ActiveWorkspace(mon.DeviceName)==1&&NativeMethods.IsIconic(new(browser))&&NativeMethods.IsIconic(new(unchosen)),"Rapid switches changed user-minimize intent.");
                session.ActivateWindow(chosen);await Wait(()=>!NativeMethods.IsIconic(new(chosen))&&!app.Tiling.Animating,"Chosen window did not return after rapid switches.");
                long partner=Create("Owned drag and reflow partner");await Wait(()=>session.Targets().ContainsKey(partner)&&!app.Tiling.Animating,"Reflow partner did not settle.");
                session.Reconcile(app.Running.Snapshot(),chosen);
                var layout=session.Targets();var destination=layout[partner];var start=layout[chosen];
                NativeMethods.SetWindowPos(new(chosen),IntPtr.Zero,(int)(destination.CenterX-start.Width/2d),(int)(destination.CenterY-start.Height/2d),start.Width,start.Height,NativeMethods.SWP_NOZORDER|NativeMethods.SWP_NOACTIVATE);
                session.MoveFinished(chosen);app.Tiling.AnimateLayout();
                var motionFrames=new List<TileRect>();var motionTime=Stopwatch.StartNew();
                while(app.Tiling.Animating)
                {var presentations=app.Tiling.PresentationsForDiagnostics;if(presentations.TryGetValue(partner,out var p)||presentations.TryGetValue(chosen,out p))motionFrames.Add(p.Bounds);await Task.Delay(12);}
                Check(motionFrames.Distinct().Count()>=8&&motionTime.ElapsedMilliseconds>=180,"Dropping a window swapped the tiles abruptly.");
                session.ToggleDirection();app.Tiling.AnimateLayout();await Task.Delay(45);session.ToggleDirection();app.Tiling.AnimateLayout();await Settled();
                Check(!app.Tiling.Animating&&session.Targets().ContainsKey(chosen),"Reversing reflow left the window moving or floating.");
                results.Add(new{bring,dropFrames=motionFrames.Distinct().Count(),dropMilliseconds=motionTime.ElapsedMilliseconds,reversedReflow=true});
                app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);
                foreach(var window in windows){app.Running.ExcludeOwnWindow(WindowSetup.Hwnd(window));window.Close();}windows.Clear();ids.Clear();
                results.Add(new{bring,groupedOwnerOnly=true,rapidSwitches=8,clockStopped=!app.Tiling.Animating});
            }
            checks["workspaceActivationMatrix"]=results;
        }
        finally
        {
            app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);app.Tiling.DiagnosticWindows=null;
            foreach(var window in windows){app.Running.ExcludeOwnWindow(WindowSetup.Hwnd(window));window.Close();}
            app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var property in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(s,property.GetValue(saved));});
        }
    }
}
