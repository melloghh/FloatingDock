using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.UI.Views;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region,out RECT rect);
    private static RECT MenuRegion148(IntPtr hwnd)
    {
        var region=NativeMethods.CreateRectRgn(0,0,0,0);
        try{Check(GetWindowRgn(hwnd,region)>0,"The actual menu has no native clip.");GetRgnBox(region,out var box);return box;}
        finally{NativeMethods.DeleteObject(region);}
    }
    private static async Task CheckUi148Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;var dock=app.Dock.PrimaryWindowForDiagnostics!;
        var mon=bar.Monitor;const uint backgroundColour=36u|(108u<<8)|(164u<<16);
        var background=new Window {Title="FloatingDock menu motion backdrop",Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,36,108,164))}};
        var backgroundHwnd=WindowSetup.Hwnd(background);WindowSetup.MakeBorderless(background);
        WindowStyler.MakeOverlayWindow(backgroundHwnd,noActivate:true);
        app.Dock.HoldFullscreenStateForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;dock.IgnoreRealCursorForDiagnostics=true;
        var results=new List<object>();
        try
        {
            app.Settings.Update(s=>{s.TopBarEnabled=true;s.TopBarAutoHide=false;s.TilingEnabled=false;s.MenuAnimations=true;s.MenuAnimationMs=200;});
            WindowStyler.SetBounds(backgroundHwnd,mon.Bounds.Left,mon.Bounds.Top,mon.Bounds.Width,mon.Bounds.Height);background.AppWindow.Show(false);
            bar.SetFullscreen(false);bar.Target(false);await Task.Delay(250);
            Check(SurfaceMotion.Enabled,"Windows animations must be enabled for the motion check.");
            foreach(var kind in new[]{"main","sound","language","calendar","network","media","tray"})
            {
                if(kind=="tray")await bar.OpenTrayForDiagnostics();else bar.OpenMenuForDiagnostics(kind);
                var hwnd=bar.Menus.PopupHandle;await Task.Delay(35);
                var early=MenuRegion148(hwnd);NativeMethods.GetWindowRect(hwnd,out var bounds);
                var motion=bar.Menus.MotionForDiagnostics!;
                Check(motion.IsRunning&&motion.Progress>0&&motion.Progress<1,"The whole menu did not start animating promptly on opening: "+kind);
                Check(early.Width<bounds.Width&&early.Height<bounds.Height,"The glass appeared at full size before the menu unfolded: "+kind);
                var dc=NativeMethods.GetDC(IntPtr.Zero);
                uint outside=GetPixel(dc,bounds.Left+bounds.Width/2,bounds.Bottom-8);NativeMethods.ReleaseDC(IntPtr.Zero,dc);
                Check(outside==backgroundColour,"Glass leaked outside the animated menu boundary: "+kind);
                if(kind=="main")OwnWindowCapture.Capture(hwnd).Save(Path.Combine(app.Paths.Root,"menu-opening.png"));
                await Task.Delay(320);
                var full=MenuRegion148(hwnd);NativeMethods.GetWindowRect(hwnd,out bounds);
                Check(!motion.IsRunning&&motion.Progress==1&&full.Width>=bounds.Width&&full.Height>=bounds.Height,"Menu animation did not settle at its usable size: "+kind);
                if(kind=="main")OwnWindowCapture.Capture(hwnd).Save(Path.Combine(app.Paths.Root,"menu-open.png"));
                var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(null);
                try{bar.Menus.Close();}finally{SynchronizationContext.SetSynchronizationContext(context);}
                await Task.Delay(65);var closing=MenuRegion148(hwnd);
                Check(WindowStyler.IsVisible(hwnd)&&motion.IsRunning&&closing.Width<full.Width&&closing.Height<full.Height,"The whole menu did not fold towards the panel on close: "+kind);
                if(kind=="main")OwnWindowCapture.Capture(hwnd).Save(Path.Combine(app.Paths.Root,"menu-closing.png"));
                await Task.Delay(180);Check(!bar.Menus.SurfaceVisible&&!WindowStyler.IsVisible(hwnd)&&!motion.IsRunning,"Close left a surface or frame clock alive: "+kind);
                results.Add(new{kind,opening=early,settled=full,closing,actualGlassClip=true,outsideGlassPixel=outside});
            }
            // Reverse a same-button close; neither a preset pose nor an old completion may win.
            bar.OpenMenuForDiagnostics("main");await Task.Delay(80);bar.Menus.Close();await Task.Delay(35);
            float before=bar.Menus.MotionForDiagnostics!.Progress;bar.OpenMenuForDiagnostics("main");
            Check(Math.Abs(bar.Menus.MotionForDiagnostics!.Progress-before)<.08,"A reversed menu jumped back to its initial size.");
            await Task.Delay(380);Check(bar.Menus.ActiveKind=="main"&&bar.Menus.SurfaceVisible,"A previous close hid the reopened menu.");
            bar.Menus.Close();await Task.Delay(30);bar.OpenMenuForDiagnostics("sound");await Task.Delay(380);
            Check(bar.Menus.ActiveKind=="sound"&&bar.Menus.SurfaceVisible,"A previous close hid a different replacement menu.");
            bar.Menus.Close(immediately:true);
            app.Settings.Update(s=>s.MenuAnimations=false);bar.OpenMenuForDiagnostics("calendar");await Task.Delay(60);
            Check(bar.Menus.MotionForDiagnostics!.Progress==1&&!bar.Menus.MotionForDiagnostics.IsRunning,"Reduced motion did not show the final state immediately.");
            bar.Menus.Close();Check(!bar.Menus.SurfaceVisible,"Reduced motion did not close immediately.");
            checks["wholeMenuMotion"]=results;checks["reversalAndReplacement"]=true;checks["reducedMotion"]=true;
        }
        finally
        {
            bar.Menus.Close(immediately:true);background.Close();bar.IgnoreRealCursorForDiagnostics=false;dock.IgnoreRealCursorForDiagnostics=false;
            app.Dock.HoldFullscreenStateForDiagnostics=false;
            app.Settings.Update(s=>{foreach(var property in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(s,property.GetValue(saved));});
        }
    }
}
