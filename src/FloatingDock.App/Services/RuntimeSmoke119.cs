using FloatingDock.Core.Configuration;
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
    private static async Task CheckUi119Async(DockApp app,Dictionary<string,object> checks)
    {
        var dock=app.Dock.PrimaryWindowForDiagnostics!;
        var original=app.Settings.Current.Clone();var pins=app.Pinned.Snapshot();
        app.Dock.HoldFullscreenStateForDiagnostics=true;app.Dock.HoldWindowListForDiagnostics=true;
        dock.IgnoreRealCursorForDiagnostics=true;
        Window? probe=null;
        var pin=new AppRef { Name="Stable pinned probe",ExePath=Path.Combine(app.Paths.Root,"pinned-probe.exe") };
        static bool Same(RECT a,RECT b)=>a.Left==b.Left&&a.Top==b.Top&&a.Right==b.Right&&a.Bottom==b.Bottom;
        try
        {
            app.Pinned.ReplaceAll(new[]{pin});app.Dock.ViewModel.SetWindows(Array.Empty<WindowInfo>(),0);
            var results=new List<object>();
            foreach(var edge in new[]{DockEdge.Left,DockEdge.Right,DockEdge.Bottom})
            {
                app.Settings.Update(s=> { s.Edge=edge;s.AutoHide=false;s.ShowRunning=true;s.WidthMode=WidthMode.Auto;s.Alignment=DockAlignment.Center;s.HorizontalOffset=0;s.MagnificationEnabled=true;s.IconAnimation=true;s.IconAnimationMs=260; });
                dock.SetFullscreenHidden(false);dock.Reveal();await Task.Delay(450);
                var baseline=NativeRect(dock.Handle);var baselinePlate=dock.RenderedPlateScreenForDiagnostics;
                int samples=0;
                for(int cycle=0;cycle<2;cycle++)
                {
                    probe=new Window { Title="Own unpinned lifecycle probe",Content=new Grid { Background=new SolidColorBrush(Microsoft.UI.Colors.DarkGreen) } };
                    var hwnd=WindowSetup.Hwnd(probe);WindowStyler.SetBounds(hwnd,dock.Monitor.Bounds.Left+500,dock.Monitor.Bounds.Top+180,240,160,topmost:false);probe.AppWindow.Show(false);
                    var info=new WindowInfo(hwnd.ToInt64(),Environment.ProcessId,Path.Combine(app.Paths.Root,"unpinned-probe.exe"),null,probe.Title,null,false);
                    var iconBefore=dock.IconScreenForDiagnostics(pin.Key);
                    app.Dock.ViewModel.SetWindows(new[]{info},hwnd.ToInt64());
                    var immediately=NativeRect(dock.Handle);var iconAfter=dock.IconScreenForDiagnostics(pin.Key);
                    checks["beforeFirstFrame"]=new { edge=edge.ToString(),nativeShiftX=immediately.Left-baseline.Left,nativeShiftY=immediately.Top-baseline.Top,iconShiftX=iconAfter.Left-iconBefore.Left,iconShiftY=iconAfter.Top-iconBefore.Top };
                    Check(Same(baseline,immediately),"Adding an unpinned app immediately moved/resized the dock HWND: "+edge);
                    Check(Same(iconBefore,iconAfter),"Existing icon jumped before the first appear frame: "+edge);
                    Check(app.Dock.ViewModel.Entries.Any(e=>e.Kind==DockEntryKind.App&&!e.IsPinned&&e.Windows.Any(w=>w.Hwnd==hwnd.ToInt64())),"Unpinned native window did not produce a running dock slot.");
                    for(int frame=0;frame<24;frame++)
                    {
                        await Task.Delay(16);Check(Same(baseline,NativeRect(dock.Handle)),"Dock HWND changed during app appearance.");
                        var plate=dock.RenderedPlateScreenForDiagnostics;
                        Check(edge==DockEdge.Bottom?Math.Abs((plate.Left+plate.Right)-(baselinePlate.Left+baselinePlate.Right))<=3:Math.Abs((plate.Top+plate.Bottom)-(baselinePlate.Top+baselinePlate.Bottom))<=3,"The rendered plate center jumped during app appearance.");samples++;
                    }
                    probe.Close();probe=null;app.Dock.ViewModel.SetWindows(Array.Empty<WindowInfo>(),0);
                    for(int frame=0;frame<28;frame++)
                    {
                        await Task.Delay(16);Check(Same(baseline,NativeRect(dock.Handle)),"Dock HWND changed during app removal or ghost cleanup.");
                        var plate=dock.RenderedPlateScreenForDiagnostics;
                        Check(edge==DockEdge.Bottom?Math.Abs((plate.Left+plate.Right)-(baselinePlate.Left+baselinePlate.Right))<=3:Math.Abs((plate.Top+plate.Bottom)-(baselinePlate.Top+baselinePlate.Bottom))<=3,"The rendered plate center jumped during app removal.");samples++;
                    }
                    var restored=dock.RenderedPlateScreenForDiagnostics;
                    Check(Same(restored,baselinePlate),"The plate did not return to its original size after closing the app.");
                }
                // A larger stable HWND must not claim transparent space away from the dock.
                dock.BeginRenderDiagnostics(stationary:true);await Task.Delay(250);
                var region=NativeMethods.CreateRectRgn(0,0,0,0);
                try { Check(GetWindowRgn(dock.Handle,region)>0&&!PtInRegion(region,0,0),"Hover captures unrelated transparent monitor space."); }
                finally { NativeMethods.DeleteObject(region);dock.EndRenderDiagnostics(); }
                await Task.Delay(350);
                results.Add(new { edge=edge.ToString(),cycles=2,stableNativeSamples=samples,initialIconJumpPx=0,plateCenterStable=true,restoredPlate=true,hoverRegionBounded=true });
            }
            checks["unpinnedLifecycle"]=results;
            Check(!dock.IsAnimating,"The dock frame loop did not settle after app removal.");
            checks["idleLoopStopped"]=true;
        }
        finally
        {
            probe?.Close();app.Dock.ViewModel.SetWindows(Array.Empty<WindowInfo>(),0);app.Pinned.ReplaceAll(pins);
            app.Settings.Update(s=> { s.Edge=original.Edge;s.AutoHide=original.AutoHide;s.ShowRunning=original.ShowRunning;s.WidthMode=original.WidthMode;s.Alignment=original.Alignment;s.HorizontalOffset=original.HorizontalOffset;s.MagnificationEnabled=original.MagnificationEnabled;s.IconAnimation=original.IconAnimation;s.IconAnimationMs=original.IconAnimationMs; });
            dock.IgnoreRealCursorForDiagnostics=false;app.Dock.HoldFullscreenStateForDiagnostics=false;app.Dock.HoldWindowListForDiagnostics=false;
        }
    }
}
