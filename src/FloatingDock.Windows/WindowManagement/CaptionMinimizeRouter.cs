using System.Runtime.InteropServices;
using FloatingDock.Windows.Native;

namespace FloatingDock.Windows.WindowManagement;

/// <summary>Routes only a complete click on a known window's native minimize button.
/// The hook lives on a sleeping message thread; no injection, mouse-move queries, or global animation changes.</summary>
public sealed class CaptionMinimizeRouter : IDisposable
{
    private delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData {public POINT Point;public uint Mouse,Flags,Time;public UIntPtr Extra;}
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int kind,HookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW")] private static extern IntPtr HitTest(IntPtr hwnd,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out UIntPtr result);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message,IntPtr hwnd,uint first,uint last);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG message,IntPtr hwnd,uint first,uint last,uint flags);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread,uint message,IntPtr w,IntPtr l);
    private readonly Thread _thread;
    private readonly HookProc _callback;
    private readonly Func<IntPtr,bool> _minimize;
    private readonly ManualResetEventSlim _ready=new();
    private IReadOnlyDictionary<long,uint> _targets=new Dictionary<long,uint>();
    private uint _threadId;
    private IntPtr _hook,_pressed;
    private bool _disposed;
    public bool Installed=>_hook!=IntPtr.Zero;
    public CaptionMinimizeRouter(Func<IntPtr,bool> minimize)
    {
        _minimize=minimize;_callback=OnMouse;
        _thread=new Thread(Run){IsBackground=true,Name="FloatingDock.CaptionClicks"};_thread.Start();_ready.Wait();
    }
    public void Update(IReadOnlyDictionary<long,uint> targets)=>Volatile.Write(ref _targets,targets);
    private IntPtr MinimizeButton(POINT point)
    {
        var window=NativeMethods.GetAncestor(WindowFromPoint(point),NativeMethods.GA_ROOT);
        var targets=Volatile.Read(ref _targets);
        if(!targets.TryGetValue(window.ToInt64(),out uint expected)||NativeMethods.IsIconic(window))return IntPtr.Zero;
        NativeMethods.GetWindowThreadProcessId(window,out uint pid);if(pid!=expected)return IntPtr.Zero;
        long packed=unchecked((uint)((point.X&0xffff)|((point.Y&0xffff)<<16)));
        return HitTest(window,0x84,IntPtr.Zero,new(packed),2,12,out var hit)!=IntPtr.Zero&&hit.ToUInt64()==8?window:IntPtr.Zero;
    }
    private IntPtr OnMouse(int code,IntPtr message,IntPtr data)
    {
        if(code>=0&&(message.ToInt64() is 0x201 or 0x202))
        {
            try
            {
                var mouse=Marshal.PtrToStructure<MouseData>(data);
                if(message.ToInt64()==0x201)
                {
                    _pressed=MinimizeButton(mouse.Point);
                    if(_pressed!=IntPtr.Zero)return new(1);
                }
                else if(_pressed!=IntPtr.Zero)
                {
                    var pressed=_pressed;_pressed=IntPtr.Zero;
                    if(MinimizeButton(mouse.Point)==pressed&&!_minimize(pressed))NativeMethods.PostMessage(pressed,0x112,new(0xF020),IntPtr.Zero);
                    return new(1); // Pair the swallowed down, including drag-off cancellation.
                }
            }
            catch { _pressed=IntPtr.Zero; } // Foreign/hung windows retain the native path.
        }
        return CallNextHookEx(_hook,code,message,data);
    }
    private void Run()
    {
        _threadId=NativeMethods.GetCurrentThreadId();
        PeekMessage(out _,IntPtr.Zero,0,0,0); // Create the queue before Dispose can post WM_QUIT.
        _hook=SetWindowsHookEx(14,_callback,GetModuleHandle(null),0);_ready.Set();
        if(_hook==IntPtr.Zero)return;
        try {while(GetMessage(out _,IntPtr.Zero,0,0)>0){} }
        finally {UnhookWindowsHookEx(_hook);_hook=IntPtr.Zero;GC.KeepAlive(_callback);}
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        Update(new Dictionary<long,uint>());PostThreadMessage(_threadId,0x12,IntPtr.Zero,IntPtr.Zero);_thread.Join();_ready.Dispose();
    }
}
