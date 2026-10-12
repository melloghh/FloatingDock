using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    // A minimum-size hook on owned fixtures makes the real three-window regression reproducible.
    private delegate IntPtr MinimumProc(IntPtr h,uint m,IntPtr w,IntPtr l,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr h,MinimumProc proc,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr h,MinimumProc proc,UIntPtr id);
    [StructLayout(LayoutKind.Sequential)] private struct FixtureMinimum {public POINT Reserved,Maximum,Position,Minimum,MaximumTrack;}
    private static async Task CheckUi130Async(DockApp app,Dictionary<string,object> checks)
    {
        var settings=app.Settings.Current.Clone();var dock=app.Dock.PrimaryWindowForDiagnostics!;var bar=app.TopBar!.Primary!;var mon=dock.Monitor;
        var windows=new List<Window>();var ids=new HashSet<long>();var original=new Dictionary<long,RECT>();var colors=new Dictionary<long,uint>();
        app.Dock.HoldFullscreenStateForDiagnostics=true;dock.IgnoreRealCursorForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;
        MinimumProc minimum=(h,m,w,l,id,data)=>
        {
            var result=DefSubclassProc(h,m,w,l);
            if(m==0x24){var value=Marshal.PtrToStructure<FixtureMinimum>(l);value.Minimum=new(){X=600,Y=250};Marshal.StructureToPtr(value,l,false);}
            return result;
        };
        async Task Wait(Func<bool> ready,string error,int limit=4000)
        {var timer=Stopwatch.StartNew();while(!ready()&&timer.ElapsedMilliseconds<limit)await Task.Delay(30);Check(ready(),error);}
        Window Create()
        {
            var window=new Window {Title="FloatingDock workspace fixture "+windows.Count,Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,30,(byte)(70+windows.Count*25),110))}};
            windows.Add(window);window.Activate();var hwnd=WindowSetup.Hwnd(window);
            SetWindowSubclass(hwnd,minimum,new(130),UIntPtr.Zero);
            WindowStyler.SetBounds(hwnd,mon.WorkArea.Left+180,mon.WorkArea.Top+160,720,520,topmost:true);
            NativeMethods.GetWindowRect(hwnd,out var rect);original[hwnd.ToInt64()]=rect;
            if(WindowBorderAccent.TryRead(hwnd,out var color))colors[hwnd.ToInt64()]=color;
            ids.Add(hwnd.ToInt64());app.Tiling.DiagnosticWindows=ids;if(app.Tiling.Session is { } session)session.DiagnosticWindows=ids;
            app.Running.IncludeOwnWindow(hwnd);return window;
        }
        try
        {
            Create();app.Tiling.SkipHotkeyRegistrationForDiagnostics=true;
            app.Settings.Update(s=>{s.TilingEnabled=true;s.TilingWorkspaces=3;s.TilingBorders=true;s.TilingActiveBorderColor="FC396E";s.TilingHideDock=true;s.TilingBringActivatedWindows=true;s.TilingExcludedApps="";s.TilingStartupDelayMs=180;s.TilingAnimationMs=220;});
            await Wait(()=>app.Tiling.Session?.ManagedCount==1&&!app.Tiling.Animating,"First tile did not settle.");
            Create();await Wait(()=>app.Tiling.Session?.Targets().Count==2&&!app.Tiling.Animating,"Second tile did not settle.");
            var third=Create();var thirdHwnd=WindowSetup.Hwnd(third);
            // A real app can overwrite its startup placement several times before first paint.
            for(int i=0;i<3;i++){await Task.Delay(45);WindowStyler.SetBounds(thirdHwnd,mon.WorkArea.Left+200+i*60,mon.WorkArea.Top+180,720+i*20,520,topmost:false);}
            NativeMethods.GetWindowRect(thirdHwnd,out var startup);original[thirdHwnd.ToInt64()]=startup;
            await Wait(()=>app.Tiling.Session?.Targets().Count==3&&!app.Tiling.Animating,"Third tile disappeared or floated due to an unnecessary quarter-width split.");
            var session=app.Tiling.Session!;
            Check(ids.All(id=>!NativeMethods.IsIconic(new(id))),"A newly opened tile was minimized.");
            Check(session.ActiveWorkspace(mon.DeviceName)==1,"New tile switched to a different workspace.");
            Check(session.Targets().Values.All(r=>r.Width>=600),"Three tiles violate their minimum width.");
            checks["threeSequentialWindows"]=new{visible=3,minimumWidth=600,workspace=1,stableStartup=true};
            await Wait(()=>dock.HideAmount>.99f,"Tiling did not conceal the dock.");Check(dock.TilingHiddenForDiagnostics,"Dock tiling visibility state missing.");
            Check(Math.Abs(bar.WorkspaceCenterOffsetForDiagnostics)<=1,"Workspace buttons are not centered on the full panel.");
            checks["workspaceCenterOffsetDip"]=bar.WorkspaceCenterOffsetForDiagnostics;
            OwnWindowCapture.Capture(bar.Handle).Save(Path.Combine(app.Paths.Root,"centered-workspaces.png"));
            session.Reconcile(app.Running.Snapshot(),thirdHwnd.ToInt64());session.UpdateBorders();
            int AccentPixels(OwnWindowCapture.Frame frame)
            {int count=0;for(int p=0;p<frame.Pixels.Length;p+=4)if(Math.Abs(frame.Pixels[p]-110)<5&&Math.Abs(frame.Pixels[p+1]-57)<5&&Math.Abs(frame.Pixels[p+2]-252)<5)count++;return count;}
            if(WindowBorderAccent.Supported)
            {await Task.Delay(120);session.Reconcile(app.Running.Snapshot(),thirdHwnd.ToInt64());session.UpdateBorders();var frame=OwnWindowCapture.Capture(thirdHwnd);int pixels=AccentPixels(frame);checks["borderState"]=session.DescribeForDiagnostics(thirdHwnd.ToInt64());checks["borderRenderedPixels"]=pixels;frame.Save(Path.Combine(app.Paths.Root,"native-border.png"));Check(pixels>200,"Native border did not render on the owned window.");}
            if(colors.ContainsKey(thirdHwnd.ToInt64()))
            {Check(WindowBorderAccent.TryRead(thirdHwnd,out var active)&&active==WindowBorderAccent.Color(app.Settings.Current.TilingActiveBorderColor),"Focused border color was not applied.");checks["nativeBorderApplied"]=true;}
            app.Tiling.SwitchWorkspace(2,mon.DeviceName);await Wait(()=>ids.All(id=>NativeMethods.IsIconic(new(id))),"Old workspace did not conceal its windows.");
            Create();await Wait(()=>session.Targets().Count==1&&!app.Tiling.Animating,"New app did not stay in the selected empty workspace.");
            var first=ids.First();var info=app.Running.Snapshot().Single(w=>w.Hwnd==first);
            app.Tiling.PrepareActivation(new[]{info});await Wait(()=>session.Targets().Count==2&&!NativeMethods.IsIconic(new(first))&&!app.Tiling.Animating,"Dock activation did not bring an existing app into the current workspace.");
            Check(session.ActiveWorkspace(mon.DeviceName)==2,"Dock activation changed the selected workspace.");
            checks["openAndBringIntoCurrentWorkspace"]=true;
            var maximized=Create();var maximizedHwnd=WindowSetup.Hwnd(maximized);NativeMethods.ShowWindow(maximizedHwnd,3);
            NativeMethods.GetWindowRect(maximizedHwnd,out var maxOriginal);original[maximizedHwnd.ToInt64()]=maxOriginal;
            await Wait(()=>session.Targets().Count==3&&!NativeMethods.IsZoomed(maximizedHwnd)&&!app.Tiling.Animating,"An app starting maximized did not join the current tiles.");
            Check(session.ActiveWorkspace(mon.DeviceName)==2,"Maximized startup changed workspace.");checks["maximizedStartupJoinsCurrentWorkspace"]=true;
            for(int i=0;i<4;i++){app.Tiling.SwitchWorkspace(i%2+1,mon.DeviceName);await Task.Delay(70);}
            await Task.Delay(400);Check(session.ActiveWorkspace(mon.DeviceName)==2,"Rapid workspace switching was undone by a delayed event.");
            app.Settings.Update(s=>s.TilingKeybindings["focus.left"]="Ctrl+Alt+F20");app.Tiling.Apply(app.Settings.Current);
            Check(app.Settings.Current.TilingEnabled&&ReferenceEquals(session,app.Tiling.Session),"Editing a bind rebuilt the tiling session.");
            app.Settings.Update(s=>s.TilingKeybindings["focus.left"]=s.SearchHotkey);app.Tiling.Apply(app.Settings.Current);
            Check(app.Settings.Current.TilingEnabled&&ReferenceEquals(session,app.Tiling.Session)&&app.Settings.Current.TilingKeybindings["focus.left"]=="Ctrl+Alt+F20","Invalid bind did not preserve the previous shortcuts/session.");
            checks["shortcutEditAndRollback"]=true;
            app.ShowSettings("Tiling manager");await Task.Delay(250);NativeMethods.GetWindowRect(app.SettingsWin!.Handle,out var settingsRect);WindowStyler.SetBounds(app.SettingsWin.Handle,settingsRect.Left,settingsRect.Top,settingsRect.Width,settingsRect.Height,topmost:true);OwnWindowCapture.Capture(app.SettingsWin.Handle).Save(Path.Combine(app.Paths.Root,"tiling-settings.png"));app.SettingsWin.Close();
            app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);await Task.Delay(500);
            if(WindowBorderAccent.Supported)Check(AccentPixels(OwnWindowCapture.Capture(thirdHwnd))<30,"Disable left the custom border rendered.");
            Check(!dock.TilingHiddenForDiagnostics,"Disable left the dock concealed.");
            foreach(var (id,before) in original)
            {
                NativeMethods.GetWindowRect(new(id),out var after);Check(Math.Abs(after.Left-before.Left)<=2&&Math.Abs(after.Top-before.Top)<=2&&Math.Abs(after.Width-before.Width)<=2&&Math.Abs(after.Height-before.Height)<=2,"Disable lost original placement.");
                if(colors.TryGetValue(id,out var color))Check(WindowBorderAccent.TryRead(new(id),out var restored)&&restored==color,"Disable lost original border color.");
            }
            checks["restoreColorsPositionsAndDock"]=true;
        }
        finally
        {
            app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);app.Tiling.DiagnosticWindows=null;
            app.SettingsWin?.Close();foreach(var window in windows){var hwnd=WindowSetup.Hwnd(window);RemoveWindowSubclass(hwnd,minimum,new(130));app.Running.ExcludeOwnWindow(hwnd);window.Close();}
            app.Settings.Update(s=>{foreach(var p in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(settings));});
            app.Dock.HoldFullscreenStateForDiagnostics=false;dock.IgnoreRealCursorForDiagnostics=false;bar.IgnoreRealCursorForDiagnostics=false;
        }
    }
}
