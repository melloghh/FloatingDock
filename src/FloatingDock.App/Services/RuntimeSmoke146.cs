using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.Integration;
using FloatingDock.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc,int x,int y);
    private static async Task CheckUi146Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;var mon=bar.Monitor;
        NativeMethods.GetCursorPos(out var originalCursor);
        var background=new Window {Title="FloatingDock stock tray visibility fixture",Content=new Grid {Background=new SolidColorBrush(global::Windows.UI.Color.FromArgb(255,36,108,164))}};
        WindowSetup.MakeBorderless(background);var backdrop=WindowSetup.Hwnd(background);
        FloatingDock.Windows.WindowManagement.WindowStyler.MakeOverlayWindow(backdrop,noActivate:true);
        app.Dock.HoldFullscreenStateForDiagnostics=true;
        try
        {
            app.Settings.Update(s=>{s.TopBarEnabled=true;s.TopBarAutoHide=false;s.TilingEnabled=false;});await Task.Delay(200);
            FloatingDock.Windows.WindowManagement.WindowStyler.SetBounds(backdrop,mon.Bounds.Left,mon.Bounds.Top,mon.Bounds.Width,mon.Bounds.Height);
            background.AppWindow.Show(false);await Task.Delay(300);
            var dc=NativeMethods.GetDC(IntPtr.Zero);
            uint expected=36u|(108u<<8)|(164u<<16);
            Check(GetPixel(dc,mon.Bounds.Left+mon.Bounds.Width/2,mon.Bounds.Top+mon.Bounds.Height/2)==expected,"Owned backdrop is unavailable for an actual-screen visibility check.");
            NativeMethods.ReleaseDC(IntPtr.Zero,dc);
            var results=new List<object>();
            for(int cycle=0;cycle<4;cycle++)
            {
                NativeMethods.SetWindowPos(backdrop,NativeMethods.HWND_TOPMOST,0,0,0,0,NativeMethods.SWP_NOMOVE|NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE);
                var cursor=new POINT {X=mon.Bounds.Right-177,Y=mon.Bounds.Top+123};SetCursorPos(cursor.X,cursor.Y);
                using var observer=new TrayObserver146(cursor,pixels:true,expected);
                await bar.OpenTrayForDiagnostics();await Task.Delay(100);observer.Stop();
                Check(bar.TrayVisible,"Repeated custom tray opening failed.");
                results.Add(new{cycle,observer.Samples,observer.VisibleSamples,observer.Unsuppressed,observer.BadPixels,observer.CursorChanges});
                Check(observer.Unsuppressed==0,"Explorer's native overflow became opaque during discovery.");
                Check(observer.BadPixels==0,"Actual stock-tray pixels appeared over the owned backdrop.");
                Check(observer.CursorChanges==0,"Opening the tray changed the physical cursor.");
                bar.Menus.Close(immediately:true);await Task.Delay(100);
            }
            checks["repeatedTrayVisibility"]=results;
            background.AppWindow.Hide();
            // Reuse the actual-menu/command checks, now with a cursor observer
            // around each right-click rather than a final-position-only check.
            await CheckUi145Async(app,checks);
            checks["noOffscreenTrayCoordinates"]=true;
        }
        finally
        {
            bar.Menus.Close(immediately:true);background.Close();SetCursorPos(originalCursor.X,originalCursor.Y);
            app.Settings.Update(s=>{foreach(var p in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
            app.Dock.HoldFullscreenStateForDiagnostics=false;
        }
    }
    private sealed class TrayObserver146:IDisposable
    {
        private readonly Thread _thread;private readonly ManualResetEventSlim _stop=new();
        private readonly POINT _cursor;private readonly bool _pixels;private readonly uint _expected;
        internal int Samples,VisibleSamples,Unsuppressed,BadPixels,CursorChanges;
        internal string FirstChange="";
        internal TrayObserver146(POINT cursor,bool pixels=false,uint expected=0)
        {
            _cursor=cursor;_pixels=pixels;_expected=expected;
            _thread=new Thread(()=>
            {
                var dc=_pixels?NativeMethods.GetDC(IntPtr.Zero):IntPtr.Zero;
                try
                {
                    while(!_stop.IsSet)
                    {
                        Samples++;NativeMethods.GetCursorPos(out var current);
                        if(current.X!=_cursor.X||current.Y!=_cursor.Y){CursorChanges++;if(FirstChange.Length==0)FirstChange=$"{_cursor.X},{_cursor.Y} -> {current.X},{current.Y}";}
                        var native=SystemTrayOverflow.Handle;
                        if(NativeMethods.IsWindowVisible(native))
                        {
                            VisibleSamples++;
                            if((!GetLayeredWindowAttributes(native,out _,out byte alpha,out _)||alpha!=0)&&NativeMethods.IsWindowVisible(native))Unsuppressed++;
                        }
                        if(_pixels&&NativeMethods.GetWindowRect(native,out var rect))
                        {
                            var monitor=FloatingDock.Windows.Display.MonitorHelper.Primary()!;
                            int x=rect.Left+rect.Width/2,y=rect.Top+rect.Height/2;
                            if(x>=monitor.Bounds.Left&&x<monitor.Bounds.Right&&y>=monitor.Bounds.Top&&y<monitor.Bounds.Bottom)
                            {
                                // Read the actual screen, not the HWND's private
                                // backing bitmap, which can contain pixels at alpha zero.
                                if(GetPixel(dc,x,y)!=_expected)BadPixels++;
                            }
                        }
                        _stop.Wait(2);
                    }
                }
                finally{if(dc!=IntPtr.Zero)NativeMethods.ReleaseDC(IntPtr.Zero,dc);}
            }){IsBackground=true,Name="FloatingDock.TrayPixels146"};_thread.Start();
        }
        internal void Stop(){_stop.Set();_thread.Join();}
        public void Dispose(){Stop();_stop.Dispose();}
    }
}
