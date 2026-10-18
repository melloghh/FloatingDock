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
            await Wait(()=>NativeMethods.IsIconic(hwnd),"Untiled user minimize did not reach native state.");
            var destination=app.Msg.AnimationRectProvider?.Invoke(hwnd);
            Check(origin is { } r&&destination is { } d&&r.Left==d.Left&&r.Top==d.Top&&r.Right==d.Right&&r.Bottom==d.Bottom,"Native minimize lost the hidden dock icon destination.");
            for(int sample=0;sample<25;sample++)
            {
                Check(!app.Tiling.PresentationsForDiagnostics.ContainsKey(id),"Untiled native minimization started a second image.");
                await Task.Delay(12);
            }
            Check(NativeMethods.IsIconic(hwnd)&&(NativeMethods.GetExStyle(hwnd)&0x80000)==0,"Native minimize leaked visibility or an opacity lease.");
            checks["untiledMinimizeWithHiddenDock"]=new{renderer="native",destination,duplicateSurfaces=0};
            NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);front.Activate();await Task.Delay(200);dock.SetFullscreenHidden(false);
            app.ToggleSearch();await app.Palette!.SearchForDiagnostics("");Check(app.Palette.ResultCount==0,"Fresh search displayed unsolicited apps.");
            await app.Palette.SearchForDiagnostics("Деинсталлировать");Check(app.Palette.ResultCount==0,"Search included an uninstaller.");app.Palette.Hide();checks["emptyPaletteAndNoiseFilter"]=true;
            var bar=app.TopBar!.Primary!;bar.IgnoreRealCursorForDiagnostics=true;
            await bar.OpenTrayForDiagnostics();await Task.Delay(100);Check(bar.TrayVisible,"Custom tray did not open: "+bar.DiagnosticState);
            NativeMethods.GetWindowRect(dock.Handle,out var dockBeforeCapture);NativeMethods.GetWindowRect(bar.Handle,out var barBeforeCapture);
            Check(NativeMethods.IsWindowVisible(dock.Handle)&&NativeMethods.IsWindowVisible(bar.Handle),"Capture setup does not contain both visible panels.");
            void PanelsUnchanged()
            {
                NativeMethods.GetWindowRect(dock.Handle,out var d);NativeMethods.GetWindowRect(bar.Handle,out var b);
                Check(NativeMethods.IsWindowVisible(dock.Handle)&&NativeMethods.IsWindowVisible(bar.Handle),"Screenshot selection hid the dock or menu bar.");
                Check(d.Left==dockBeforeCapture.Left&&d.Top==dockBeforeCapture.Top&&d.Right==dockBeforeCapture.Right&&d.Bottom==dockBeforeCapture.Bottom&&b.Left==barBeforeCapture.Left&&b.Top==barBeforeCapture.Top&&b.Right==barBeforeCapture.Right&&b.Bottom==barBeforeCapture.Bottom,"Screenshot selection moved or collapsed a panel.");
            }
            if(Environment.GetCommandLineArgs().Contains("--test-real-capture141"))
            {
                INPUT Key(ushort key,bool up=false)=>new(){type=NativeMethods.INPUT_KEYBOARD,U=new(){ki=new(){wVk=key,dwFlags=up?NativeMethods.KEYEVENTF_KEYUP:0}}};
                Check(FloatingDock.Windows.Shell.ShellLauncher.Execute("ms-screenclip:",null,null),"Could not open screenshot selection.");
                var monitor=new CaptureOverlayMonitor();
                await Wait(()=>{monitor.Scan();return monitor.IsActive;},"Win+Shift+S did not open the capture overlay while the tray was open.",6000);
                for(int sample=0;sample<4;sample++)
                {
                    PanelsUnchanged();Check(bar.Menus.SurfaceVisible,"Screenshot selection dismissed the custom tray.");
                    Check(!NativeMethods.IsWindowVisible(bar.InputHandle),"Capture input surface covers the real selection overlay.");
                    foreach(var panel in new[]{bar.Handle,dock.Handle,bar.Menus.PopupHandle})
                        Check((NativeMethods.GetExStyle(panel)&NativeMethods.WS_EX_TOPMOST)==0,"A panel remains topmost during real screenshot selection.");
                    await Task.Delay(80);
                }
                var escape=new[]{Key(0x1B),Key(0x1B,true)};NativeMethods.SendInput(2,escape,Marshal.SizeOf<INPUT>());
                await Wait(()=>{monitor.Scan();return !monitor.IsActive;},"Capture overlay did not close.");checks["realScreenshotSelectionWithTray"]=true;
            }
            else app.Dock.BeginCaptureShortcut();
            PanelsUnchanged();
            Check(!FloatingDock.Windows.Integration.SystemTrayOverflow.Visible,"Screenshot shortcut left Explorer's overflow visible.");
            Check(CaptureShortcutWatcher.IsCaptureShortcut(0x53,true,true)&&CaptureShortcutWatcher.IsCaptureShortcut(0x2C,false,false)&&!CaptureShortcutWatcher.IsCaptureShortcut(0x53,false,true),"Screenshot key recognition is incorrect.");
            await Task.Delay(2200);checks["capturePreservesPanelsAndTray"]=true;
        }
        finally
        {
            app.Palette?.Hide();app.Running.ExcludeOwnWindow(hwnd);front.Close();backdrop.Close();
            app.Dock.PrimaryWindowForDiagnostics?.SetFullscreenHidden(false);if(app.TopBar?.Primary is { } bar){bar.IgnoreRealCursorForDiagnostics=false;bar.Menus.Close(immediately:true);}
            app.Settings.Update(s=>{foreach(var p in typeof(FloatingDock.Core.Configuration.DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
        }
    }
}
