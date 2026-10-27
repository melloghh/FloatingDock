using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;

internal static partial class RuntimeSmoke
{
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW")] private static extern IntPtr CaptionHit160(IntPtr window,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out UIntPtr result);
    private static async Task CheckGlass160Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;
        app.Dock.HoldFullscreenStateForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;
        NativeMethods.GetCursorPos(out var cursor);
        var front=new Window {Title="FloatingDock caption and restore fixture",Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,38,108,156))}};
        var hwnd=WindowSetup.Hwnd(front);var ownedHwnd=hwnd;long id=hwnd.ToInt64();
        var args=Environment.GetCommandLineArgs();int external=Array.IndexOf(args,"--test-minimize-fixture");
        async Task Wait(Func<bool> predicate,string error,int timeout=4000)
        {var clock=Stopwatch.StartNew();while(!predicate()&&clock.ElapsedMilliseconds<timeout)await Task.Delay(10);Check(predicate(),error);}
        try
        {
            app.Settings.Update(s=>{s.TilingEnabled=false;s.TopBarEnabled=true;s.TopBarAutoHide=false;s.MenuAnimations=true;s.IconAnimation=true;s.IconAnimationMs=280;s.AnimationFps=144;});
            bar.SetFullscreen(false);bar.Target(false);
            app.UserTiles.Add(new AppRef{Name="Glass · File Explorer",ExePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),Source=AppSource.User});
            app.UserTiles.Add(new AppRef{Name="Glass · FloatingDock",ExePath=Environment.ProcessPath,Source=AppSource.User});
            app.UserTiles.Add(new AppRef{Name="Glass · Notepad",ExePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"notepad.exe"),Source=AppSource.User});
            app.ToggleSearch();await app.Palette!.SearchForDiagnostics("glass");await Task.Delay(400);
            Check(app.Palette.ResultCount>=3,"Search result fixture was not indexed.");
            var palette=app.Palette;var region=NativeMethods.CreateRectRgn(0,0,0,0);
            try
            {
                GetWindowRgn(palette.Handle,region);NativeMethods.GetWindowRect(palette.Handle,out var bounds);
                double scale=bar.Monitor.Scale;
                Check(PtInRegion(region,bounds.Width/2,(int)(28*scale)),"Search input plate is clipped out.");
                Check(!PtInRegion(region,bounds.Width/2,(int)(61*scale)),"Search glass fills the gap between its two surfaces.");
                Check(PtInRegion(region,bounds.Width/2,(int)(85*scale)),"Search results plate is missing.");
                OwnWindowCapture.Capture(palette.Handle).Save(Path.Combine(app.Paths.Root,"glass-search.png"));
            }
            finally {NativeMethods.DeleteObject(region);palette.Hide();}
            checks["separateSearchGlassPlates"]=true;
            app.ToggleLaunchpad();await app.LaunchpadWin!.LoadCatalogForSmokeAsync();await Task.Delay(500);
            var power=(MenuFlyout)app.LaunchpadWin!.PowerButtonForDiagnostics.Flyout;
            Check(power.Items.Count==2&&power.Items.OfType<MenuFlyoutItem>().Select(i=>i.Text).Contains(L.Text("Restart")),"Launchpad power actions are missing.");
            var powerRoot=app.LaunchpadWin.PowerButtonForDiagnostics.XamlRoot;
            var confirmation=PowerActions.ConfirmAsync(powerRoot,false);await Task.Delay(150);
            var dialog=AuditDescendants(powerRoot.Content).OfType<ContentDialog>().FirstOrDefault()??
                VisualTreeHelper.GetOpenPopupsForXamlRoot(powerRoot).SelectMany(p=>AuditDescendants(p.Child)).OfType<ContentDialog>().FirstOrDefault();
            Check(dialog is not null&&dialog.DefaultButton==ContentDialogButton.Close,"Shutdown confirmation has no safe default.");
            dialog!.Hide();await confirmation;
            OwnWindowCapture.Capture(app.LaunchpadWin.Handle).Save(Path.Combine(app.Paths.Root,"glass-launchpad.png"));
            app.LaunchpadWin.Hide();await Task.Delay(260);checks["powerActionsAvailableNoPowerExecuted"]=true;
            app.ShowSettings("Appearance");await Task.Delay(350);
            OwnWindowCapture.Capture(app.SettingsWin!.Handle).Save(Path.Combine(app.Paths.Root,"glass-settings.png"));
            app.SettingsWin.Close();await Task.Delay(100);

            WindowStyler.SetBounds(hwnd,bar.Monitor.WorkArea.Left+240,bar.Monitor.WorkArea.Top+160,900,620,topmost:true);
            app.Tiling.DiagnosticWindows=new HashSet<long>{id};app.Running.IncludeOwnWindow(hwnd);
            app.Tiling.NoteLaunch(new AppRef{Name="Owned opening",ExePath=Environment.ProcessPath});front.Activate();
            await Task.Delay(400);
            Check(!app.Tiling.PresentationsForDiagnostics.ContainsKey(id)&&(NativeMethods.GetExStyle(hwnd)&0x80000)==0,"Native opening was hidden behind a thumbnail.");checks["nativeOpeningWithoutSourceMutation"]=true;
            if(external>=0)
            {
                app.Running.ExcludeOwnWindow(hwnd);front.Close();
                hwnd=new(long.Parse(args[external+1]));id=hwnd.ToInt64();
                app.Tiling.DiagnosticWindows=new HashSet<long>{id};
                NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);WindowActivator.Activate(hwnd);
                WindowStyler.SetBounds(hwnd,bar.Monitor.WorkArea.Left+240,bar.Monitor.WorkArea.Top+160,900,620,topmost:true);
                app.Running.IncludeOwnWindow(hwnd);
            }
            await Wait(()=>app.Dock.AnimationRectFor(hwnd) is not null,"Caption fixture has no dock icon.");
            Check(!app.Tiling.CaptionRoutingInstalled,"Caption hook is installed outside tiling.");
            var cycles=new List<object>();
            foreach(var edge in new[]{DockEdge.Bottom,DockEdge.Left,DockEdge.Right})
            {
                app.Settings.Update(s=>s.Edge=edge);await Task.Delay(200);
                foreach(bool caption in new[]{false,true})
                {
                    NativeMethods.ShowWindowAsync(hwnd,NativeMethods.SW_RESTORE);WindowActivator.Activate(hwnd);await Wait(()=>!NativeMethods.IsIconic(hwnd),"Fixture did not restore.");await Task.Delay(400);
                    if(caption)
                    {
                        NativeMethods.GetWindowRect(hwnd,out var rect);POINT? button=null;
                        for(int x=rect.Right-150;x<rect.Right-30&&button is null;x+=5)
                            for(int y=rect.Top+8;y<rect.Top+42;y+=5)
                            {
                                int packed=(x&0xffff)|((y&0xffff)<<16);
                                if(CaptionHit160(hwnd,0x84,IntPtr.Zero,new(packed),2,20,out var hit)!=IntPtr.Zero&&hit.ToUInt64()==8){button=new POINT{X=x,Y=y};break;}
                            }
                        Check(button is not null,"Fixture has no native minimize hit target.");
                        // Default caption controls emit SC_MINIMIZE. Exercise
                        // that native command without racing the user's cursor.
                        NativeMethods.PostMessage(hwnd,0x112,new(0xF020),IntPtr.Zero);
                    }
                    else app.Tiling.MinimizeToDock(hwnd);
                    var timer=Stopwatch.StartNew();
                    await Wait(()=>NativeMethods.IsIconic(hwnd),"Native minimize did not complete.");
                    await Task.Delay(360);
                    Check(!app.Tiling.PresentationsForDiagnostics.ContainsKey(id),"Native minimize has a duplicate thumbnail.");
                    Check((NativeMethods.GetExStyle(hwnd)&0x80000)==0,"Minimize leaked its opacity style.");
                    app.Tiling.PrepareActivation(new[]{new WindowInfo(id,Environment.ProcessId,Environment.ProcessPath,null,"Caption fixture",null,true)});
                    await Wait(()=>!app.Tiling.Animating&&!NativeMethods.IsIconic(hwnd),"Untiled restore did not settle.");
                    Check(!app.Tiling.PresentationsForDiagnostics.ContainsKey(id),"Native restore has a duplicate thumbnail.");
                    await Task.Delay(400);
                    Check((NativeMethods.GetExStyle(hwnd)&0x80000)==0,"Restore leaked opacity.");
                    cycles.Add(new{edge=edge.ToString(),caption,native=true,milliseconds=timer.ElapsedMilliseconds});
                }
            }
            checks["sharedCaptionDockMinimizeAndRestore"]=cycles;
            app.Running.ExcludeOwnWindow(hwnd);if(NativeMethods.IsWindow(ownedHwnd))front.Close();
            checks["externalCaptionFixture"]=external>=0;
            await bar.OpenTrayForDiagnostics();await Task.Delay(400);
            Check(bar.TrayVisible,"Tray did not open.");
            OwnWindowCapture.Capture(bar.Menus.PopupHandle).Save(Path.Combine(app.Paths.Root,"glass-tray.png"));
            for(int cycle=0;cycle<2;cycle++)
            {
                var trayButton=AuditDescendants((DependencyObject)bar.Menus.ScrollForDiagnostics!.Content).OfType<Button>()
                    .FirstOrDefault(b=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(b)=="FloatingDock");
                Check(trayButton is not null,"The owned notification icon is absent from the live tray.");
                var point=trayButton!.TransformToVisual(trayButton.XamlRoot.Content).TransformPoint(new(trayButton.ActualWidth/2,trayButton.ActualHeight/2));
                NativeMethods.GetWindowRect(bar.Menus.PopupHandle,out var trayBounds);double scale=trayButton.XamlRoot.RasterizationScale;
                SetCursorPos(trayBounds.Left+(int)(point.X*scale),trayBounds.Top+(int)(point.Y*scale));
                NativeMethods.SendInput(1,new[]{new INPUT{U=new(){mi=new(){dwFlags=8}}}},Marshal.SizeOf<INPUT>());await Task.Delay(60);
                NativeMethods.SendInput(1,new[]{new INPUT{U=new(){mi=new(){dwFlags=16}}}},Marshal.SizeOf<INPUT>());
                await Wait(()=>SystemTrayOverflow.LastContextMenuHandle!=IntPtr.Zero&&NativeMethods.IsWindowVisible(SystemTrayOverflow.LastContextMenuHandle),"Owned tray context menu did not open: "+SystemTrayOverflow.LastDiagnostic);
                Check(bar.TrayVisible,"Right-click hid the tray behind its context menu.");
                await Task.Delay(150);EndMenu();await Task.Delay(300);
                Check(bar.TrayVisible,"Closing a tray context menu hid the tray.");
            }
            checks["ownedTrayContextMenuCycles"]=2;
            var watch=Stopwatch.StartNew();var first=await app.ReadTrayItemsAsync();var second=await app.ReadTrayItemsAsync();
            Check(ReferenceEquals(first,second)&&watch.ElapsedMilliseconds<100,"Warm tray enumerated Explorer again.");
            checks["warmTrayReadMilliseconds"]=watch.ElapsedMilliseconds;bar.Menus.Close(immediately:true);
            app.Settings.Update(s=>s.MenuAnimations=false);app.ToggleSearch();Check(!app.Palette!.MotionForDiagnostics.IsRunning,"Reduced motion started a search clock.");app.Palette.Hide();
            checks["reducedMotion"]=true;
        }
        finally
        {
            app.Palette?.Hide();app.SettingsWin?.Close();app.LaunchpadWin?.Hide();bar.Menus.Close(immediately:true);
            app.Running.ExcludeOwnWindow(hwnd);if(NativeMethods.IsWindow(ownedHwnd))front.Close();app.Tiling.DiagnosticWindows=null;
            SetCursorPos(cursor.X,cursor.Y);bar.IgnoreRealCursorForDiagnostics=false;app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var p in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
        }
    }
}
