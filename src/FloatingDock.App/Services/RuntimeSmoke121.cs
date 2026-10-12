using System.Diagnostics;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Core.Tiling;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    private static async Task CheckUi121Async(DockApp app,Dictionary<string,object> checks)
    {
        var original=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;var dock=app.Dock.PrimaryWindowForDiagnostics!;
        var probes=new List<Window>();var rects=new Dictionary<long,RECT>();
        app.Dock.HoldFullscreenStateForDiagnostics=true;dock.IgnoreRealCursorForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;
        using var trayHost=new MessageWindow();using var trayFixture=new TrayIcon(trayHost,"FloatingDock tray smoke fixture",Environment.ProcessPath);
        int clicks=0,contexts=0;trayFixture.LeftClick+=()=>clicks++;trayFixture.MenuProvider=()=>{contexts++;return Array.Empty<TrayMenuItem>();};
        try
        {
            Check(app.LaunchpadWin is null,"Startup eagerly allocated the launchpad.");
            var mon=dock.Monitor;
            for(int i=0;i<3;i++)
            {
                var probe=new Window { Title="FloatingDock tiling fixture "+i,Content=new Grid { Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,(byte)(40+i*40),120,170)) } };
                probes.Add(probe);var hwnd=WindowSetup.Hwnd(probe);probe.Activate();WindowStyler.SetBounds(hwnd,mon.Bounds.Left+200+i*90,mon.Bounds.Top+170+i*70,420,300,topmost:false);
                app.Running.IncludeOwnWindow(hwnd);NativeMethods.GetWindowRect(hwnd,out var rect);rects[hwnd.ToInt64()]=rect;
            }
            app.Tiling.DiagnosticWindows=rects.Keys.ToHashSet();
            app.Tiling.SkipHotkeyRegistrationForDiagnostics=true;
            app.Settings.Update(s=>{s.TilingEnabled=true;s.TilingModifier=TilingModifier.Alt;s.TilingExcludedApps="";s.TilingAnimationMs=160;s.TilingWorkspaces=3;});
            await Task.Delay(1100);
            Check(app.Settings.Current.TilingEnabled&&app.Tiling.Session?.ManagedCount==3,"Opt-in tiling did not manage exactly the own fixtures.");
            Check(!app.Tiling.Animating,"Tiling timer did not stop after settling.");
            var session=app.Tiling.Session!;var targets=session.Targets();
            foreach(var (id,target) in targets)
            {NativeMethods.GetWindowRect(new(id),out var rect);Check(Math.Abs(rect.Left-target.X)<=2&&Math.Abs(rect.Top-target.Y)<=2,"Tiling final position mismatch.");}
            var first=rects.Keys.First();
            // Background diagnostics cannot require Windows foreground-lock permission.
            // Select only the owned fixture through the same model input as a foreground event.
            session.Reconcile(app.Running.Snapshot(),first);var beforeSwap=session.Targets()[first];
            session.Navigate(TileDirection.Right,true);app.Tiling.AnimateLayout();await Task.Delay(280);Check(!app.Tiling.Animating&&session.Targets()[first]!=beforeSwap,"Swap failed or never settled.");
            session.SwitchWorkspace(2,mon.DeviceName);await Task.Delay(250);Check(rects.Keys.All(id=>NativeMethods.IsIconic(new(id))),"Inactive workspace left visible fixture windows.");
            session.SwitchWorkspace(1,mon.DeviceName);app.Tiling.AnimateLayout();await Task.Delay(400);
            Check(rects.Keys.All(id=>!NativeMethods.IsIconic(new(id))),"Workspace switch failed to restore its windows.");
            Check(app.Tiling.HandleHotkey(120)&&app.Tiling.Paused&&app.Tiling.Session is null,"Pause shortcut did not release the native session.");
            Check(app.Tiling.HandleHotkey(120)&&!app.Tiling.Paused,"Resume shortcut did not reactivate tiling.");await Task.Delay(500);
            Check(app.Tiling.Session?.ManagedCount==3&&!app.Tiling.Animating,"Resume failed or left the frame timer running.");
            session=app.Tiling.Session!;
            NativeMethods.ShowWindow(new(first),6);await Task.Delay(300);
            Check(NativeMethods.IsIconic(new(first))&&!session.Targets().ContainsKey(first),"User-minimized window still occupies a tile.");
            NativeMethods.ShowWindow(new(first),9);await Task.Delay(350);Check(session.Targets().ContainsKey(first),"User restore did not rejoin its layout.");
            NativeMethods.SetForegroundWindow(new(first));NativeMethods.ShowWindow(new(first),3);await Task.Delay(300);
            Check(NativeMethods.IsZoomed(new(first))&&!session.Targets().ContainsKey(first),"Maximize was overridden by tiling.");
            checks["beforeFloatingToggle"]=session.DescribeForDiagnostics(first);
            session.Reconcile(app.Running.Snapshot(),first);
            session.ToggleFloating();app.Tiling.AnimateLayout();await Task.Delay(500);
            Check(!NativeMethods.IsZoomed(new(first))&&session.Targets().ContainsKey(first),"Floating toggle did not return a maximized fixture to tiling: "+session.DescribeForDiagnostics(first));
            checks["minimizeMaximizeAndPause"]=true;
            checks["builtInTiling"]=new {windows=3,managedOwnWindowsOnly=true,workspaceRoundTrip=true,finiteAnimations=true};
            app.Settings.Update(s=>s.TilingEnabled=false);await Task.Delay(300);
            foreach(var (id,before) in rects)
            {NativeMethods.GetWindowRect(new(id),out var after);Check(Math.Abs(before.Left-after.Left)<=2&&Math.Abs(before.Top-after.Top)<=2&&Math.Abs(before.Width-after.Width)<=2&&Math.Abs(before.Height-after.Height)<=2,"Disabling tiling did not restore original window placement.");}
            checks["disableRestoresPlacement"]=true;
            app.ShowSettings("Tiling manager");await Task.Delay(200);OwnWindowCapture.Capture(app.SettingsWin!.Handle).Save(Path.Combine(app.Paths.Root,"tiling-settings.png"));app.SettingsWin.Close();
            probes[0].Activate();await Task.Delay(150);
            await bar.OpenTrayForDiagnostics();await Task.Delay(300);
            var items=bar.Menus.TrayItems;Check(bar.TrayVisible&&items.Count>0,"Own tray menu has no live icons.");
            File.WriteAllLines(Path.Combine(app.Paths.Root,"tray-items.txt"),items.Select(i=>$"{i.Key} | PNG={i.Icon?.Length??0}"));
            Check(!SystemTrayOverflow.Visible,"Own tray left the Windows overflow on screen.");
            OwnWindowCapture.Capture(bar.Menus.PopupHandle).Save(Path.Combine(app.Paths.Root,"own-tray-menu.png"));
            checks["trayImages"]=new {liveIcons=items.Count,withPng=items.Count(i=>i.Icon is not null)};
            var fixture=items.SingleOrDefault(i=>i.Name.Contains("FloatingDock tray smoke fixture"));Check(fixture is not null,"Real fixture notification icon was not enumerated.");
            bar.Menus.Close(immediately:true);
            Check(await app.ActivateTrayItemAsync(fixture!,false),"Explorer invoke failed for own fixture.");await Task.Delay(200);Check(clicks==1,"Tray click failed to reach the actual notification callback.");
            Check(await app.ActivateTrayItemAsync(fixture!,true),"Explorer context action failed for own fixture.");await Task.Delay(250);Check(contexts==1,"Tray right-click failed to reach the actual notification callback.");
            checks["realTrayCallbacks"]=new {clicks,contexts};
            var files=Path.Combine(app.Paths.Root,"search-fixtures");Directory.CreateDirectory(files);
            for(int i=0;i<1200;i++)File.WriteAllText(Path.Combine(files,$"palette-fixture-{i:D4}.txt"),"");
            app.Settings.Update(s=>{s.SearchFolders=files;s.SearchFiles=true;s.SearchResultLimit=8;});
            app.ToggleSearch();var timer=Stopwatch.StartNew();await app.Palette!.SearchForDiagnostics("palette-fixture");long cold=timer.ElapsedMilliseconds;
            timer.Restart();for(int i=0;i<20;i++)await app.Palette.SearchForDiagnostics("palette-fixture-"+i.ToString("D2"));long warm=timer.ElapsedMilliseconds;
            await app.Palette.SearchForDiagnostics("palette-fixture");await Task.Delay(220);
            var paletteFrame=OwnWindowCapture.Capture(app.Palette.Handle);int blue=0;
            for(int p=0;p<paletteFrame.Pixels.Length;p+=4)if(Math.Abs(paletteFrame.Pixels[p]-215)<12&&Math.Abs(paletteFrame.Pixels[p+1]-96)<12&&Math.Abs(paletteFrame.Pixels[p+2]-70)<12)blue++;
            Check(blue>2000,"Search selected row did not render.");paletteFrame.Save(Path.Combine(app.Paths.Root,"search-palette.png"));app.Palette.Hide();
            checks["searchSelectionRendered"]=blue;
            checks["searchTimings"]=new {files=1200,coldMs=cold,warm20QueriesMs=warm};
            bar.Menus.MediaSource=new FakeMedia();bar.OpenMenuForDiagnostics("media");await Task.Delay(350);
            OwnWindowCapture.Capture(bar.Menus.PopupHandle).Save(Path.Combine(app.Paths.Root,"media-no-source-id.png"));bar.Menus.Close(immediately:true);
            await Task.Delay(700);Check(!dock.IsAnimating&&!app.Tiling.Animating,"Idle frame loops did not stop.");
            var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;timer.Restart();await Task.Delay(3000);process.Refresh();
            checks["idle"]=new {cpuPercentOfOneCore=(process.TotalProcessorTime-cpu).TotalMilliseconds/timer.Elapsed.TotalMilliseconds*100,privateMB=process.PrivateMemorySize64/1048576d,workingMB=process.WorkingSet64/1048576d};
        }
        finally
        {
            app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);app.Tiling.DiagnosticWindows=null;
            app.Palette?.Hide();bar.Menus.Close(immediately:true);app.SettingsWin?.Close();
            foreach(var probe in probes){app.Running.ExcludeOwnWindow(WindowSetup.Hwnd(probe));probe.Close();}
            app.Settings.Update(s=>{foreach(var p in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(original));});
            app.Dock.HoldFullscreenStateForDiagnostics=false;dock.IgnoreRealCursorForDiagnostics=false;bar.IgnoreRealCursorForDiagnostics=false;
        }
    }
}
