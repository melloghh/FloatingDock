using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Models;
using FloatingDock.Windows.Display;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.Taskbar;
using FloatingDock.Windows.WindowManagement;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window,IntPtr region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region,int x,int y);
    private static RECT NativeRect(IntPtr hwnd)
    { Check(NativeMethods.GetWindowRect(hwnd,out var r),"Could not read own window bounds.");return r; }
    private static void CheckRoundedWindow(IntPtr hwnd)
    {
        var r=CreateRectRgn(0,0,0,0);
        try
        {
            Check(GetWindowRgn(hwnd,r)>1,"The popup has no rounded native clipping region.");
            var b=NativeRect(hwnd);
            Check(!PtInRegion(r,0,0)&&PtInRegion(r,b.Width/2,b.Height/2),"Rounded corners include a square edge or clip the content.");
        }
        finally { DeleteObject(r); }
    }
    private static async Task CheckUi113Async(DockApp app,Dictionary<string,object> checks)
    {
        var dock=app.Dock.PrimaryWindowForDiagnostics!;var bar=app.TopBar!.Primary!;
        var original=app.Settings.Current.Clone();
        dock.IgnoreRealCursorForDiagnostics=true;bar.IgnoreRealCursorForDiagnostics=true;
        app.Dock.HoldFullscreenStateForDiagnostics=true;app.Dock.HoldWindowListForDiagnostics=true;
        await app.PrepareLaunchpadAsync();dock.SetFullscreenHidden(false);
        var edges=new List<object>();
        Window? probe=null,second=null;
        IntPtr inputLayout=IntPtr.Zero;
        try
        {
            app.Pinned.Add(new AppRef { Name="Explorer",ExePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe") });
            app.Settings.Update(s=> { s.AutoHide=false;s.Edge=DockEdge.Bottom;s.BottomOffset=24;s.TopBarEnabled=true;s.TopBarAutoHide=false; });
            await Task.Delay(400);
            var barRect=NativeRect(bar.Handle);
            Check(barRect.Left>bar.Monitor.Bounds.Left&&barRect.Right<bar.Monitor.Bounds.Right&&barRect.Top>bar.Monitor.Bounds.Top,"The menu bar has no screen margins.");
            CheckRoundedWindow(bar.Handle);
            OwnWindowCapture.Capture(bar.Handle).Save(Path.Combine(app.Paths.Root,"rounded-menu-bar.png"));
            foreach(var edge in new[]{DockEdge.Bottom,DockEdge.Left,DockEdge.Right})
            {
                app.Settings.Update(s=>s.Edge=edge);await Task.Delay(350);
                var plate=dock.PlateScreenForDiagnostics;var mon=dock.Monitor;
                Check(plate.Left>=mon.Bounds.Left&&plate.Right<=mon.Bounds.Right&&plate.Top>=mon.Bounds.Top&&plate.Bottom<=mon.Bounds.Bottom,"The dock is outside its monitor: "+edge);
                Check(edge==DockEdge.Bottom?plate.Width>plate.Height:plate.Height>plate.Width,"Incorrect dock orientation: "+edge);
                int gap=edge switch { DockEdge.Left=>plate.Left-mon.Bounds.Left,DockEdge.Right=>mon.Bounds.Right-plate.Right,_=>mon.Bounds.Bottom-plate.Bottom };
                int taskbarGap=app.Settings.Current.TaskbarMode==TaskbarMode.Leave&&!app.Settings.Current.ReserveWorkArea ? edge switch { DockEdge.Left=>mon.WorkArea.Left-mon.Bounds.Left,DockEdge.Right=>mon.Bounds.Right-mon.WorkArea.Right,_=>mon.Bounds.Bottom-mon.WorkArea.Bottom } : 0;
                Check(Math.Abs(gap-taskbarGap-24*mon.Scale)<3,"The screen-edge gap changed: "+edge+" gap="+gap);
                OwnWindowCapture.Capture(dock.Handle).Save(Path.Combine(app.Paths.Root,"dock-"+edge.ToString().ToLowerInvariant()+".png"));
                CheckNativeMenu(app,checks);
                dock.IgnoreRealCursorForDiagnostics=false;
                var point=dock.LaunchpadPointForDiagnostics();var packed=new IntPtr((point.Y<<16)|(point.X&0xffff));
                SendMessage(dock.Handle,0x200,IntPtr.Zero,packed);Check(dock.IsHovering,"Vertical pointer mapping missed the icon: "+edge);
                SendMessage(dock.Handle,0x201,new IntPtr(1),packed);SendMessage(dock.Handle,0x202,IntPtr.Zero,packed);
                await Task.Delay(450);Check(app.LaunchpadWin!.IsOpen,"A native dock click did not open Launchpad: "+edge);
                if(edge==DockEdge.Bottom)OwnWindowCapture.Capture(app.LaunchpadWin.Handle).Save(Path.Combine(app.Paths.Root,"launchpad-glass.png"));
                app.LaunchpadWin.Hide();dock.IgnoreRealCursorForDiagnostics=true;
                await Task.Delay(600);
                dock.SetFullscreenHidden(true);await Task.Delay(90);
                Check(dock.HideAmount is >0 and <1,"The dock fullscreen hide snapped: "+edge);
                await Task.Delay(650);Check(dock.HideAmount==1,"The dock did not finish hiding: "+edge);
                var strip=NativeRect(dock.Handle);
                Check(edge==DockEdge.Bottom?strip.Height==2:strip.Width==2,"The reveal strip is on the wrong axis: "+edge);
                dock.Reveal();await Task.Delay(90);Check(dock.HideAmount is >0 and <1,"The edge reveal snapped: "+edge);
                dock.SetFullscreenHidden(false);await Task.Delay(350);Check(dock.HideAmount==0,"The dock did not restore: "+edge);
                edges.Add(new { edge=edge.ToString(),gap,plate,strip,nativeMouseClick=true,animatedHideAndReveal=true,contextMenuClear=true });
            }
            checks["dockEdges"]=edges;
            app.Settings.Update(s=>s.Edge=DockEdge.Left);await Task.Delay(350);
            probe=new Window { Title="First preview",Content=new Grid { Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,34,187,153)) } };
            second=new Window { Title="Second preview",Content=new Grid { Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,34,187,153)) } };
            var hwnd=WindowSetup.Hwnd(probe);var hwnd2=WindowSetup.Hwnd(second);
            WindowStyler.SetBounds(hwnd,500,200,440,280,topmost:false);probe.AppWindow.Show(false);
            WindowStyler.SetBounds(hwnd2,550,240,400,260,topmost:false);second.AppWindow.Show(false);
            bool closed=false;second.Closed+=(_,_)=>closed=true;
            WindowInfo Snapshot(IntPtr h,string title)=>new(h.ToInt64(),Environment.ProcessId,Environment.ProcessPath,null,title,null,false);
            var entry=new DockEntry { Kind=DockEntryKind.App,Key="preview113",Name="Preview",Windows=new[]{Snapshot(hwnd,probe.Title)} };
            var preview=dock.PreviewForDiagnostics;var anchor=dock.PlateScreenForDiagnostics;
            preview.Track(entry,anchor,diagnostic:true,edge:DockEdge.Left);
            await Task.Delay(450);Check(!preview.Visible,"The preview appeared before the extended delay.");
            await Task.Delay(450);Check(preview.Visible&&preview.ThumbnailCount==1,"The delayed preview did not open.");
            CheckRoundedWindow(preview.Handle);
            Check(NativeRect(preview.Handle).Left>=anchor.Right,"The left dock preview overlaps the dock.");
            entry=new DockEntry { Kind=DockEntryKind.App,Key="preview113",Name="Preview",Windows=new[]{Snapshot(hwnd,probe.Title),Snapshot(hwnd2,second.Title)} };
            preview.Track(entry,anchor,diagnostic:true,edge:DockEdge.Left);await Task.Delay(250);
            Check(preview.ThumbnailCount==2&&preview.CloseButtons.Count==2,"The preview missed a newly opened second window or close buttons.");
            int GreenRows()
            {
                var frame=OwnWindowCapture.Capture(preview.Handle);int count=0;
                for(int y=(int)(40*dock.Monitor.Scale);y<Math.Min(frame.Height,(int)(186*dock.Monitor.Scale));y++)
                { int p=(y*frame.Width+frame.Width/2)*4;if(Math.Abs(frame.Pixels[p+2]-34)<5&&Math.Abs(frame.Pixels[p+1]-187)<5&&Math.Abs(frame.Pixels[p]-153)<5)count++; }
                return count;
            }
            int beforeResize=GreenRows();
            probe.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(640,160));await Task.Delay(350);
            int afterResize=GreenRows();Check(beforeResize>60&&afterResize>20&&afterResize<beforeResize*.75,"The DWM thumbnail did not adjust its aspect ratio after source resize.");
            OwnWindowCapture.Capture(preview.Handle).Save(Path.Combine(app.Paths.Root,"preview-rounded-close.png"));
            probe.Close();probe=null;await Task.Delay(350);
            Check(preview.ThumbnailCount==1&&preview.Visible,"Closing a source window left a stale or broken preview.");
            var peer=new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(preview.CloseButtons.Single());
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(450);Check(closed&&!preview.Visible&&preview.ThumbnailCount==0,"The close button did not close the source and remove the preview.");second=null;
            checks["previewLifetime"]=new { delayMs=Dock.DockWindowPreview.HoverDelayMs,roundedNativeRegion=true,clientAreaOnly=true,addedWindowDetected=true,sourceResizeHandled=true,beforeResizeGreenRows=beforeResize,afterResizeGreenRows=afterResize,closedSourceRemoved=true,closeButtonWorked=true };
            probe=new Window { Title="Keyboard layout polling probe",Content=new TextBox { Text="Layout probe" } };hwnd=WindowSetup.Hwnd(probe);probe.AppWindow.Show(false);probe.Activate();
            uint thread=NativeMethods.GetWindowThreadProcessId(hwnd,out _);
            inputLayout=FloatingDock.Windows.Integration.InputLanguages.GetKeyboardLayout(thread);
            bar.OpenMenuForDiagnostics("language",hwnd);await Task.Delay(100);bar.Menus.Close();await Task.Delay(220);
            WindowStyler.BringToFront(hwnd);await Task.Delay(100);
            var alternate=FloatingDock.Windows.Integration.InputLanguages.Installed().FirstOrDefault(l=>l.Handle!=inputLayout);
            Check(alternate is not null,"No second installed keyboard layout is available for the polling check.");
            var timer=Stopwatch.StartNew();FloatingDock.Windows.Integration.InputLanguages.Request(hwnd,alternate!.Handle);
            while(bar.LanguageCode!=alternate.Tag&&timer.ElapsedMilliseconds<500)await Task.Delay(15);
            Check(FloatingDock.Windows.Integration.InputLanguages.GetKeyboardLayout(thread)==alternate.Handle&&bar.LanguageCode==alternate.Tag,"The top bar did not follow a keyboard layout change outside its menu.");
            checks["keyboardLayoutRefresh"]=new { measuredMs=timer.Elapsed.TotalMilliseconds,intervalMs=150,menuWasClosed=true,ownProbeOnly=true };
            FloatingDock.Windows.Integration.InputLanguages.Request(hwnd,inputLayout);await Task.Delay(150);
            foreach(var edge in new[]{DockEdge.Left,DockEdge.Right})
            {
                var before=MonitorHelper.Describe(dock.Monitor.Handle)!;
                using(var reservation=new AppBarReservation())
                {
                    Check(reservation.Apply(new[]{new AppBarRequest(before,40,Edge:edge)}).AllReserved,"Side work-area reservation failed.");await Task.Delay(250);
                    var reserved=MonitorHelper.Describe(before.Handle)!;
                    Check(edge==DockEdge.Left?reserved.WorkArea.Left>=before.WorkArea.Left+40:reserved.WorkArea.Right<=before.WorkArea.Right-40,"Side reservation reduced the wrong dimension.");
                }
                await Task.Delay(250);Check(MonitorHelper.Describe(before.Handle)!.WorkArea==before.WorkArea,"Side reservation did not restore the work area.");
            }
            checks["sideAppBar"]=new { leftAndRightReserved=true,workAreaRestored=true };
        }
        finally
        {
            dock.PreviewForDiagnostics.Hide();app.LaunchpadWin?.Hide();
            if(probe is not null&&inputLayout!=IntPtr.Zero)FloatingDock.Windows.Integration.InputLanguages.Request(WindowSetup.Hwnd(probe),inputLayout);
            probe?.Close();second?.Close();dock.SetFullscreenHidden(false);
            app.Settings.Update(s=> { s.Edge=original.Edge;s.BottomOffset=original.BottomOffset;s.AutoHide=original.AutoHide;s.TopBarEnabled=original.TopBarEnabled;s.TopBarAutoHide=original.TopBarAutoHide; });
            dock.IgnoreRealCursorForDiagnostics=false;bar.IgnoreRealCursorForDiagnostics=false;
            app.Dock.HoldFullscreenStateForDiagnostics=false;app.Dock.HoldWindowListForDiagnostics=false;
        }
    }
}
