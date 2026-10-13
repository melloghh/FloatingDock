using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.Integration;

/// <summary>Invokes Explorer's real overflow chevron through UI Automation. Explorer owns all icon callbacks.</summary>
public static unsafe partial class SystemTrayOverflow
{
    private static IntPtr _temporarilyShownShell;
    private static IntPtr _returnFocus;
    public static string LastDiagnostic { get; private set; }="";
    public static IReadOnlyList<string> LastDiscovery { get; private set; }=Array.Empty<string>();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid,IntPtr outer,uint context,in Guid iid,out IntPtr instance);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved,uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string className,string? caption);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd,int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wparam,IntPtr lparam);
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo { public int Size,Flags;public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;public RECT CaretRect; }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW")] private static extern IntPtr CancelMode(IntPtr hwnd,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out UIntPtr result);
    private static void* Slot(IntPtr instance,int index)=>((void**)*(void**)instance)[index];
    private static int Get(IntPtr self,int slot,out IntPtr result)
    { IntPtr value=default;int hr=((delegate* unmanaged[Stdcall]<IntPtr,IntPtr*,int>)Slot(self,slot))(self,&value);result=value;return hr; }
    private static int Child(IntPtr walker,int slot,IntPtr parent,out IntPtr result)
    { IntPtr value=default;int hr=((delegate* unmanaged[Stdcall]<IntPtr,IntPtr,IntPtr*,int>)Slot(walker,slot))(walker,parent,&value);result=value;return hr; }
    private static string String(IntPtr element,int slot)
    {
        if(Get(element,slot,out var bstr)<0||bstr==IntPtr.Zero)return "";
        try{return Marshal.PtrToStringBSTR(bstr);}finally{Marshal.FreeBSTR(bstr);}
    }
    private static bool Chevron(IntPtr automation,IntPtr walker,IntPtr shell,bool invoke,List<string>? diagnostics)
    {
        IntPtr root=default;
        int hr=((delegate* unmanaged[Stdcall]<IntPtr,IntPtr,IntPtr*,int>)Slot(automation,6))(automation,shell,&root);
        if(hr<0||root==IntPtr.Zero){LastDiagnostic=$"ElementFromHandle=0x{hr:X8}";return false;}
        var pending=new Stack<(IntPtr Element,int Depth)>();pending.Push((root,0));int visited=0;
        try
        {
            while(pending.Count>0&&visited++<700)
            {
                var (element,depth)=pending.Pop();
                try
                {
                    string id=String(element,29),name=String(element,23),cls=String(element,30);
                    diagnostics?.Add($"{depth}: {id} | {name} | {cls}");
                    // Explorer appends the current action (e.g. "Hide") when
                    // the chevron receives focus. Its base accessible name stays stable.
                    bool namedChevron=new[]{"Show hidden icons","Отображать скрытые значки","Показать скрытые значки"}.Any(prefix=>name.StartsWith(prefix,StringComparison.OrdinalIgnoreCase));
                    bool match=id is "SystemTray.ChevronButton" or "ChevronButton" or "NotificationChevron" || (cls is "Button" or "SystemTray.NormalButton")&&namedChevron;
                    if(match)
                    {
                        if(!invoke)return true;
                        IntPtr pattern=default;
                        var iid=new Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059");
                        hr=((delegate* unmanaged[Stdcall]<IntPtr,int,Guid*,IntPtr*,int>)Slot(element,14))(element,10000,&iid,&pattern);
                        if(hr>=0&&pattern!=IntPtr.Zero)
                        {
                            try { hr=((delegate* unmanaged[Stdcall]<IntPtr,int>)Slot(pattern,3))(pattern);LastDiagnostic=$"Invoke=0x{hr:X8}";return hr>=0; }
                            finally { Marshal.Release(pattern); }
                        }
                        LastDiagnostic=$"GetCurrentPatternAs=0x{hr:X8}";return false;
                    }
                    if(depth>=18||Child(walker,4,element,out var child)<0)continue;
                    while(child!=IntPtr.Zero)
                    {
                        Child(walker,6,child,out var next);pending.Push((child,depth+1));child=next;
                        if(pending.Count>=700){if(child!=IntPtr.Zero)Marshal.Release(child);break;}
                    }
                }
                finally { Marshal.Release(element); }
            }
        }
        finally { foreach(var item in pending)Marshal.Release(item.Element); }
        LastDiagnostic="No chevron in the shell tree";return false;
    }
    public static IntPtr Handle
    {
        get
        {
            var modern=FindWindow("TopLevelWindowForOverflowXamlIsland",null);
            return modern!=IntPtr.Zero?modern:FindWindow("NotifyIconOverflowWindow",null);
        }
    }
    public static bool Visible=>NativeMethods.IsWindowVisible(Handle);
    public static void ReleaseTemporaryTaskbar()
    {
        var shell=Interlocked.Exchange(ref _temporarilyShownShell,IntPtr.Zero);
        if(shell!=IntPtr.Zero&&NativeMethods.IsWindow(shell))ShowWindow(shell,0);
    }
    public static void Dismiss()
    {
        if(!Visible){ReleaseTemporaryTaskbar();return;}
        var hwnd=Handle;var thread=NativeMethods.GetWindowThreadProcessId(hwnd,out _);
        var info=new GuiThreadInfo { Size=Marshal.SizeOf<GuiThreadInfo>() };
        var target=GetGUIThreadInfo(thread,ref info)&&info.Focus!=IntPtr.Zero&&NativeMethods.GetAncestor(info.Focus,NativeMethods.GA_ROOT)==hwnd?info.Focus:hwnd;
        // Send Escape to Explorer's focused input site, never inject a key into
        // the user's active application. Explorer performs its normal light-dismiss.
        PostMessage(target,0x100,new IntPtr(0x1B),IntPtr.Zero);
        PostMessage(target,0x101,new IntPtr(0x1B),IntPtr.Zero);
        CancelMode(target,0x001F,IntPtr.Zero,IntPtr.Zero,2,25,out _);
        if(target!=hwnd)CancelMode(hwnd,0x001F,IntPtr.Zero,IntPtr.Zero,2,25,out _);
        var foreground=NativeMethods.GetForegroundWindow();
        if((foreground==hwnd||foreground==FindWindow("Shell_TrayWnd",null))&&NativeMethods.IsWindowVisible(_returnFocus)&&!NativeMethods.IsIconic(_returnFocus))NativeMethods.SetForegroundWindow(_returnFocus);
        ShowWindow(hwnd,0); // capture/fullscreen dismissal must remove native pixels immediately.
        ReleaseTemporaryTaskbar();
    }
    public static bool TryOpen(int x,int y,int right,int bottom)
    {
        if(Visible)Dismiss();
        LastDiagnostic="Opening";
        _returnFocus=NativeMethods.GetForegroundWindow();
        var shell=FindWindow("Shell_TrayWnd",null);if(shell==IntPtr.Zero)return false;
        bool hidden=!NativeMethods.IsWindowVisible(shell);
        bool opened=false;
        int init=CoInitializeEx(IntPtr.Zero,0);
        IntPtr automation=default,walker=default;
        try
        {
            if(hidden)ShowWindow(shell,4); // temporary presentation only; no taskbar state or work-area changes.
            var clsid=new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E");var iid=new Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
            int hr=CoCreateInstance(clsid,IntPtr.Zero,1,iid,out automation);
            if(hr<0){LastDiagnostic=$"CoCreateInstance=0x{hr:X8}";return false;}
            hr=Get(automation,16,out walker);if(hr<0){LastDiagnostic=$"RawViewWalker=0x{hr:X8}";return false;}
            bool invoked=false;
            var discovery=new List<string>();
            for(int attempt=0;attempt<5;attempt++)
            {
                if(attempt>0){if(attempt==1)NativeMethods.SetForegroundWindow(shell);Thread.Sleep(80);}
                discovery.Clear();
                if(Chevron(automation,walker,shell,true,discovery)){invoked=true;break;}
            }
            LastDiscovery=discovery.ToArray();
            if(!invoked)return false;
            var clock=Stopwatch.StartNew();
            while(clock.ElapsedMilliseconds<1200)
            {
                var overflow=Handle;
                if(NativeMethods.IsWindowVisible(overflow)&&NativeMethods.GetWindowRect(overflow,out var rect))
                {
                    // Internal discovery leaves Explorer's own geometry intact.
                    if(right!=int.MaxValue)NativeMethods.SetWindowPos(overflow,NativeMethods.HWND_TOPMOST,Math.Min(right-rect.Width,x),Math.Min(y,bottom-rect.Height),0,0,NativeMethods.SWP_NOSIZE|NativeMethods.SWP_NOACTIVATE);
                    opened=true;if(hidden)_temporarilyShownShell=shell;
                    return true;
                }
                Thread.Sleep(25);
            }
            LastDiagnostic+="; popup did not become visible";return false;
        }
        finally
        {
            if(walker!=IntPtr.Zero)Marshal.Release(walker);if(automation!=IntPtr.Zero)Marshal.Release(automation);
            if(hidden&&!opened)ShowWindow(shell,0);
            if(init>=0)CoUninitialize();
        }
    }
    public static IReadOnlyList<string> Describe()
    {
        var result=new List<string>();IntPtr automation=default,walker=default;int init=CoInitializeEx(IntPtr.Zero,0);
        try
        {
            var clsid=new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E");var iid=new Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
            if(CoCreateInstance(clsid,IntPtr.Zero,1,iid,out automation)>=0&&Get(automation,16,out walker)>=0)Chevron(automation,walker,FindWindow("Shell_TrayWnd",null),false,result);
            return result;
        }
        finally { if(walker!=IntPtr.Zero)Marshal.Release(walker);if(automation!=IntPtr.Zero)Marshal.Release(automation);if(init>=0)CoUninitialize(); }
    }
}
