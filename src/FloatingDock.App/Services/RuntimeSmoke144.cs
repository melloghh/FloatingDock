using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Core.Configuration;
using FloatingDock.Core.Tiling;
using FloatingDock.Windows.Integration;
using FloatingDock.Windows.Native;
using FloatingDock.Windows.WindowManagement;
using Microsoft.UI.Xaml.Controls;

namespace FloatingDock.UI.Services;
internal static partial class RuntimeSmoke
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier {public uint Size;public IntPtr Window;public uint Id;public Guid Guid;}
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(in IconIdentifier id,out RECT rect);
    private static async Task CheckUi144Async(DockApp app,Dictionary<string,object> checks)
    {
        var saved=app.Settings.Current.Clone();var bar=app.TopBar!.Primary!;var mon=bar.Monitor;
        NativeMethods.GetCursorPos(out var cursor);
        using var input=new LayoutWindow144();
        using var trayHost=new MessageWindow();using var tray=new TrayIcon(trayHost,"FloatingDock position fixture 144",Environment.ProcessPath);
        int contexts=0;RECT callbackRect=default;
        tray.MenuProvider=()=> {contexts++;var identity=new IconIdentifier {Size=(uint)Marshal.SizeOf<IconIdentifier>(),Window=trayHost.Handle,Id=1};Shell_NotifyIconGetRect(identity,out callbackRect);return Array.Empty<TrayMenuItem>();};
        async Task Wait(Func<bool> condition,string message,int timeout=3500)
        {var time=Stopwatch.StartNew();while(!condition()&&time.ElapsedMilliseconds<timeout)await Task.Delay(8);Check(condition(),message);}
        try
        {
            app.Dock.HoldFullscreenStateForDiagnostics=true;
            app.Settings.Update(s=>{s.TilingEnabled=false;s.TopBarEnabled=true;s.TopBarAutoHide=false;s.MenuAnimations=true;s.MenuAnimationMs=260;});await Task.Delay(180);
            // Production click path: first display must not wait for Start export,
            // forty-eight icon requests or invisible GPU warm-up.
            Check(app.LaunchpadWin is null,"Launchpad was allocated before first use.");
            var clock=Stopwatch.StartNew();app.ToggleLaunchpad();long firstShow=clock.ElapsedMilliseconds;
            Check(app.LaunchpadWin!.IsOpen&&NativeMethods.IsWindowVisible(app.LaunchpadWin.Handle)&&firstShow<500,"Cold Launchpad click waited for preparation.");
            await Task.Delay(300);Check(app.LaunchpadWin.ContentOpacity>.9,"Launchpad did not complete its first visible transition.");
            app.LaunchpadWin.Hide();await Task.Delay(350);checks["coldLaunchpad"]=new{firstShowMs=firstShow,awaitedIconBatch=false};
            input.Activate();await Task.Delay(100);
            // Foreground locking can reject SetForegroundWindow for a background
            // test process. Click only a verified, owned edit before issuing keys.
            NativeMethods.SetWindowPos(input.Handle,NativeMethods.HWND_TOPMOST,0,0,0,0,NativeMethods.SWP_NOMOVE|NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE);
            NativeMethods.GetWindowRect(input.Edit,out var editRect);var editPoint=new POINT {X=editRect.Left+20,Y=editRect.Top+20};
            Check(WindowFromPoint(editPoint)==input.Edit,"Language fixture edit is covered; cannot safely issue input.");
            SetCursorPos(editPoint.X,editPoint.Y);
            var editClick=new[]{new INPUT {U=new(){mi=new(){dwFlags=2}}},new INPUT {U=new(){mi=new(){dwFlags=4}}}};
            NativeMethods.SendInput(2,editClick,Marshal.SizeOf<INPUT>());await Task.Delay(60);
            Check(NativeMethods.GetForegroundWindow()==input.Handle,"Language fixture did not receive foreground input.");
            var layouts=InputLanguages.Installed();Check(layouts.Count>1,"Language check requires two installed layouts.");
            uint thread=NativeMethods.GetWindowThreadProcessId(input.Handle,out _);var original=InputLanguages.GetKeyboardLayout(thread);
            Check(InputLanguages.InputSite(input.Handle)==input.Edit,"Focused edit input site was not resolved.");
            INPUT Key(ushort key,bool up=false)=>new(){type=NativeMethods.INPUT_KEYBOARD,U=new(){ki=new(){wVk=key,dwFlags=up?NativeMethods.KEYEVENTF_KEYUP:0}}};
            var keys=new[]{Key(0x5B),Key(0x20),Key(0x20,true),Key(0x5B,true)};
            Check(NativeMethods.SendInput((uint)keys.Length,keys,Marshal.SizeOf<INPUT>())==keys.Length,"Could not switch the fixture with Win+Space.");
            await Wait(()=>InputLanguages.GetKeyboardLayout(thread)!=original,"Win+Space did not change the fixture input language.");
            string expected=layouts.First(l=>l.Handle==InputLanguages.GetKeyboardLayout(thread)).Tag;
            clock.Restart();await Wait(()=>bar.LanguageCode==expected,"Top bar did not reflect the keyboard shortcut.",250);
            checks["hotkeyLanguageLatencyMs"]=clock.ElapsedMilliseconds;
            for(int i=0;i<2;i++)
            {
                var before=InputLanguages.GetKeyboardLayout(thread);int selected=Enumerable.Range(0,layouts.Count).First(n=>layouts[n].Handle!=before);
                bar.OpenMenuForDiagnostics("language",input.Handle);await Task.Delay(320);
                var button=bar.Menus.LanguageButtons[selected];var point=button.TransformToVisual(button.XamlRoot.Content).TransformPoint(new(button.ActualWidth/2,button.ActualHeight/2));
                NativeMethods.GetWindowRect(bar.Menus.PopupHandle,out var bounds);double scale=button.XamlRoot.RasterizationScale;
                SetCursorPos(bounds.Left+(int)Math.Round(point.X*scale),bounds.Top+(int)Math.Round(point.Y*scale));
                var mouse=new[]{new INPUT {U=new(){mi=new(){dwFlags=2}}},new INPUT {U=new(){mi=new(){dwFlags=4}}}};
                NativeMethods.SendInput(2,mouse,Marshal.SizeOf<INPUT>());
                await Wait(()=>InputLanguages.GetKeyboardLayout(thread)==layouts[selected].Handle,"Mouse language row did not switch the focused edit.");
                await Wait(()=>bar.LanguageCode==layouts[selected].Tag,"Mouse-selected layout did not reach the bar.",250);
                Check(!bar.Menus.SurfaceVisible,"Language dismissal retained an active surface.");
            }
            checks["mouseLanguageCycles"]=2;
            InputLanguages.Request(input.Handle,original);
            int popupCloak=0;var nativePopup=SystemTrayOverflow.Handle;
            if(nativePopup!=IntPtr.Zero)NativeMethods.DwmGetWindowAttribute(nativePopup,14,out popupCloak,4);
            using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
            {
                var busyShell=app.Worker.Run(()=>{entered.Set();release.Wait(5000);});
                try
                {
                    Check(await Task.Run(()=>entered.Wait(2000)),"Busy shell fixture did not start.");
                    var read=bar.OpenTrayForDiagnostics();
                    Check(await Task.WhenAny(read,Task.Delay(1500))==read,"Tray opening was queued behind shell icon/catalog work.");
                    await read;
                }
                finally {release.Set();await busyShell;}
            }
            await Task.Delay(320);checks["trayIndependentOfBusyShell"]=true;
            var fixture=bar.Menus.TrayItems.SingleOrDefault(i=>i.Name.Contains("position fixture 144"));Check(fixture is not null,"Live tray fixture was not discovered.");
            Check(!SystemTrayOverflow.Visible,"Custom tray left the native overflow visible.");
            bar.Menus.Close(immediately:true);
            var anchor=new POINT {X=mon.Bounds.Right-190,Y=mon.Bounds.Top+100};
            SetCursorPos(anchor.X,anchor.Y);
            Check(await app.ActivateTrayItemAsync(fixture!,true,anchor),"Context activation failed.");await Wait(()=>contexts==1,"Context notification did not reach the app.");
            checks["trayContextOrigin"]=new{callbackRect.Left,callbackRect.Top,callbackRect.Right,callbackRect.Bottom,anchor.X,anchor.Y};
            Check(Math.Abs(callbackRect.Left+callbackRect.Width/2-anchor.X)<32&&Math.Abs(callbackRect.Top+callbackRect.Height/2-anchor.Y)<32,"Tray callback still reported a taskbar/off-screen source rectangle.");
            NativeMethods.DwmGetWindowAttribute(SystemTrayOverflow.Handle,14,out int afterCloak,4);
            Check((afterCloak&1)==(popupCloak&1),"Tray operation leaked an Explorer app cloak.");
            for(int i=0;i<3;i++)
            {bar.OpenMenuForDiagnostics("calendar");await Task.Delay(80);bar.Menus.Close();await Task.Delay(40);bar.OpenMenuForDiagnostics("sound");await Task.Delay(340);Check(bar.Menus.ActiveKind=="sound"&&bar.Menus.SurfaceVisible,"Interrupted menu transition hid the replacement.");bar.Menus.Close(immediately:true);}
            checks["interruptedMenus"]=3;
            await CheckReflow144Async(app,checks);
        }
        finally
        {
            bar.Menus.Close(immediately:true);app.LaunchpadWin?.Hide();SetCursorPos(cursor.X,cursor.Y);
            app.Settings.Update(s=>{foreach(var p in typeof(DockSettings).GetProperties().Where(p=>p.CanWrite))p.SetValue(s,p.GetValue(saved));});
            app.Dock.HoldFullscreenStateForDiagnostics=false;
        }
    }
    private static async Task CheckReflow144Async(DockApp app,Dictionary<string,object> checks)
    {
        var first=new Microsoft.UI.Xaml.Window {Title="FloatingDock reflow 144 A",Content=new Grid()};
        var second=new Microsoft.UI.Xaml.Window {Title="FloatingDock reflow 144 B",Content=new Grid()};
        var a=FloatingDock.UI.Views.WindowSetup.Hwnd(first);var b=FloatingDock.UI.Views.WindowSetup.Hwnd(second);bool closed=false;
        var ids=new HashSet<long>{a.ToInt64(),b.ToInt64()};
        async Task Wait(Func<bool> condition,string message)
        {var clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<5000)await Task.Delay(8);Check(condition(),message);}
        try
        {
            first.Activate();second.Activate();app.Running.IncludeOwnWindow(a);app.Running.IncludeOwnWindow(b);
            app.Tiling.DiagnosticWindows=ids;app.Tiling.SkipHotkeyRegistrationForDiagnostics=true;
            app.Settings.Update(s=>{s.TilingEnabled=true;s.TilingAnimationMs=320;s.TilingAnimationFps=144;s.TilingExcludedApps="";});app.Tiling.Apply(app.Settings.Current);
            await Wait(()=>app.Tiling.Session?.ManagedCount==2&&!app.Tiling.Animating,"Reflow fixtures did not settle.");
            var session=app.Tiling.Session!;session.Reconcile(app.Running.Snapshot(),a.ToInt64());
            session.Resize(TileDirection.Right);app.Tiling.AnimateLayout();await Task.Delay(80);
            Check(app.Tiling.PresentationsForDiagnostics.ContainsKey(a.ToInt64()),"Resize did not use a compositor representation.");
            var before=app.Tiling.PresentationsForDiagnostics[a.ToInt64()].Bounds;
            session.Resize(TileDirection.Right);app.Tiling.AnimateLayout();
            var after=app.Tiling.PresentationsForDiagnostics[a.ToInt64()].Bounds;
            Check(before==after,"Retarget jumped to the native final placement.");
            var frames=new HashSet<TileRect>();
            while(app.Tiling.Animating){if(app.Tiling.PresentationsForDiagnostics.TryGetValue(a.ToInt64(),out var p))frames.Add(p.Bounds);await Task.Delay(8);}
            Check(frames.Count>10,"Retargeted resize did not produce smooth intermediate sizes.");
            second.Close();closed=true;app.Running.ExcludeOwnWindow(b);
            await Wait(()=>app.Tiling.Animating,"Closing a tile did not animate the remaining layout.");
            var closeFrames=new HashSet<TileRect>();
            while(app.Tiling.Animating){if(app.Tiling.PresentationsForDiagnostics.TryGetValue(a.ToInt64(),out var p))closeFrames.Add(p.Bounds);await Task.Delay(8);}
            await Wait(()=>session.Targets().Count==1,"Closed tile retained space.");
            Check(closeFrames.Count>10,"Close reflow snapped to its final rectangle.");
            NativeMethods.GetWindowRect(a,out var actual);var target=session.Targets()[a.ToInt64()];
            Check(Math.Abs(actual.Width-target.Width)<=2&&Math.Abs(actual.Left-target.X)<=2,"Closing reflow did not reach the final native tile.");
            Check((NativeMethods.GetExStyle(a)&0x80000)==0,"Reflow leaked an opacity lease.");
            checks["tilingRetargetAndClose"]=new{retargetFrames=frames.Count,closeReflowFrames=closeFrames.Count,finite=true};
        }
        finally
        {
            app.Settings.Update(s=>s.TilingEnabled=false);app.Tiling.Apply(app.Settings.Current);app.Tiling.DiagnosticWindows=null;
            app.Running.ExcludeOwnWindow(a);app.Running.ExcludeOwnWindow(b);first.Close();if(!closed)second.Close();
        }
    }
    private sealed class LayoutWindow144:IDisposable
    {
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG message,IntPtr hwnd,uint first,uint last);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(in MSG message);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
        private readonly Thread _thread;private readonly ManualResetEventSlim _ready=new();
        internal IntPtr Handle {get;private set;}internal IntPtr Edit {get;private set;}
        internal LayoutWindow144()
        {
            _thread=new Thread(()=>
            {
                Handle=NativeMethods.CreateWindowEx(0x80,"STATIC","FloatingDock language thread fixture",0x10CF0000,220,180,600,300,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
                Edit=NativeMethods.CreateWindowEx(0,"EDIT","Language input",0x50010080,20,20,480,60,Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
                SetFocus(Edit);_ready.Set();
                while(GetMessage(out var message,IntPtr.Zero,0,0)>0)
                {if(message.message==0x8002)break;if(message.message==0x8001)SetFocus(Edit);else DispatchMessage(message);}
                NativeMethods.DestroyWindow(Handle);
            }){IsBackground=true};_thread.Start();Check(_ready.Wait(2000),"Language fixture did not start.");
        }
        internal void Activate(){NativeMethods.SetForegroundWindow(Handle);NativeMethods.PostMessage(Handle,0x8001,IntPtr.Zero,IntPtr.Zero);}
        public void Dispose(){NativeMethods.PostMessage(Handle,0x8002,IntPtr.Zero,IntPtr.Zero);_thread.Join(2000);_ready.Dispose();}
    }
}
