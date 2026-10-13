using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.RunningApps;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    private static async Task CheckUi145Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;var mon=bar.Monitor;
        NativeMethods.GetCursorPos(out var cursor);
        using var fixture=new MisplacedTrayMenu145();
        async Task Wait(Func<bool> condition,string message)
        {var clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<3500)await Task.Delay(8);Check(condition(),message);}
        IntPtr ActualMenu(string executable)
        {
            IntPtr found=IntPtr.Zero;
            NativeMethods.EnumWindows((menu,_)=>
            {
                if(NativeMethods.IsWindowVisible(menu)&&NativeMethods.GetClass(menu)=="#32768")
                {NativeMethods.GetWindowThreadProcessId(menu,out uint pid);if(Path.GetFileName(WindowIdentityResolver.GetExePath(pid))?.Equals(executable,StringComparison.OrdinalIgnoreCase)==true){found=menu;return false;}}
                return true;
            },IntPtr.Zero);return found;
        }
        RECT Verify(IntPtr menu,POINT anchor)
        {
            Check(NativeMethods.GetWindowRect(menu,out var rect),"The actual native menu has no bounds.");
            int wantedX=Math.Clamp(anchor.X,mon.WorkArea.Left+4,Math.Max(mon.WorkArea.Left+4,mon.WorkArea.Right-4-rect.Width));
            Check(Math.Abs(rect.Left-wantedX)<6&&Math.Abs(rect.Top-anchor.Y-8*mon.Scale)<6,"Actual app menu is away from the tray icon: "+rect.Left+","+rect.Top);
            return rect;
        }
        try
        {
            app.Dock.HoldFullscreenStateForDiagnostics=true;
            app.Settings.Update(s=>{s.TopBarEnabled=true;s.TopBarAutoHide=false;s.TilingEnabled=false;});await Task.Delay(200);
            for(int cycle=0;cycle<2;cycle++)
            {
                await bar.OpenTrayForDiagnostics();await Task.Delay(220);
                var item=bar.Menus.TrayItems.SingleOrDefault(i=>i.Name.Contains("FloatingDock misplaced menu 145"));Check(item is not null,"Fixture notification icon was not discovered.");
                Check(!string.IsNullOrWhiteSpace(item!.Executable),"Live tray metadata omitted the application executable.");
                bar.Menus.Close(immediately:true);
                var anchor=new POINT {X=mon.Bounds.Right-150-cycle*200,Y=mon.Bounds.Top+120};
                SetCursorPos(anchor.X,anchor.Y);
                Check(await app.ActivateTrayItemAsync(item,true,anchor),"Fixture context callback failed.");
                await Wait(()=>ActualMenu(Path.GetFileName(Environment.ProcessPath)!)!=IntPtr.Zero,"Fixture app did not display its native menu.");
                var menu=ActualMenu(Path.GetFileName(Environment.ProcessPath)!);var rect=Verify(menu,anchor);
                checks["actualFixtureMenu"+cycle]=new {rect.Left,rect.Top,rect.Right,rect.Bottom};
                // Click a relocated command, not just inspect a notification rect.
                SetCursorPos(rect.Left+30,rect.Top+18);
                var click=new[]{new INPUT {U=new(){mi=new(){dwFlags=2}}},new INPUT {U=new(){mi=new(){dwFlags=4}}}};
                NativeMethods.SendInput(2,click,Marshal.SizeOf<INPUT>());
                await Wait(()=>fixture.Commands==cycle+1,"Relocated menu command did not receive the actual mouse click.");
                await Wait(()=>!NativeMethods.IsWindowVisible(menu),"Fixture native menu did not close after its command.");
            }
            checks["relocatedMenuCommands"]=fixture.Commands;
            if(Environment.GetCommandLineArgs().Contains("--test-wallpaper145"))
            {
                await bar.OpenTrayForDiagnostics();await Task.Delay(220);
                var item=bar.Menus.TrayItems.FirstOrDefault(i=>Path.GetFileName(i.Executable)?.Equals("wallpaper64.exe",StringComparison.OrdinalIgnoreCase)==true||i.Name.Contains("Wallpaper Engine",StringComparison.OrdinalIgnoreCase));
                Check(item is not null,"Wallpaper Engine's existing notification icon is unavailable.");bar.Menus.Close(immediately:true);
                var anchor=new POINT {X=mon.Bounds.Right-180,Y=mon.Bounds.Top+110};SetCursorPos(anchor.X,anchor.Y);
                Check(await app.ActivateTrayItemAsync(item!,true,anchor),"Wallpaper Engine context callback failed.");
                var executable=Path.GetFileName(item!.Executable)!;
                await Wait(()=>ActualMenu(executable)!=IntPtr.Zero,"Wallpaper Engine did not display its native menu.");
                var menu=ActualMenu(executable);var rect=Verify(menu,anchor);
                checks["actualWallpaperEngineMenu"]=new {rect.Left,rect.Top,rect.Right,rect.Bottom};
                NativeMethods.GetWindowThreadProcessId(menu,out uint pid);
                var owner=NativeMethods.GetWindow(menu,4);
                if(owner!=IntPtr.Zero)NativeMethods.PostMessage(owner,0x1F,IntPtr.Zero,IntPtr.Zero);
                // Escape is injected only while the inspected app's menu owns focus.
                NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(),out uint foreground);
                if(foreground==pid)
                {
                    var escape=new[]{new INPUT {type=NativeMethods.INPUT_KEYBOARD,U=new(){ki=new(){wVk=0x1B}}},new INPUT {type=NativeMethods.INPUT_KEYBOARD,U=new(){ki=new(){wVk=0x1B,dwFlags=NativeMethods.KEYEVENTF_KEYUP}}}};
                    NativeMethods.SendInput(2,escape,Marshal.SizeOf<INPUT>());
                }
                await Wait(()=>!NativeMethods.IsWindowVisible(menu),"Wallpaper Engine menu did not dismiss without executing a command.");
            }
        }
        finally
        {
            bar.Menus.Close(immediately:true);SetCursorPos(cursor.X,cursor.Y);
            app.Settings.Update(s=>{foreach(var p in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
            app.Dock.HoldFullscreenStateForDiagnostics=false;
        }
    }
    private sealed class MisplacedTrayMenu145:IDisposable
    {
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG message,IntPtr window,uint first,uint last);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(in MSG message);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread,uint message,IntPtr w,IntPtr l);
        private readonly Thread _thread;private readonly ManualResetEventSlim _ready=new();private IntPtr _owner;private uint _id;private int _commands;
        internal int Commands=>Volatile.Read(ref _commands);
        internal MisplacedTrayMenu145()
        {
            _thread=new Thread(()=>
            {
                _id=NativeMethods.GetCurrentThreadId();
                using var host=new MessageWindow();using var tray=new TrayIcon(host,"FloatingDock misplaced menu 145",Environment.ProcessPath);_owner=host.Handle;
                tray.MenuProvider=()=>
                {
                    // Reproduce apps which ignore Explorer's corrected icon rect.
                    int command=NativeMenu.Show(host.Handle,new[]{new MenuItemSpec(145,"Fixture command"),new MenuItemSpec(146,"Second fixture command")},0,0,bottomAlign:false);
                    if(command==145)Interlocked.Increment(ref _commands);
                    return Array.Empty<TrayMenuItem>();
                };
                _ready.Set();while(GetMessage(out var message,IntPtr.Zero,0,0)>0)DispatchMessage(message);
            }){IsBackground=true};_thread.SetApartmentState(ApartmentState.STA);_thread.Start();Check(_ready.Wait(2000),"Native tray/menu fixture did not start.");
        }
        public void Dispose(){NativeMethods.PostMessage(_owner,0x1F,IntPtr.Zero,IntPtr.Zero);PostThreadMessage(_id,0x12,IntPtr.Zero,IntPtr.Zero);_thread.Join(2000);_ready.Dispose();}
    }
}
