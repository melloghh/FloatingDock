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
    private static async Task CheckUi141Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var mon=app.Dock.PrimaryWindowForDiagnostics!.Monitor;
        var backdrop=new Window {Title="FloatingDock fullscreen fixture",Content=new Grid()};
        var front=new Window {Title="FloatingDock minimize fixture",Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,38,108,156))}};
        var hwnd=WindowSetup.Hwnd(front);long id=hwnd.ToInt64();
        async Task Wait(Func<bool> ready,string error,int timeout=4000)
        {var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<timeout)await Task.Delay(12);Check(ready(),error);}
        try
        {
            app.Settings.Update(s=>{s.TilingEnabled=false;s.IconAnimation=true;s.IconAnimationMs=320;s.TopBarEnabled=true;if(Environment.GetCommandLineArgs().Contains("--test-side-dock"))s.Edge=FloatingDock.Core.Configuration.DockEdge.Left;});app.Tiling.Apply(app.Settings.Current);
            backdrop.Activate();WindowStyler.SetBounds(WindowSetup.Hwnd(backdrop),mon.Bounds.Left,mon.Bounds.Top,mon.Bounds.Width,mon.Bounds.Height);
            front.Activate();WindowStyler.SetBounds(hwnd,mon.WorkArea.Left+150,mon.WorkArea.Top+120,850,650);
            app.Running.IncludeOwnWindow(hwnd);await Wait(()=>app.Dock.AnimationRectFor(hwnd) is not null,"Untiled fixture has no dock icon.");await Task.Delay(220);
            var dock=app.Dock.PrimaryWindowForDiagnostics!;dock.SetFullscreenHidden(true,blockEdgeReveal:true);
            var origin=app.Dock.AnimationRectFor(hwnd);Check(origin is not null,"Hidden dock lost its animation destination.");
            NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_MINIMIZE);
            await Wait(()=>app.Tiling.PresentationsForDiagnostics.ContainsKey(id),"Untiled user minimize did not get a dock animation.");
            await Task.Delay(70);
            var shown=app.Tiling.PresentationsForDiagnostics[id];var image=OwnWindowCapture.Capture(shown.Handle);NativeMethods.GetWindowRect(shown.Handle,out var viewport);
            int centerX=(int)shown.Bounds.CenterX-viewport.Left,centerY=(int)shown.Bounds.CenterY-viewport.Top,pixel=(centerY*image.Width+centerX)*4;
            Check(pixel>=0&&pixel+3<image.Pixels.Length&&image.Pixels[pixel]>90&&image.Pixels[pixel+1]>45,"Untiled minimized thumbnail is empty.");checks["untiledMinimizedThumbnailRendered"]=true;
            var bounds=new List<TileRect>();
            while(app.Tiling.Animating){if(app.Tiling.PresentationsForDiagnostics.TryGetValue(id,out var p))bounds.Add(p.Bounds);await Task.Delay(12);}
            Check(bounds.Distinct().Count()>5&&bounds[^1].Width<100,"Untiled minimize did not shrink to the hidden dock icon.");
            Check(origin is { } r&&Math.Abs(bounds[^1].CenterX-(r.Left+r.Width/2d))<80&&Math.Abs(bounds[^1].CenterY-(r.Top+r.Height/2d))<80,"Untiled minimize went towards the Windows taskbar.");
            Check((NativeMethods.GetExStyle(hwnd)&0x80000)==0,"Untiled minimize leaked its opacity lease.");checks["untiledMinimizeWithHiddenDock"]=new{frames=bounds.Distinct().Count(),destination=origin,last=bounds[^1]};
            NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);front.Activate();await Task.Delay(200);dock.SetFullscreenHidden(false);
            app.ToggleSearch();await app.Palette!.SearchForDiagnostics("");Check(app.Palette.ResultCount==0,"Fresh search displayed unsolicited apps.");
            await app.Palette.SearchForDiagnostics("Деинсталлировать");Check(app.Palette.ResultCount==0,"Search included an uninstaller.");app.Palette.Hide();checks["emptyPaletteAndNoiseFilter"]=true;
            var bar=app.TopBar!.Primary!;bar.IgnoreRealCursorForDiagnostics=true;
            await bar.OpenTrayForDiagnostics();await Task.Delay(150);Check(bar.TrayVisible,"Custom tray did not open.");
            if(Environment.GetCommandLineArgs().Contains("--test-real-capture141"))
            {
                INPUT Key(ushort key,bool up=false)=>new(){type=NativeMethods.INPUT_KEYBOARD,U=new(){ki=new(){wVk=key,dwFlags=up?NativeMethods.KEYEVENTF_KEYUP:0}}};
                var keys=new[]{Key(0x5B),Key(0x10),Key(0x53),Key(0x53,true),Key(0x10,true),Key(0x5B,true)};
                Check(NativeMethods.SendInput((uint)keys.Length,keys,Marshal.SizeOf<INPUT>())==keys.Length,"Could not issue the screenshot shortcut.");
                var monitor=new CaptureOverlayMonitor();
                await Wait(()=>{monitor.Scan();return monitor.IsActive;},"Win+Shift+S did not open the capture overlay while the tray was open.",6000);
                Check(!bar.Menus.SurfaceVisible,"Real screenshot shortcut did not dismiss the tray.");
                var escape=new[]{Key(0x1B),Key(0x1B,true)};NativeMethods.SendInput(2,escape,Marshal.SizeOf<INPUT>());
                await Wait(()=>{monitor.Scan();return !monitor.IsActive;},"Capture overlay did not close.");checks["realWinShiftSWithTray"]=true;
            }
            else app.Dock.BeginCaptureShortcut();
            Check(!bar.Menus.SurfaceVisible&&!NativeMethods.IsWindowVisible(bar.Menus.PopupHandle),"Screenshot shortcut left the tray visible.");
            Check(!FloatingDock.Windows.Integration.SystemTrayOverflow.Visible,"Screenshot shortcut left Explorer's overflow visible.");
            Check(CaptureShortcutWatcher.IsCaptureShortcut(0x53,true,true)&&CaptureShortcutWatcher.IsCaptureShortcut(0x2C,false,false)&&!CaptureShortcutWatcher.IsCaptureShortcut(0x53,false,true),"Screenshot key recognition is incorrect.");
            await Task.Delay(2200);checks["trayScreenshotShortcutDismissal"]=true;
        }
        finally
        {
            app.Palette?.Hide();app.Running.ExcludeOwnWindow(hwnd);front.Close();backdrop.Close();
            app.Dock.PrimaryWindowForDiagnostics?.SetFullscreenHidden(false);if(app.TopBar?.Primary is { } bar){bar.IgnoreRealCursorForDiagnostics=false;bar.Menus.Close(immediately:true);}
            app.Settings.Update(s=>{foreach(var p in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
        }
    }
}
