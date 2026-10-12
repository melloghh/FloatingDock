using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;

internal static partial class RuntimeSmoke
{
    private static async Task CheckUi118Async(DockApp app,Dictionary<string,object> checks)
    {
        var dock=app.Dock.PrimaryWindowForDiagnostics!;var bar=app.TopBar!.Primary!;
        var original=app.Settings.Current.Clone();var monitor=dock.Monitor;var pins=app.Pinned.Snapshot();
        app.Dock.HoldFullscreenStateForDiagnostics=true;app.Dock.HoldWindowListForDiagnostics=true;
        dock.IgnoreRealCursorForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;
        var a=new AppRef { Name="Auto-hide probe A",ExePath=Path.Combine(app.Paths.Root,"probe-a.exe") };
        var b=new AppRef { Name="Auto-hide probe B",ExePath=Path.Combine(app.Paths.Root,"probe-b.exe") };
        var separator=AppRef.Separator();
        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);
                if(child is T match)yield return match;
                foreach(var nested in Descendants<T>(child))yield return nested;
            }
        }
        static void Invoke(Button button)
        {
            var peer=new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
        try
        {
            app.Pinned.ReplaceAll(new[]{a,separator,b});
            var results=new List<object>();
            foreach(var edge in new[]{DockEdge.Left,DockEdge.Right,DockEdge.Bottom})
            {
                app.Settings.Update(s=> { s.Edge=edge;s.AutoHide=true;s.ShowRunning=false;s.TopBarEnabled=true;s.TopBarAutoHide=false;s.BottomOffset=19; });
                dock.SetFullscreenHidden(false);dock.Reveal();await Task.Delay(380);
                var full=monitor with { WorkArea=monitor.Bounds };
                var reserved=monitor with { WorkArea=monitor.Bounds with { Top=monitor.Bounds.Top+(int)Math.Round(36*monitor.Scale) } };
                dock.SetMonitor(full);var initial=dock.Bounds;
                dock.SetMonitor(reserved);var moved=dock.Bounds;
                int drift=edge==DockEdge.Bottom?moved.X-initial.X:moved.Y-initial.Y;
                checks["workAreaBaseline"]=new { edge=edge.ToString(),driftPx=drift };
                Check(drift==0,"Auto-hidden dock jumps along its edge when the top reservation changes: "+edge+", drift="+drift+"px.");
                int samples=0;
                for(int cycle=0;cycle<2;cycle++)
                {
                    dock.Reveal();
                    for(int frame=0;frame<82;frame++)
                    {
                        await Task.Delay(16);dock.SetMonitor(frame%2==0?full:reserved);
                        var bounds=NativeRect(dock.Handle);
                        Check(edge==DockEdge.Bottom?bounds.Left==initial.X:bounds.Top==initial.Y,"Native dock changed its along-edge anchor during slide: "+edge);
                        var plate=dock.PlateScreenForDiagnostics;
                        Check(edge==DockEdge.Bottom?Math.Abs((plate.Left+plate.Right)/2.0-(monitor.Bounds.Left+monitor.Bounds.Right)/2.0)<2:Math.Abs((plate.Top+plate.Bottom)/2.0-(monitor.Bounds.Top+monitor.Bounds.Bottom)/2.0)<2,"The plate left its stable monitor center: "+edge);
                        samples++;
                    }
                    Check(dock.HideAmount==1,"Auto-hide did not finish: "+edge);
                    Check(edge==DockEdge.Bottom?NativeRect(dock.Handle).Height==2:NativeRect(dock.Handle).Width==2,"Auto-hide reveal strip uses the wrong axis.");
                }
                dock.Reveal();await Task.Delay(300);Check(dock.HideAmount==0,"Reveal did not finish: "+edge);
                results.Add(new { edge=edge.ToString(),workAreaDriftPx=drift,cycles=2,stableNativeSamples=samples,hideRevealFinished=true });
            }
            checks["autoHideAnchors"]=results;
            app.Settings.Update(s=> { s.AutoHide=false;s.Edge=DockEdge.Left; });
            app.ShowSettings("Dock");await Task.Delay(350);
            var settings=app.SettingsWin!;
            var labels=Descendants<TextBlock>(settings.XamlRoot!.Content).Select(t=>t.Text).ToArray();
            Check(labels.Contains(a.Name)&&labels.Contains(b.Name)&&!labels.Contains(separator.Name),"The pinned-app list includes a separator or omits an app.");
            var firstRow=Descendants<Grid>(settings.XamlRoot.Content).Single(g=>g.Children.OfType<TextBlock>().Any(t=>t.Text==a.Name));
            Invoke(firstRow.Children.OfType<Button>().Single(button=>Equals(button.Content,"▼")));await Task.Delay(200);
            var reordered=app.Pinned.Snapshot();
            Check(reordered.Select(p=>p.Key).SequenceEqual(new[]{b.Key,separator.Key,a.Key}),"Moving a visible app lost or moved the hidden separator.");
            app.ShowSettings("Icon Packs");await Task.Delay(350);
            labels=Descendants<TextBlock>(settings.XamlRoot.Content).Select(t=>t.Text).ToArray();
            Check(labels.Contains(a.Name)&&labels.Contains(b.Name)&&!labels.Contains(separator.Name),"A separator appears among individual app icons.");
            Check(app.Dock.ViewModel.Entries.Any(e=>e.Kind==DockEntryKind.Separator&&e.Key==separator.Key),"Filtering settings removed the divider from the dock.");
            checks["separatorSettings"]=new { hiddenFromPinnedApps=true,hiddenFromAppIcons=true,reorderPreservesDividerPosition=true,dockDividerPreserved=true };
            settings.Close();
        }
        finally
        {
            app.SettingsWin?.Close();app.Pinned.ReplaceAll(pins);
            app.Settings.Update(s=> { s.Edge=original.Edge;s.AutoHide=original.AutoHide;s.ShowRunning=original.ShowRunning;s.TopBarEnabled=original.TopBarEnabled;s.TopBarAutoHide=original.TopBarAutoHide;s.BottomOffset=original.BottomOffset; });
            dock.SetMonitor(monitor);dock.SetFullscreenHidden(false);
            dock.IgnoreRealCursorForDiagnostics=false;bar.IgnoreRealCursorForDiagnostics=false;
            app.Dock.HoldFullscreenStateForDiagnostics=false;app.Dock.HoldWindowListForDiagnostics=false;
        }
    }
}
