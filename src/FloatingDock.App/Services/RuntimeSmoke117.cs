using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;

internal static partial class RuntimeSmoke
{
    private static async Task CheckUi117Async(DockApp app,Dictionary<string,object> checks)
    {
        var dock=app.Dock.PrimaryWindowForDiagnostics!;var bar=app.TopBar!.Primary!;
        app.Dock.HoldFullscreenStateForDiagnostics=true;dock.IgnoreRealCursorForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;
        var monitor=FloatingDock.Windows.Display.MonitorHelper.Describe(WindowStyler.MonitorOf(bar.Handle))!;
        var background=new Grid();
        for(int i=0;i<96;i++)
        {
            background.ColumnDefinitions.Add(new ColumnDefinition());
            var stripe=new Border { Background=new SolidColorBrush(i%2==0?Microsoft.UI.Colors.White:Microsoft.UI.Colors.Black) };
            Grid.SetColumn(stripe,i);background.Children.Add(stripe);
        }
        var probe=new Window { Title="FloatingDock backdrop pixel probe",Content=background };
        var probeHandle=WindowSetup.Hwnd(probe);WindowSetup.MakeBorderless(probe);
        WindowStyler.SetBounds(probeHandle,monitor.Bounds.Left+20,monitor.Bounds.Top+50,monitor.Bounds.Width-40,monitor.Bounds.Height-80);
        probe.Activate();
        static int DifferentPixels(OwnWindowCapture.Frame before,OwnWindowCapture.Frame after,int left,int top,int width,int height)
        {
            int different=0;
            for(int y=top;y<top+height;y++)for(int x=left;x<left+width;x++)
            {
                int p=(y*before.Width+x)*4;
                if(Math.Abs(before.Pixels[p]-after.Pixels[p])>1||Math.Abs(before.Pixels[p+1]-after.Pixels[p+1])>1||Math.Abs(before.Pixels[p+2]-after.Pixels[p+2])>1)different++;
            }
            return different;
        }
        try
        {
            await Task.Delay(350);
            var menuChecks=new List<object>();
            foreach(var kind in new[]{"main","network","sound","language","calendar"})
            {
                var before=OwnWindowCapture.Capture(probeHandle);
                bar.OpenMenuForDiagnostics(kind);await Task.Delay(kind=="network"?1100:320);
                var popup=bar.Menus.PopupHandle;var popupRect=NativeRect(popup);var probeRect=NativeRect(probeHandle);
                int left=40,top=30;
                Check(popupRect.Width+left<probeRect.Width&&popupRect.Height+top<probeRect.Height,"Probe monitor is too small for the quick menu.");
                WindowStyler.SetBounds(popup,probeRect.Left+left,probeRect.Top+top,popupRect.Width,popupRect.Height);
                await Task.Delay(100);
                var region=NativeMethods.CreateRectRgn(0,0,0,0);
                try
                {
                    Check(GetWindowRgn(popup,region)>0,"Quick menu has no native backdrop clip: "+kind);
                    Check(!PtInRegion(region,0,0)&&!PtInRegion(region,popupRect.Width-1,0)&&!PtInRegion(region,0,popupRect.Height-1)&&!PtInRegion(region,popupRect.Width-1,popupRect.Height-1),"Native blur reaches outside rounded corners: "+kind);
                    Check(PtInRegion(region,popupRect.Width/2,popupRect.Height/2),"Quick-menu clipping removed its content.");
                }
                finally { NativeMethods.DeleteObject(region); }
                var visible=OwnWindowCapture.Capture(probeHandle);
                Check(DifferentPixels(before,visible,left,top,3,3)==0,"Top-left corner changes the underlying pixels: "+kind);
                Check(DifferentPixels(before,visible,left+popupRect.Width-3,top+popupRect.Height-3,3,3)==0,"Bottom-right corner changes the underlying pixels: "+kind);
                var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(null);
                try { bar.Menus.Close(); }finally { SynchronizationContext.SetSynchronizationContext(context); }
                await Task.Delay(300);
                var after=OwnWindowCapture.Capture(probeHandle);
                int residual=DifferentPixels(before,after,left,top,popupRect.Width,popupRect.Height);
                Check(residual==0,"Closing left altered desktop pixels: "+kind+", count="+residual);
                after.Save(Path.Combine(app.Paths.Root,"after-close-"+kind+".png"));
                menuChecks.Add(new { kind,nativeRoundedClip=true,unchangedCornerPixels=true,residualPixels=residual });
            }
            checks["backdropPixels"]=menuChecks;
            app.ShowSettings("Dock");await Task.Delay(400);
            var settings=app.SettingsWin!;var settingsHandle=settings.Handle;
            WindowStyler.SetBounds(settingsHandle,monitor.Bounds.Left+40,monitor.Bounds.Top+65,Math.Min(980,monitor.Bounds.Width-80),Math.Min(740,monitor.Bounds.Height-110));
            await Task.Delay(250);
            var settingsBefore=OwnWindowCapture.Capture(settingsHandle);
            background.Background=new SolidColorBrush(Microsoft.UI.Colors.Red);
            foreach(var stripe in background.Children.Cast<Border>())stripe.Background=new SolidColorBrush(Microsoft.UI.Colors.Red);
            await Task.Delay(250);
            var settingsAfter=OwnWindowCapture.Capture(settingsHandle);
            Check(DifferentPixels(settingsBefore,settingsAfter,40,250,100,100)==0,"Settings background still samples the underlying window.");
            foreach(var page in new[]{"Appearance","Dock","Menu Bar","Dock"}) { app.ShowSettings(page);await Task.Delay(260); }
            NativeMethods.ShowWindowAsync(settingsHandle,NativeMethods.SW_MINIMIZE);await Task.Delay(250);
            Check(NativeMethods.IsIconic(settingsHandle),"Settings did not minimize.");
            NativeMethods.ShowWindowAsync(settingsHandle,NativeMethods.SW_RESTORE);await Task.Delay(400);
            Check(!NativeMethods.IsIconic(settingsHandle),"Settings did not restore.");
            OwnWindowCapture.Capture(settingsHandle).Save(Path.Combine(app.Paths.Root,"settings-restored.png"));
            settings.Close();await Task.Delay(200);
            Check(!NativeMethods.IsWindow(settingsHandle),"Closed settings kept its native window.");
            checks["settingsSurface"]=new { opaqueBackground=true,pageNavigation=true,minimizeRestore=true,closedWindowReleased=true };
        }
        finally
        {
            app.SettingsWin?.Close();bar.Menus.Close(immediately:true);probe.Close();
            app.Dock.HoldFullscreenStateForDiagnostics=false;dock.IgnoreRealCursorForDiagnostics=false;bar.IgnoreRealCursorForDiagnostics=false;
        }
    }
}
