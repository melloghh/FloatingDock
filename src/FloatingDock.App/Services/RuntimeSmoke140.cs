using System.Diagnostics;
using System.Runtime.InteropServices;
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
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr h,out uint key,out byte alpha,out uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindowPlacement")] private static extern bool GetFixturePlacement(IntPtr h,ref NativeTilingSession.Placement p);
    [DllImport("user32.dll",EntryPoint="SetWindowPlacement")] private static extern bool SetFixturePlacement(IntPtr h,ref NativeTilingSession.Placement p);
    private static async Task CheckUi140Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var mon=app.Dock.PrimaryWindowForDiagnostics!.Monitor;
        var windows=new List<Window>();var ids=new HashSet<long>();var minima=new Dictionary<long,(int Width,int Height)>();var styles=new Dictionary<long,long>();var originals=new Dictionary<long,RECT>();
        bool realPlayer=Environment.GetCommandLineArgs().Contains("--test-spotify140"),foreignSaved=false;IntPtr foreign=IntPtr.Zero;var foreignPlacement=new NativeTilingSession.Placement {Length=Marshal.SizeOf<NativeTilingSession.Placement>()};
        app.Dock.HoldFullscreenStateForDiagnostics=true;
        MinimumProc minimum=(h,m,w,l,id,data)=>
        {
            var result=DefSubclassProc(h,m,w,l);
            if(m==0x24&&minima.TryGetValue(h.ToInt64(),out var min)){var value=Marshal.PtrToStructure<FixtureMinimum>(l);value.Minimum=new(){X=min.Width,Y=min.Height};Marshal.StructureToPtr(value,l,false);}return result;
        };
        async Task Wait(Func<bool> ready,string error,int limit=4000)
        {var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<limit)await Task.Delay(15);Check(ready(),error);}
        Window Create(int width,int height)
        {
            var window=new Window {Title="FloatingDock mixed fixture "+windows.Count,Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,38,108,156))}};
            windows.Add(window);window.Activate();var hwnd=WindowSetup.Hwnd(window);long id=hwnd.ToInt64();
            minima[id]=(width,height);ids.Add(id);styles[id]=NativeMethods.GetExStyle(hwnd);
            SetWindowSubclass(hwnd,minimum,new(140),UIntPtr.Zero);
            WindowStyler.SetBounds(hwnd,mon.WorkArea.Left+200,mon.WorkArea.Top+140,850,700,topmost:true);
            NativeMethods.GetWindowRect(hwnd,out var rect);originals[id]=rect;
            app.Tiling.DiagnosticWindows=ids;if(app.Tiling.Session is { } session)session.DiagnosticWindows=ids;
            app.Running.IncludeOwnWindow(hwnd);return window;
        }
        bool Matches(long id,TileRect target)
        {return NativeMethods.GetWindowRect(new(id),out var r)&&Math.Abs(r.Left-target.X)<=2&&Math.Abs(r.Top-target.Y)<=2&&Math.Abs(r.Width-target.Width)<=2&&Math.Abs(r.Height-target.Height)<=2;}
        void RestoredOpacity()
        {foreach(long id in ids){Check((NativeMethods.GetExStyle(new(id))&0x80000)==(styles[id]&0x80000),"A compositor transition leaked the layered style.");if(GetLayeredWindowAttributes(new(id),out _,out var alpha,out _))Check(alpha!=0,"An opacity lease left an app invisible.");}}
        try
        {
            Create(480,600);app.Tiling.SkipHotkeyRegistrationForDiagnostics=true;
            app.Settings.Update(s=>{s.TilingEnabled=true;s.TilingExcludedApps="";s.TilingWorkspaces=3;s.TilingAnimationMs=360;s.TilingAnimationFps=120;s.TilingStartupDelayMs=180;if(Environment.GetCommandLineArgs().Contains("--test-side-dock"))s.Edge=FloatingDock.Core.Configuration.DockEdge.Left;});
            await Wait(()=>app.Tiling.Session?.Targets().Count==1&&!app.Tiling.Animating,"First fixture did not settle.");
            Create(400,250);await Wait(()=>app.Tiling.Session?.Targets().Count==2&&!app.Tiling.Animating,"Second fixture did not settle.");
            bool unknownBrowser=Environment.GetCommandLineArgs().Contains("--test-only-ui142");
            var browser=Create(unknownBrowser?0:480,unknownBrowser?0:400);long browserId=WindowSetup.Hwnd(browser).ToInt64();
            await Wait(()=>app.Tiling.Session?.Targets().Count==3&&!app.Tiling.Animating,"Third fixture did not settle.");
            var bar=app.TopBar!.Primary!;NativeMethods.GetWindowRect(bar.Handle,out var barRect);
            int backgroundX=barRect.Width/2+250,backgroundY=barRect.Height/2;
            long playerId;
            if(realPlayer)
            {
                await Wait(()=>app.Running.Snapshot().Any(w=>string.Equals(w.ExeFileName,"Spotify.exe",StringComparison.OrdinalIgnoreCase)),"No existing Spotify window available.");
                foreign=new(app.Running.Snapshot().First(w=>string.Equals(w.ExeFileName,"Spotify.exe",StringComparison.OrdinalIgnoreCase)).Hwnd);
                Check(GetFixturePlacement(foreign,ref foreignPlacement),"Could not preserve Spotify placement.");
                foreignSaved=true;
                NativeMethods.ShowWindowAsync(foreign,4);await Wait(()=>!NativeMethods.IsIconic(foreign),"Spotify did not restore.");
                playerId=foreign.ToInt64();ids.Add(playerId);minima[playerId]=(800,600);styles[playerId]=NativeMethods.GetExStyle(foreign);NativeMethods.GetWindowRect(foreign,out var r);originals[playerId]=r;
                app.Tiling.DiagnosticWindows=ids;app.Tiling.Session!.DiagnosticWindows=ids;app.Tiling.ReconcileForDiagnostics();
            }
            else playerId=WindowSetup.Hwnd(Create(800,600)).ToInt64();
            await Wait(()=>GetLayeredWindowAttributes(new(playerId),out _,out var a,out _)&&a==0,"The startup wait still exposed the saved app rectangle.");
            await Wait(()=>app.Tiling.PresentationsForDiagnostics.ContainsKey(playerId),"The new app did not get a compositor presentation.");
            var opening=new List<TileRect>();int nativeChanges=0;TileRect? previous=null;
            var clock=Stopwatch.StartNew();
            while(app.Tiling.Animating&&clock.ElapsedMilliseconds<1800)
            {
                if(app.Tiling.PresentationsForDiagnostics.TryGetValue(playerId,out var p))opening.Add(p.Bounds);
                if(app.Tiling.Session!.Targets().TryGetValue(playerId,out var target)&&Matches(playerId,target))
                {if(previous is { } old&&old!=target)nativeChanges++;previous=target;}
                await Task.Delay(12);
            }
            var session=app.Tiling.Session!;var targets=session.Targets();
            Check(targets.Count==4,"An 800x600 player covered the other tiles instead of reflowing four windows.");
            if(unknownBrowser){Check(targets[browserId].Width>=480*mon.Scale&&targets[browserId].Height>=320*mon.Scale,"A browser with unknown native minimum became a sliver.");checks["unknownBrowserUsefulSize"]=targets[browserId];}
            Check(opening.Distinct().Count()>5,"Opening did not produce smooth compositor frames.");
            Check(nativeChanges==0&&ids.All(id=>Matches(id,targets[id])),"An app visibly moved through intermediate native bounds.");
            foreach(var (id,r) in targets){Check(r.Width>=minima[id].Width&&r.Height>=minima[id].Height,"Mixed layout violated a minimum size.");foreach(var other in targets.Where(p=>p.Key!=id))Check(r.X+r.Width<=other.Value.X+16||other.Value.X+other.Value.Width<=r.X+16||r.Y+r.Height<=other.Value.Y+16||other.Value.Y+other.Value.Height<=r.Y+16,"Mixed windows overlap.");}
            RestoredOpacity();checks["fourMixedMinimumWindows"]=new{count=4,openingFrames=opening.Distinct().Count(),nativeIntermediateChanges=nativeChanges,playerMinimum="800x600"};
            if(Environment.GetCommandLineArgs().Contains("--test-only-ui141")||unknownBrowser)
            {
                NativeMethods.ShowWindowAsync(new(playerId),NativeMethods.SW_MINIMIZE);
                await Wait(()=>NativeMethods.IsIconic(new(playerId))&&app.Tiling.PresentationsForDiagnostics.ContainsKey(playerId),"A user minimize did not animate to the dock.");
                await Wait(()=>!app.Tiling.Animating&&session.Targets().Count==3,"A user-minimized window continued occupying a tile.");
                RestoredOpacity();
                NativeMethods.ShowWindowAsync(new(playerId),NativeMethods.SW_RESTORE);
                await Wait(()=>!app.Tiling.Animating&&session.Targets().Count==4&&ids.All(id=>Matches(id,session.Targets()[id])),"User restore did not reflow the remaining windows.");
                RestoredOpacity();checks["userMinimizeAndRestore"]=true;
            }
            // A real app can apply its saved geometry well after the startup gate.
            session.Move(playerId,session.Targets()[playerId]);
            NativeMethods.SetWindowPos(new(playerId),IntPtr.Zero,mon.WorkArea.Left+100,mon.WorkArea.Top+100,1000,750,NativeMethods.SWP_NOZORDER|NativeMethods.SWP_NOACTIVATE);
            await Wait(()=>!app.Tiling.Animating&&Matches(playerId,session.Targets()[playerId]),"A late self-resize covered adjacent windows.");checks["lateSelfResizeRepaired"]=true;
            var origin=app.Dock.AnimationRectFor(new(playerId));Check(origin is not null,"Workspace hiding has no dock icon destination.");
            app.Tiling.SwitchWorkspace(2,mon.DeviceName);
            await Wait(()=>ids.All(id=>NativeMethods.IsIconic(new(id))),"Workspace state waited on the visual animation.");
            await Task.Delay(80);
            Check(app.Tiling.PresentationsForDiagnostics.TryGetValue(playerId,out var hiding),"No workspace hide animation was presented.");
            // Inspect only the center of our own fixture's representation, including
            // after native minimization. A black/empty DWM thumbnail is not a pass.
            var frame=OwnWindowCapture.Capture(hiding.Handle);NativeMethods.GetWindowRect(hiding.Handle,out var viewport);
            int gapPixel=((barRect.Top+backgroundY-viewport.Top)*frame.Width+barRect.Left+backgroundX-viewport.Left)*4;
            int x=hiding.Bounds.X+hiding.Bounds.Width/2-viewport.Left,y=hiding.Bounds.Y+hiding.Bounds.Height/2-viewport.Top;
            int pixel=(y*frame.Width+x)*4;bool rendered=pixel>=0&&pixel+3<frame.Pixels.Length&&(realPlayer?frame.Pixels[pixel]+frame.Pixels[pixel+1]+frame.Pixels[pixel+2]>20:frame.Pixels[pixel]>90&&frame.Pixels[pixel+1]>45);
            checks["minimizedThumbnailRendered"]=new{rendered,bgra=pixel>=0&&pixel+3<frame.Pixels.Length?frame.Pixels.Skip(pixel).Take(4).ToArray():Array.Empty<byte>()};Check(rendered,"Minimized source disappeared from the compositor animation.");
            var conceal=new List<TileRect>();while(app.Tiling.Animating){if(app.Tiling.PresentationsForDiagnostics.TryGetValue(playerId,out var p))conceal.Add(p.Bounds);await Task.Delay(12);}
            // Compare the same minimized native state with/without the surface;
            // tiled test titlebars can cover this point before the workspace switch.
            var background=OwnWindowCapture.Capture(bar.Handle);int backgroundPixel=(backgroundY*background.Width+backgroundX)*4;
            checks["viewportColors"]=new{without=background.Pixels.Skip(backgroundPixel).Take(3).ToArray(),during=frame.Pixels.Skip(gapPixel).Take(3).ToArray()};
            Check(Enumerable.Range(0,3).All(c=>Math.Abs(frame.Pixels[gapPixel+c]-background.Pixels[backgroundPixel+c])<12),"The animation surface darkened the desktop outside its thumbnail.");checks["transparentAnimationViewport"]=true;
            Check(conceal.Distinct().Count()>5&&conceal[^1].Width<100,"Workspace animation did not shrink towards the dock.");
            checks["workspaceHideToDock"]=new{frames=conceal.Distinct().Count(),dock=origin,last=conceal[^1],edge=app.Settings.Current.Edge.ToString()};RestoredOpacity();
            app.Tiling.SwitchWorkspace(1,mon.DeviceName);await Wait(()=>!app.Tiling.Animating&&ids.All(id=>!NativeMethods.IsIconic(new(id))&&Matches(id,session.Targets()[id])),"Workspace restoration did not return exact tiles.");RestoredOpacity();
            for(int i=0;i<6;i++){app.Tiling.SwitchWorkspace(i%2+1,mon.DeviceName);await Task.Delay(40);}
            app.Tiling.SwitchWorkspace(1,mon.DeviceName);await Wait(()=>!app.Tiling.Animating&&ids.All(id=>!NativeMethods.IsIconic(new(id))),"Rapid workspace switching leaked hidden apps.");RestoredOpacity();checks["rapidWorkspaceCancellation"]=true;
            session.Reconcile(app.Running.Snapshot(),playerId);session.ToggleDirection();app.Tiling.AnimateLayout();await Task.Delay(35);
            app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);await Task.Delay(120);RestoredOpacity();
            Check(app.Tiling.PresentationCountForDiagnostics==0&&!app.Tiling.Animating,"Disable leaked surfaces/timers.");
            foreach(var (id,r) in originals)Check(Matches(id,new(r.Left,r.Top,r.Width,r.Height)),"Disable during animation lost original app placement.");checks["disableRestoresOpacityAndPlacement"]=true;
            checks["realSpotify"]=realPlayer;
        }
        finally
        {
            app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);app.Tiling.DiagnosticWindows=null;
            foreach(var window in windows){var hwnd=WindowSetup.Hwnd(window);RemoveWindowSubclass(hwnd,minimum,new(140));app.Running.ExcludeOwnWindow(hwnd);window.Close();}
            if(foreignSaved&&NativeMethods.IsWindow(foreign)){foreignPlacement.Flags|=4;SetFixturePlacement(foreign,ref foreignPlacement);}
            app.Settings.Update(s=>{foreach(var p in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});app.Dock.HoldFullscreenStateForDiagnostics=false;
        }
    }
}
