using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace FloatingDock.UI.Services;

internal static partial class RuntimeSmoke
{
    private delegate IntPtr CloseProbeProc(IntPtr hwnd,uint message,IntPtr w,IntPtr l,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd,CloseProbeProc proc,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd,CloseProbeProc proc,UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd,uint message,IntPtr w,IntPtr l);
    private static async Task CheckUi116Async(DockApp app,Dictionary<string,object> checks)
    {
        var dock=app.Dock.PrimaryWindowForDiagnostics!;var bar=app.TopBar!.Primary!;
        var original=app.Settings.Current.Clone();Window? probe=null;
        CloseProbeProc? refusal=null;IntPtr refusalHandle=IntPtr.Zero;var subclassId=new UIntPtr(0x116);
        app.Dock.HoldFullscreenStateForDiagnostics=true;app.Dock.HoldWindowListForDiagnostics=true;
        dock.IgnoreRealCursorForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;
        static void WithoutContext(Action action)
        {
            var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(null);
            try { action(); }finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        static void Invoke(Button button)
        {
            var peer=new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
        static bool Above(IntPtr top,IntPtr bottom)
        {
            bool found=false,result=false;
            NativeMethods.EnumWindows((h,_)=> { if(h==top)found=true;if(h==bottom){result=found;return false;}return true; },IntPtr.Zero);
            return result;
        }
        try
        {
            app.Settings.Update(s=> { s.Edge=DockEdge.Left;s.AutoHide=false;s.TopBarEnabled=true;s.TopBarAutoHide=false; });
            dock.SetFullscreenHidden(false);bar.SetFullscreen(false);await Task.Delay(400);
            var menuResults=new List<object>();
            foreach(var kind in new[]{"main","sound","network","language","calendar"})
            {
                bar.OpenMenuForDiagnostics(kind);await Task.Delay(240);
                var popup=bar.Menus.PopupHandle;
                Check(bar.Menus.ActiveKind==kind&&WindowStyler.IsVisible(popup),"Menu failed to open: "+kind);
                dock.Reassert();await Task.Delay(70);
                Check(Above(popup,dock.Handle),"The dock covers the quick menu: "+kind);
                if(kind=="main")OwnWindowCapture.Capture(popup).Save(Path.Combine(app.Paths.Root,"main-above-side-dock.png"));
                WithoutContext(()=>bar.Menus.Close());
                await Task.Delay(45);
                if(SurfaceMotion.Enabled)Check(WindowStyler.IsVisible(popup)&&bar.Menus.SurfaceVisible,"Closing skipped its finite animation.");
                await Task.Delay(220);
                Check(!WindowStyler.IsVisible(popup)&&!bar.Menus.SurfaceVisible,"Native callback left a faded menu: "+kind);
                // Network completion and foreground/window-list updates must not resurrect it.
                dock.Reassert();await Task.Delay(700);
                Check(!WindowStyler.IsVisible(popup),"Closed menu reappeared after background updates: "+kind);
                menuResults.Add(new { kind,withoutSynchronizationContext=true,nativeWindowHidden=true,noDelayedResurrection=true,aboveDock=true });
            }
            checks["quickMenuLifecycle"]=menuResults;
            bar.OpenMenuForDiagnostics("main");await Task.Delay(35);WithoutContext(()=>bar.Menus.Close());await Task.Delay(35);
            bar.OpenMenuForDiagnostics("sound");await Task.Delay(240);
            Check(bar.Menus.ActiveKind=="sound"&&WindowStyler.IsVisible(bar.Menus.PopupHandle),"A stale timer hid the replacement menu.");
            bar.SetCaptureHidden(true);Check(!WindowStyler.IsVisible(bar.Menus.PopupHandle),"Capture dismissal was animated over the screenshot overlay.");
            bar.SetCaptureHidden(false);bar.Target(false);await Task.Delay(200);
            checks["interruptedMenus"]=new { replacementSurvivesOldClose=true,captureHidesImmediately=true };
            await app.PrepareLaunchpadAsync();var launchpad=app.LaunchpadWin!;launchpad.Show();await Task.Delay(380);
            var tiles=launchpad.TilesForDiagnostics;
            Check(tiles.Count>0,"Launchpad has no realized tiles.");
            foreach(var tile in tiles.Take(4))tile.SetHovered(true);
            await Task.Delay(240);
            foreach(var tile in tiles.Take(4))
            {
                var highlight=tile.HighlightForDiagnostics;var point=highlight.TransformToVisual(tile).TransformPoint(new global::Windows.Foundation.Point());
                var scale=ElementCompositionPreview.GetElementVisual(tile).Scale;
                Check(scale.X==1&&scale.Y==1,"The whole Launchpad tile still scales beyond its cell.");
                Check(point.X>=-.5&&point.Y>=-.5&&point.X+highlight.ActualWidth<=tile.ActualWidth+.5&&point.Y+highlight.ActualHeight<=tile.ActualHeight+.5,"Rounded highlight extends outside its tile.");
            }
            OwnWindowCapture.Capture(launchpad.Handle).Save(Path.Combine(app.Paths.Root,"launchpad-bounded-hover.png"));
            foreach(var tile in tiles)tile.ResetMotion();
            WithoutContext(launchpad.Hide);await Task.Delay(280);
            Check(!WindowStyler.IsVisible(launchpad.Handle)&&!launchpad.IsOpen,"Launchpad dismissal requires a captured UI context.");
            launchpad.Show();await Task.Delay(45);WithoutContext(launchpad.Hide);await Task.Delay(40);launchpad.Show();await Task.Delay(400);
            Check(launchpad.IsOpen&&WindowStyler.IsVisible(launchpad.Handle),"An interrupted Launchpad transition hid the reopened window.");
            launchpad.Hide();await Task.Delay(280);
            checks["launchpadHighlight"]=new { cellsChecked=Math.Min(4,tiles.Count),fixedBounds=true,roundedFill=true,onlyIconScales=true,nativeContextDismissal=true,reversalSafe=true };
            Window NewProbe(string title)
            {
                var window=new Window { Title=title,Content=new Grid { Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,34,187,153)) } };
                WindowSetup.MakeBorderless(window);WindowStyler.SetBounds(WindowSetup.Hwnd(window),450,240,640,360,topmost:false);
                window.AppWindow.Show(false);return window;
            }
            DockEntry Entry(Window window)=>new() { Kind=DockEntryKind.App,Key="close116",Name="Own close probe",Windows=new[]{new WindowInfo(WindowSetup.Hwnd(window).ToInt64(),Environment.ProcessId,Environment.ProcessPath,null,window.Title,null,false)} };
            var preview=dock.PreviewForDiagnostics;
            probe=NewProbe("Animated close probe");bool closed=false;probe.Closed+=(_,_)=>closed=true;
            var source=WindowSetup.Hwnd(probe);WindowStyler.BringToFront(source);await Task.Delay(180);
            var before=NativeRect(source);preview.Track(Entry(probe),dock.PlateScreenForDiagnostics,diagnostic:true,edge:DockEdge.Left);await Task.Delay(1050);
            Check(preview.Visible&&preview.CloseButtons.Count==1,"The close-animation preview failed to open.");
            OwnWindowCapture.Capture(preview.Handle).Save(Path.Combine(app.Paths.Root,"preview-before-close.png"));
            WithoutContext(()=>Invoke(preview.CloseButtons.Single()));await Task.Delay(45);
            if(SurfaceMotion.Enabled)Check(!closed&&preview.Visible&&preview.PendingCloseCount==1,"Closing did not wait for the preview animation.");
            var during=NativeRect(source);Check(before.Left==during.Left&&before.Top==during.Top&&before.Width==during.Width&&before.Height==during.Height,"Preview close animation resized the source window.");
            OwnWindowCapture.Capture(preview.Handle).Save(Path.Combine(app.Paths.Root,"preview-during-close.png"));
            await Task.Delay(500);Check(closed&&!preview.Visible&&preview.ThumbnailCount==0&&preview.PendingCloseCount==0,"Preview close did not finish or release its thumbnail.");probe=null;
            checks["animatedPreviewClose"]=new { delayBeforeCloseMs=160,sourceGeometryUnchanged=true,finiteFadeAndUniformScale=true,sourceClosed=true,thumbnailReleased=true };
            probe=NewProbe("Application refusing close");refusalHandle=WindowSetup.Hwnd(probe);int requests=0;
            refusal=(h,m,w,l,id,data)=> { if(m==0x10){requests++;return IntPtr.Zero;}return DefSubclassProc(h,m,w,l); };
            Check(SetWindowSubclass(refusalHandle,refusal,subclassId,UIntPtr.Zero),"Could not configure the own close-refusal probe.");
            preview.Track(Entry(probe),dock.PlateScreenForDiagnostics,diagnostic:true,edge:DockEdge.Left);await Task.Delay(1050);
            Invoke(preview.CloseButtons.Single());await Task.Delay(850);
            Check(requests==1&&NativeMethods.IsWindow(refusalHandle)&&preview.Visible&&preview.PendingCloseCount==0&&preview.CloseButtons.Single().IsEnabled,"A refused close did not recover the preview or was retried.");
            OwnWindowCapture.Capture(preview.Handle).Save(Path.Combine(app.Paths.Root,"preview-close-refused-restored.png"));
            checks["refusedClose"]=new { oneNormalCloseRequest=true,noForcedTermination=true,previewRestored=true,closeButtonEnabled=true };
            preview.Hide();RemoveWindowSubclass(refusalHandle,refusal,subclassId);refusalHandle=IntPtr.Zero;probe.Close();probe=null;
            probe=NewProbe("Close then leave preview");closed=false;probe.Closed+=(_,_)=>closed=true;
            preview.Track(Entry(probe),dock.PlateScreenForDiagnostics,diagnostic:true,edge:DockEdge.Left);await Task.Delay(1050);
            Invoke(preview.CloseButtons.Single());preview.Hide();await Task.Delay(300);
            Check(closed&&preview.PendingCloseCount==0,"Leaving the preview silently cancelled the user's close request.");probe=null;
            checks["closeAfterPreviewDismissal"]=true;
        }
        finally
        {
            if(refusalHandle!=IntPtr.Zero&&refusal is not null)RemoveWindowSubclass(refusalHandle,refusal,subclassId);
            probe?.Close();dock.PreviewForDiagnostics.Hide();app.LaunchpadWin?.Hide();bar.Menus.Close(immediately:true);
            app.Settings.Update(s=> { s.Edge=original.Edge;s.AutoHide=original.AutoHide;s.TopBarEnabled=original.TopBarEnabled;s.TopBarAutoHide=original.TopBarAutoHide; });
            dock.IgnoreRealCursorForDiagnostics=false;bar.IgnoreRealCursorForDiagnostics=false;
            app.Dock.HoldFullscreenStateForDiagnostics=false;app.Dock.HoldWindowListForDiagnostics=false;
        }
    }
}
